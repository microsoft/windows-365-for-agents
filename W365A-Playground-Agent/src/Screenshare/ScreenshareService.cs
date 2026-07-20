// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Options;

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>
/// Screenshare config accessors + pure helpers. The turn-scoped operations (ARI token mint via
/// UserAuthorization.ExchangeTurnTokenAsync, and GetSessionDetails via the orchestrator's W365
/// MCP client) live in the handlers that have that context; this service centralizes the audience,
/// viewer URL, TTLs, and the computerUrl derivation so they are consistent and unit-testable.
/// </summary>
public sealed class ScreenshareService
{
    private readonly ScreenshareOptions _options;

    public ScreenshareService(IOptions<ScreenshareOptions> options) => _options = options.Value;

    public string ViewerUrl => _options.ViewerUrl;
    public TimeSpan RedeemBy => TimeSpan.FromMinutes(_options.RedeemByMinutes);

    /// <summary>Public HTTPS origin of paw's webapp (for the viewer link in the "Watch live" card); empty if unset.</summary>
    public string PublicBaseUrl => _options.PublicBaseUrl?.TrimEnd('/') ?? "";

    /// <summary>Scopes for the ARI OBO exchange (audience/.default).</summary>
    public string[] AriScopes => [$"{_options.AriAudience}/.default"];

    /// <summary>
    /// Derive the ARI computerUrl from a session's screenShareUrl by removing the "/screenshare"
    /// path segment (keeping the required api-version query). Null if the shape is unexpected.
    /// </summary>
    public static string? DeriveComputerUrl(string? screenShareUrl)
    {
        if (string.IsNullOrWhiteSpace(screenShareUrl)) return null;
        if (screenShareUrl.Contains("/screenshare?", StringComparison.OrdinalIgnoreCase))
            return screenShareUrl.Replace("/screenshare?", "?", StringComparison.OrdinalIgnoreCase);
        if (screenShareUrl.EndsWith("/screenshare", StringComparison.OrdinalIgnoreCase))
            return screenShareUrl[..^"/screenshare".Length];
        return null;
    }

    /// <summary>SessionUntil = min(now + MaxSession, bearer expiry) — the Strategy-A bound.</summary>
    public DateTimeOffset ComputeSessionUntil(DateTimeOffset now, DateTimeOffset? bearerExpiry)
    {
        var policyMax = now.AddMinutes(_options.MaxSessionMinutes);
        return bearerExpiry is { } exp && exp < policyMax ? exp : policyMax;
    }

    /// <summary>Read the "exp" claim from a JWT bearer, or null if it can't be parsed.</summary>
    public static DateTimeOffset? ReadExpiry(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        try
        {
            var t = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(jwt);
            return t.ValidTo == default ? null : new DateTimeOffset(t.ValidTo, TimeSpan.Zero);
        }
        catch { return null; }
    }
}
