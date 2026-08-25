// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>
/// In-memory store of screenshare tickets. Singleton so state is shared across the (transient)
/// agent instances. State is per-instance and resets on App Service restart — in-flight views are
/// dropped and the human re-shares. A distributed store (e.g. Redis / Azure Table) would add
/// restart + multi-instance survival.
/// </summary>
public interface IScreenshareTicketStore
{
    /// <summary>Create + store a ticket with a freshly generated opaque 256-bit id.</summary>
    ScreenshareTicket Create(NewTicket spec);

    /// <summary>Look up a ticket by id (no state change). Null if unknown.</summary>
    ScreenshareTicket? Get(string ticketId);

    /// <summary>
    /// Redeem for the ticket holder. The first successful open within RedeemBy atomically claims
    /// the ticket and creates a continuity cookie. Later redemptions require that exact cookie.
    /// </summary>
    RedeemOutcome Redeem(string ticketId, string? existingCookieId);

    /// <summary>Set a lifecycle status (from beacons / orchestrator).</summary>
    void SetStatus(string ticketId, ShareStatus status);

    /// <summary>Record a heartbeat timestamp.</summary>
    void Heartbeat(string ticketId);

    /// <summary>Invalidate a single ticket.</summary>
    void Revoke(string ticketId);

    /// <summary>Invalidate every ticket for a Cloud PC session (agent EndSession / recycle).</summary>
    int RevokeBySession(string w365SessionId);

    /// <summary>Invalidate every ticket for a conversation (conversation end / uninstall).</summary>
    int RevokeByConversation(string conversationId);

    /// <summary>Evict tickets past SessionUntil. Returns the count removed.</summary>
    int SweepExpired();

    /// <summary>
    /// True if <paramref name="w365SessionId"/> has a ticket that is still usable — <c>Offered</c> within
    /// its RedeemBy window, or <c>Redeemed</c>/<c>Live</c>/<c>Controlling</c> within SessionUntil — i.e. a
    /// "Watch live" card a viewer could still open or is actively viewing.
    /// </summary>
    bool HasRedeemableTicket(string w365SessionId);

    /// <summary>
    /// Remove tickets for <paramref name="w365SessionId"/> that are no longer an active view (anything not
    /// <c>Redeemed</c>/<c>Live</c>/<c>Controlling</c>) — called before minting a fresh offer so superseded
    /// tickets don't accumulate. Returns the count removed.
    /// </summary>
    int PurgeSupersededTickets(string w365SessionId);
}
