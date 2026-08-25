// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>
/// Screenshare viewer page + redeem + state endpoints. They are anonymous to the bot-JWT scheme:
/// the opaque ticket is the initial capability, the first successful redemption atomically claims
/// it, and a separate HttpOnly cookie binds later requests to that browser.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class ScreenshareController(
    IScreenshareTicketStore store,
    IOptions<ScreenshareOptions> options,
    ILogger<ScreenshareController> logger) : ControllerBase
{
    private readonly ScreenshareOptions _options = options.Value;
    private const string RedeemCookie = "ss_redeem";

    [HttpGet("/screenshare")]
    public IActionResult ViewerPage()
    {
        var ticketForLog = Request.Query.TryGetValue("ticket", out var tq) && !string.IsNullOrEmpty(tq)
            ? Mask(tq.ToString()) : "(none)";

        logger.LogInformation("[Screenshare] viewer page served ticket={Ticket}", ticketForLog);

        // Top-level page (NOT a Teams iframe): deny framing entirely, but still allow the SDK's own
        // nested CDN iframe (frame-src) + scripts. No Teams frame-ancestors / TeamsJS needed anymore.
        var cdn = Uri.TryCreate(_options.ViewerUrl, UriKind.Absolute, out var v)
            ? v.GetLeftPart(UriPartial.Authority)
            : "https://packages.global.cloudinferenceplatform.azure.com";
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        Response.Headers["Content-Security-Policy"] = string.Join("; ",
            "default-src 'self'",
            "frame-ancestors 'none'",
            $"script-src 'self' 'nonce-{nonce}' {cdn}",
            $"frame-src {cdn}",
            $"style-src 'self' 'nonce-{nonce}'",
            "img-src 'self' data:",
            "connect-src 'self'");
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Content(ScreenshareViewerPage.Render(nonce), "text/html");
    }

    [HttpPost("/api/screenshare/session")]
    public IActionResult Redeem([FromBody] RedeemRequest? body)
    {
        if (string.IsNullOrWhiteSpace(body?.Ticket))
        {
            return BadRequest();
        }

        var existingCookie = Request.Cookies[RedeemCookie];
        var outcome = store.Redeem(body.Ticket, existingCookie);
        logger.LogInformation("[Screenshare] redeem ticket={Ticket} continuity={Continuity} success={Ok} reason={Reason}",
            Mask(body.Ticket), existingCookie is not null, outcome.Success, outcome.Reason);

        if (!outcome.Success)
        {
            return outcome.Reason == RedeemFailure.WrongViewer
                ? StatusCode(StatusCodes.Status403Forbidden)
                : StatusCode(StatusCodes.Status410Gone, new { reason = outcome.Reason.ToString() });
        }

        var t = outcome.Ticket!;
        if (outcome.RedeemCookieId is not null)
        {
            Response.Cookies.Append(RedeemCookie, outcome.RedeemCookieId, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax,
                Path = "/api/screenshare", Expires = t.SessionUntilUtc,
            });
        }

        return Ok(new SessionResponse(t.ComputerUrl, t.AriToken, t.ViewerUrl,
            t.Mode.ToString().ToLowerInvariant()));
    }

    [HttpPost("/api/screenshare/state")]
    public IActionResult State([FromBody] StateRequest? body)
    {
        if (string.IsNullOrWhiteSpace(body?.Ticket))
        {
            return BadRequest();
        }

        var t = store.Get(body.Ticket);
        if (t is null)
        {
            return Ok(new StateResponse("ended"));
        }

        var cookieOk = Request.Cookies.TryGetValue(RedeemCookie, out var c)
            && !string.IsNullOrEmpty(t.RedeemCookieId)
            && string.Equals(c, t.RedeemCookieId, StringComparison.Ordinal);
        if (!cookieOk)
        {
            logger.LogWarning("[Screenshare] state denied: viewer cookie missing or mismatched ticket={Ticket}", Mask(body.Ticket));
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var prev = t.Status;
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
            // Log only real transitions (SetStatus ignores terminal Revoked/Ended) — not every 12s heartbeat.
            if (prev is not (ShareStatus.Revoked or ShareStatus.Ended) && m != prev)
            {
                if (m is ShareStatus.Ended)
                {
                    var lasted = t.RedeemedAt is { } r ? (int)(DateTimeOffset.UtcNow - r).TotalSeconds : -1;
                    logger.LogInformation("[Screenshare] view ended ticket={Ticket} reason={Reason} visibility={Vis} lastedSec={Lasted}",
                        Mask(body.Ticket), body.Reason ?? "(none)", body.Visibility ?? "(none)", lasted);
                }
                else
                {
                    logger.LogInformation("[Screenshare] view {Status} ticket={Ticket} sdk={Sdk}",
                        m, Mask(body.Ticket), body.SdkStatus);
                }
            }
        }
        store.Heartbeat(body.Ticket);

        var cur = store.Get(body.Ticket);
        var directive = cur is null ? "ended"
            : cur.Status == ShareStatus.Revoked ? "revoked"
            : cur.Status == ShareStatus.Ended || DateTimeOffset.UtcNow > cur.SessionUntilUtc ? "ended"
            : "continue";
        return Ok(new StateResponse(directive));
    }

    private static string Mask(string ticket) => ScreenshareService.MaskTicket(ticket);
}
