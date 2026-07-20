// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>
/// Screenshare viewer page + redeem + state endpoints. Self-authenticated via the Teams getAuthToken
/// (SSO); endpoints are [AllowAnonymous] to the app's bot-JWT scheme and enforce opener==hirer here.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class ScreenshareController(
    IScreenshareTicketStore store, ISsoTokenValidator sso,
    IOptions<ScreenshareOptions> options, IWebHostEnvironment env,
    ILogger<ScreenshareController> logger) : ControllerBase
{
    private readonly ScreenshareOptions _options = options.Value;
    private const string RedeemCookie = "ss_redeem";

    [HttpGet("/screenshare")]
    public ContentResult ViewerPage()
    {
        // The page runs inside the Teams dialog iframe (frame-ancestors) and loads the SDK + its nested
        // viewer iframe from the CDN (script-src / frame-src). It makes only same-origin API calls; the
        // nested CDN iframe does ARI/ACS in its own context, so our page needs no connect-src for those.
        // Deliberately NO X-Frame-Options — it would conflict with frame-ancestors and block Teams.
        var cdn = Uri.TryCreate(_options.ViewerUrl, UriKind.Absolute, out var v)
            ? v.GetLeftPart(UriPartial.Authority)
            : "https://packages.global.cloudinferenceplatform.azure.com";
        Response.Headers["Content-Security-Policy"] = string.Join("; ",
            "default-src 'self'",
            "frame-ancestors https://teams.microsoft.com https://*.teams.microsoft.com https://*.cloud.microsoft https://*.office.com https://*.office365.com",
            $"script-src 'self' 'unsafe-inline' https://res.cdn.office.net {cdn}",
            $"frame-src {cdn}",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "connect-src 'self'");
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Content(ScreenshareViewerPage.Html, "text/html");
    }

    [HttpPost("/api/screenshare/session")]
    public async Task<IActionResult> Redeem([FromBody] RedeemRequest? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Ticket)) return BadRequest();
        var oid = await ResolveOpenerOidAsync(ct);
        if (oid is null) return Unauthorized();

        var outcome = store.Redeem(body.Ticket, oid, Request.Cookies[RedeemCookie]);
        logger.LogInformation("[Screenshare] redeem ticket={Ticket} oid={Oid} success={Ok} reason={Reason}",
            Mask(body.Ticket), oid, outcome.Success, outcome.Reason);

        if (!outcome.Success)
            return outcome.Reason == RedeemFailure.WrongHuman
                ? StatusCode(StatusCodes.Status403Forbidden)
                : StatusCode(StatusCodes.Status410Gone, new { reason = outcome.Reason.ToString() });

        var t = outcome.Ticket!;
        if (outcome.RedeemCookieId is not null)
            Response.Cookies.Append(RedeemCookie, outcome.RedeemCookieId, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax,
                Path = "/api/screenshare", Expires = t.SessionUntilUtc,
            });

        return Ok(new SessionResponse(t.ComputerUrl, t.AriToken, t.ViewerUrl,
            t.Mode.ToString().ToLowerInvariant()));
    }

    [HttpPost("/api/screenshare/state")]
    public async Task<IActionResult> State([FromBody] StateRequest? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Ticket)) return BadRequest();
        var t = store.Get(body.Ticket);
        if (t is null) return Ok(new StateResponse("ended"));

        var oid = await ResolveOpenerOidAsync(ct);
        var cookieOk = Request.Cookies.TryGetValue(RedeemCookie, out var c) && c == t.RedeemCookieId;
        if (oid is null && !cookieOk) return Unauthorized();
        if (oid is not null && !string.Equals(oid, t.HumanOid, StringComparison.OrdinalIgnoreCase) && !cookieOk)
            return StatusCode(StatusCodes.Status403Forbidden);

        var mapped = body.SdkStatus?.ToLowerInvariant() switch
        {
            "connected" => ShareStatus.Live,
            "controlling" => ShareStatus.Controlling,
            "view-only" => ShareStatus.Live,
            "disconnected" => ShareStatus.Ended,
            _ => (ShareStatus?)null,
        };
        if (mapped is { } m)
        {
            store.SetStatus(body.Ticket, m);
            if (m is ShareStatus.Controlling or ShareStatus.Live) // audit control state
                logger.LogInformation("[Screenshare] state ticket={Ticket} oid={Oid} sdk={Sdk}",
                    Mask(body.Ticket), oid ?? "(cookie)", body.SdkStatus);
        }
        store.Heartbeat(body.Ticket);

        var cur = store.Get(body.Ticket);
        var directive = cur is null ? "ended"
            : cur.Status == ShareStatus.Revoked ? "revoked"
            : cur.Status == ShareStatus.Ended || DateTimeOffset.UtcNow > cur.SessionUntilUtc ? "ended"
            : "continue";
        return Ok(new StateResponse(directive));
    }

    private async Task<string?> ResolveOpenerOidAsync(CancellationToken ct)
    {
        // DEV-ONLY bypass so the flow is testable locally before the Teams app-reg exists.
        if (env.IsDevelopment() && !string.IsNullOrWhiteSpace(_options.DevBypassOid))
            return _options.DevBypassOid;

        var auth = Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim() : null;
        return await sso.ValidateAndGetOidAsync(token, ct);
    }

    private static string Mask(string ticket) =>
        ticket.Length <= 8 ? "****" : $"{ticket[..4]}\u2026{ticket[^4..]}";
}
