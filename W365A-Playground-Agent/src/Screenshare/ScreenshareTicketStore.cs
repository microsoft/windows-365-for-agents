// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <inheritdoc />
public sealed class ScreenshareTicketStore : IScreenshareTicketStore
{
    private readonly ConcurrentDictionary<string, ScreenshareTicket> _tickets = new(StringComparer.Ordinal);

    // No background timer runs in this in-memory store, so expired tickets are reclaimed opportunistically:
    // once the map grows past this many entries, Create() runs a full SweepExpired() pass.
    private const int SweepThreshold = 100;

    public ScreenshareTicket Create(NewTicket spec)
    {
        var now = DateTimeOffset.UtcNow;
        var ticket = new ScreenshareTicket
        {
            TicketId = NewOpaqueId(),
            ComputerUrl = spec.ComputerUrl,
            AriToken = spec.AriToken,
            ViewerUrl = spec.ViewerUrl,
            Mode = spec.Mode,
            Scope = spec.Scope,
            ConversationId = spec.ConversationId,
            HumanOid = spec.HumanOid,
            W365SessionId = spec.W365SessionId,
            CreatedAt = now,
            RedeemByUtc = now + spec.RedeemBy,
            SessionUntilUtc = spec.SessionUntilUtc,
            AriTokenExpiryUtc = spec.AriTokenExpiryUtc,
            Status = ShareStatus.Offered,
            LastHeartbeatAt = now,
        };
        _tickets[ticket.TicketId] = ticket;

        // Opportunistic cleanup (amortized): a full scan only fires on the rare insert that crosses the
        // threshold, bounding the store to roughly the tickets created within one SessionUntil window.
        if (_tickets.Count > SweepThreshold)
            SweepExpired();

        return ticket;
    }

    public ScreenshareTicket? Get(string ticketId) =>
        _tickets.TryGetValue(ticketId, out var t) ? t : null;

    public RedeemOutcome Redeem(string ticketId, string openerOid, string? existingCookieId)
    {
        if (!_tickets.TryGetValue(ticketId, out var t))
            return RedeemOutcome.Fail(RedeemFailure.NotFound);

        lock (t)
        {
            var now = DateTimeOffset.UtcNow;
            if (t.Status is ShareStatus.Revoked or ShareStatus.Ended)
                return RedeemOutcome.Fail(RedeemFailure.Revoked);
            if (now > t.SessionUntilUtc)
            {
                t.Status = ShareStatus.Expired;
                return RedeemOutcome.Fail(RedeemFailure.Expired);
            }
            // The interactive sign-in is the authoritative gate: the opener must be the bound hirer.
            if (!string.Equals(openerOid, t.HumanOid, StringComparison.OrdinalIgnoreCase))
                return RedeemOutcome.Fail(RedeemFailure.WrongHuman);

            if (t.Status == ShareStatus.Offered)
            {
                if (now > t.RedeemByUtc)
                {
                    t.Status = ShareStatus.Expired;
                    return RedeemOutcome.Fail(RedeemFailure.Expired);
                }
                t.Status = ShareStatus.Redeemed;
                t.RedeemedAt = now;
                t.RedeemCookieId = NewOpaqueId();
            }
            // else: refresh by the same verified hirer — reuse the existing cookie.

            t.LastHeartbeatAt = now;
            return new RedeemOutcome(true, RedeemFailure.None, t, t.RedeemCookieId);
        }
    }

    public void SetStatus(string ticketId, ShareStatus status)
    {
        if (_tickets.TryGetValue(ticketId, out var t))
            lock (t) { if (t.Status is not (ShareStatus.Revoked or ShareStatus.Ended)) t.Status = status; }
    }

    public void Heartbeat(string ticketId)
    {
        if (_tickets.TryGetValue(ticketId, out var t))
            lock (t) t.LastHeartbeatAt = DateTimeOffset.UtcNow;
    }

    public void Revoke(string ticketId)
    {
        if (_tickets.TryGetValue(ticketId, out var t))
            lock (t) t.Status = ShareStatus.Revoked;
    }

    public int RevokeBySession(string w365SessionId) => RevokeWhere(t =>
        string.Equals(t.W365SessionId, w365SessionId, StringComparison.OrdinalIgnoreCase));

    public int RevokeByConversation(string conversationId) => RevokeWhere(t =>
        string.Equals(t.ConversationId, conversationId, StringComparison.Ordinal));

    public int SweepExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var kvp in _tickets)
            if (now > kvp.Value.SessionUntilUtc && _tickets.TryRemove(kvp.Key, out _)) removed++;
        return removed;
    }

    public bool HasRedeemableTicket(string w365SessionId)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var t in _tickets.Values)
        {
            if (!string.Equals(t.W365SessionId, w365SessionId, StringComparison.OrdinalIgnoreCase)) continue;
            lock (t)
            {
                if (now > t.SessionUntilUtc) continue;
                switch (t.Status)
                {
                    case ShareStatus.Redeemed:
                    case ShareStatus.Live:
                    case ShareStatus.Controlling:
                        return true;
                    case ShareStatus.Offered when now <= t.RedeemByUtc:
                        return true;
                }
            }
        }
        return false;
    }

    public int PurgeSupersededTickets(string w365SessionId)
    {
        var removed = 0;
        foreach (var kvp in _tickets)
        {
            var t = kvp.Value;
            if (!string.Equals(t.W365SessionId, w365SessionId, StringComparison.OrdinalIgnoreCase)) continue;
            bool active;
            lock (t) active = t.Status is ShareStatus.Redeemed or ShareStatus.Live or ShareStatus.Controlling;
            if (!active && _tickets.TryRemove(kvp.Key, out _)) removed++;
        }
        return removed;
    }

    private int RevokeWhere(Func<ScreenshareTicket, bool> predicate)
    {
        var n = 0;
        foreach (var t in _tickets.Values)
            if (predicate(t)) lock (t) { t.Status = ShareStatus.Revoked; n++; }
        return n;
    }

    private static string NewOpaqueId()
    {
        Span<byte> bytes = stackalloc byte[32]; // 256-bit
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }
}
