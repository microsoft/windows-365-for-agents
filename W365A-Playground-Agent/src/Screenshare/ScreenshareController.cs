// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>
/// Screenshare viewer page + redeem + state endpoints. The viewer requires an interactive Entra
/// sign-in (OIDC): endpoints are [AllowAnonymous] to the bot-JWT scheme and instead authenticate the
/// opener via the "Cookies" scheme (challenging OpenIdConnect when unauthenticated), then enforce
/// opener==hirer here. The agentic Teams surface can't host task-module dialogs or getAuthToken, so
/// the viewer opens as a top-level browser page launched by the card's Action.OpenUrl.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class ScreenshareController(
    IScreenshareTicketStore store,
    IOptions<ScreenshareOptions> options, IWebHostEnvironment env,
    ILogger<ScreenshareController> logger) : ControllerBase
{
    private readonly ScreenshareOptions _options = options.Value;
    private const string RedeemCookie = "ss_redeem";

    [HttpGet("/screenshare")]
    public async Task<IActionResult> ViewerPage()
    {
        var ticketForLog = Request.Query.TryGetValue("ticket", out var tq) && !string.IsNullOrEmpty(tq)
            ? Mask(tq.ToString()) : "(none)";

        // Require interactive sign-in so the opener proves they are the bound hirer. In Development a
        // configured DevBypassOid short-circuits this so the flow is testable without an app-reg.
        if (!IsDevBypass())
        {
            var auth = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (!auth.Succeeded)
            {
                logger.LogInformation("[Screenshare] viewer page: sign-in required, challenging OIDC ticket={Ticket}", ticketForLog);
                // Round-trip back to this exact viewer URL (carries the ticket) after sign-in.
                var returnUrl = Request.Path + Request.QueryString;
                return Challenge(
                    new AuthenticationProperties { RedirectUri = returnUrl },
                    OpenIdConnectDefaults.AuthenticationScheme);
            }
            var openerOid = auth.Principal?.FindFirst("oid")?.Value
                ?? auth.Principal?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
            logger.LogInformation("[Screenshare] viewer page served ticket={Ticket} oid={Oid}", ticketForLog, openerOid ?? "(none)");
        }
        else
        {
            logger.LogInformation("[Screenshare] viewer page served (dev bypass) ticket={Ticket}", ticketForLog);
        }

        // Top-level page (NOT a Teams iframe): deny framing entirely, but still allow the SDK's own
        // nested CDN iframe (frame-src) + scripts. No Teams frame-ancestors / TeamsJS needed anymore.
        var cdn = Uri.TryCreate(_options.ViewerUrl, UriKind.Absolute, out var v)
            ? v.GetLeftPart(UriPartial.Authority)
            : "https://packages.global.cloudinferenceplatform.azure.com";
        Response.Headers["Content-Security-Policy"] = string.Join("; ",
            "default-src 'self'",
            "frame-ancestors 'none'",
            $"script-src 'self' 'unsafe-inline' {cdn}",
            $"frame-src {cdn}",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "connect-src 'self'");
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Content(ScreenshareViewerPage.Html, "text/html");
    }

    [HttpGet("/screenshare/switch-account")]
    public async Task<IActionResult> SwitchAccount([FromQuery] string? ticket)
    {
        var returnUrl = "/screenshare" + (string.IsNullOrEmpty(ticket) ? "" : $"?ticket={Uri.EscapeDataString(ticket)}");
        if (IsDevBypass()) return Redirect(returnUrl);
        // Clear the local cookie so the re-challenge can't silently reuse the wrong account, then force
        // the Entra account picker so the opener can choose their hirer identity.
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        logger.LogInformation("[Screenshare] switch-account requested ticket={Ticket}",
            string.IsNullOrEmpty(ticket) ? "(none)" : Mask(ticket));
        var props = new AuthenticationProperties { RedirectUri = returnUrl };
        props.Items["prompt"] = "select_account";
        return Challenge(props, OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpPost("/api/screenshare/session")]
    public async Task<IActionResult> Redeem([FromBody] RedeemRequest? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Ticket)) return BadRequest();
        var oid = await ResolveOpenerOidAsync(ct);
        if (oid is null)
        {
            logger.LogWarning("[Screenshare] redeem denied: no opener identity (sign-in unresolved) ticket={Ticket}", Mask(body.Ticket));
            return Unauthorized();
        }

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
        if (oid is null && !cookieOk)
        {
            logger.LogWarning("[Screenshare] state denied: unauthenticated ticket={Ticket}", Mask(body.Ticket));
            return Unauthorized();
        }
        if (oid is not null && !string.Equals(oid, t.HumanOid, StringComparison.OrdinalIgnoreCase) && !cookieOk)
        {
            logger.LogWarning("[Screenshare] state denied: opener {Oid} != hirer ticket={Ticket}", oid, Mask(body.Ticket));
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
                    logger.LogInformation("[Screenshare] view ended ticket={Ticket} oid={Oid} reason={Reason} visibility={Vis} lastedSec={Lasted}",
                        Mask(body.Ticket), oid ?? "(cookie)", body.Reason ?? "(none)", body.Visibility ?? "(none)", lasted);
                }
                else
                {
                    logger.LogInformation("[Screenshare] view {Status} ticket={Ticket} oid={Oid} sdk={Sdk}",
                        m, Mask(body.Ticket), oid ?? "(cookie)", body.SdkStatus);
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

    private async Task<string?> ResolveOpenerOidAsync(CancellationToken ct)
    {
        // DEV-ONLY bypass so the flow is testable locally before the app-reg / sign-in exists.
        if (IsDevBypass())
            return _options.DevBypassOid;

        // The opener authenticated interactively via OIDC; their identity lives in the cookie session.
        var auth = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!auth.Succeeded || auth.Principal is null) return null;
        return auth.Principal.FindFirst("oid")?.Value
            ?? auth.Principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
    }

    private bool IsDevBypass() =>
        env.IsDevelopment() && !string.IsNullOrWhiteSpace(_options.DevBypassOid);

    private static string Mask(string ticket) => ScreenshareService.MaskTicket(ticket);
}
