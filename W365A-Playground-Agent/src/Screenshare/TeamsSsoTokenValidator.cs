// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <inheritdoc />
public sealed class TeamsSsoTokenValidator : ISsoTokenValidator
{
    private readonly ScreenshareOptions _options;
    private readonly string? _tenantId;
    private readonly ILogger<TeamsSsoTokenValidator> _logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _oidc;

    public TeamsSsoTokenValidator(IOptions<ScreenshareOptions> options, IConfiguration config,
        ILogger<TeamsSsoTokenValidator> logger)
    {
        _options = options.Value;
        _logger = logger;
        _tenantId = FirstNonEmpty(_options.SsoTenantId, config["TokenValidation:TenantId"],
            ExtractTenantId(config["Connections:ServiceConnection:Settings:AuthorityEndpoint"]));

        if (!string.IsNullOrWhiteSpace(_tenantId))
        {
            _oidc = new ConfigurationManager<OpenIdConnectConfiguration>(
                $"https://login.microsoftonline.com/{_tenantId}/v2.0/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(), new HttpClient());
        }
    }

    public async Task<string?> ValidateAndGetOidAsync(string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) return null;
        if (_oidc is null || string.IsNullOrWhiteSpace(_options.SsoAudience))
        {
            _logger.LogWarning("TeamsSsoTokenValidator: SSO not configured (audience/tenant missing).");
            return null;
        }
        try
        {
            var config = await _oidc.GetConfigurationAsync(ct);
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = [$"https://login.microsoftonline.com/{_tenantId}/v2.0",
                                $"https://sts.windows.net/{_tenantId}/"],
                ValidateAudience = true,
                ValidAudiences = [_options.SsoAudience, $"api://{_options.SsoAudience}"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5),
                IssuerSigningKeys = config.SigningKeys,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
            };
            var principal = new JwtSecurityTokenHandler().ValidateToken(bearerToken, parameters, out _);
            return principal.FindFirst("oid")?.Value
                ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TeamsSsoTokenValidator: token validation failed.");
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? ExtractTenantId(string? authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var u) && u.Segments.Length > 1
            ? u.Segments[^1].Trim('/') : null;
}
