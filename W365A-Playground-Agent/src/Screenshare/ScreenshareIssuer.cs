// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>
/// Creates a first-redeemer screenshare ticket during the agentic offer turn (mint-at-offer).
/// ARI token minting happens here, inside the proven agentic turn, so the later viewer open
/// (the card's Action.OpenUrl) needs no agentic auth.
/// </summary>
public interface IScreenshareIssuer
{
    Task<ScreenshareTicket?> CreateOfferAsync(
        Func<string[], CancellationToken, Task<string?>> mintAri,
        string conversationId, string screenShareUrl,
        string w365SessionId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class ScreenshareIssuer(
    IScreenshareTicketStore store, ScreenshareService svc,
    ILogger<ScreenshareIssuer> logger) : IScreenshareIssuer
{
    public async Task<ScreenshareTicket?> CreateOfferAsync(
        Func<string[], CancellationToken, Task<string?>> mintAri,
        string conversationId, string screenShareUrl,
        string w365SessionId, CancellationToken ct)
    {
        var computerUrl = ScreenshareService.DeriveComputerUrl(screenShareUrl);
        if (computerUrl is null)
        {
            logger.LogWarning("[Screenshare] offer skipped: unexpected screenShareUrl shape.");
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
            conversationId, w365SessionId, svc.RedeemBy,
            svc.ComputeSessionUntil(now, tokenExpiry), tokenExpiry));

        logger.LogInformation("[Screenshare] offer ticket created session={Session} ticket={Ticket} scope={Scope} redeemByUtc={RedeemBy:o} untilUtc={Until:o}",
            w365SessionId, ScreenshareService.MaskTicket(ticket.TicketId), ticket.Scope, ticket.RedeemByUtc, ticket.SessionUntilUtc);
        return ticket;
    }
}
