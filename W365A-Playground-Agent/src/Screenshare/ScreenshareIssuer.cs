// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>
/// Creates a hirer-bound screenshare ticket during the agentic offer turn (mint-at-offer). All
/// agentic work (ARI token mint + hirer resolution) happens here, inside the proven agentic turn,
/// so the later viewer open (the card's Action.OpenUrl) needs no agentic auth. Returns null (offer
/// skipped) if any prerequisite fails — notably an unresolved hirer, which fails closed.
/// </summary>
public interface IScreenshareIssuer
{
    Task<ScreenshareTicket?> CreateOfferAsync(
        Func<string[], CancellationToken, Task<string?>> mintAri,
        string agentInstanceId, string conversationId,
        string screenShareUrl, string w365SessionId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class ScreenshareIssuer(
    IScreenshareTicketStore store, IHirerResolver hirer, ScreenshareService svc,
    ILogger<ScreenshareIssuer> logger) : IScreenshareIssuer
{
    public async Task<ScreenshareTicket?> CreateOfferAsync(
        Func<string[], CancellationToken, Task<string?>> mintAri,
        string agentInstanceId, string conversationId,
        string screenShareUrl, string w365SessionId, CancellationToken ct)
    {
        var computerUrl = ScreenshareService.DeriveComputerUrl(screenShareUrl);
        if (computerUrl is null)
        {
            logger.LogWarning("[Screenshare] offer skipped: unexpected screenShareUrl shape.");
            return null;
        }

        var hirerOid = string.IsNullOrWhiteSpace(agentInstanceId)
            ? null
            : await hirer.ResolveHirerOidAsync(agentInstanceId, ct);
        if (string.IsNullOrEmpty(hirerOid))
        {
            logger.LogWarning("[Screenshare] offer skipped: hirer could not be resolved (fail closed).");
            return null;
        }

        var token = await mintAri(svc.AriScopes, ct);
        if (string.IsNullOrEmpty(token))
        {
            logger.LogWarning("[Screenshare] offer skipped: ARI token mint failed.");
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var tokenExpiry = ScreenshareService.ReadExpiry(token);
        // Drop any superseded tickets for this session (expired/ended/revoked offers) before minting a
        // fresh one, so re-offers don't accumulate orphans in the in-memory store.
        var purged = store.PurgeSupersededTickets(w365SessionId);
        if (purged > 0)
        {
            logger.LogInformation("[Screenshare] purged {Count} superseded ticket(s) for session {Session}.", purged, w365SessionId);
        }

        var ticket = store.Create(new NewTicket(
            computerUrl, token, svc.ViewerUrl, ShareMode.Interactive, ShareScope.SeeControl,
            conversationId, hirerOid, w365SessionId, svc.RedeemBy,
            svc.ComputeSessionUntil(now, tokenExpiry), tokenExpiry));

        // hirer is the Entra object id (oid) — NEVER the UPN (compliance). Ticket id masked (first4…last4).
        logger.LogInformation("[Screenshare] offer ticket created session={Session} ticket={Ticket} hirer={Hirer} scope={Scope} redeemByUtc={RedeemBy:o} untilUtc={Until:o}",
            w365SessionId, ScreenshareService.MaskTicket(ticket.TicketId), hirerOid, ticket.Scope, ticket.RedeemByUtc, ticket.SessionUntilUtc);
        return ticket;
    }
}
