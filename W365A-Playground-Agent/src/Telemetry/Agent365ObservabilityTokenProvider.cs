// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.Agents.Authentication;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Tokens;

namespace Microsoft.W365APlaygroundAgent.Telemetry;

internal sealed class Agent365ObservabilityTokenProvider(
    IConnections connections,
    AgenticTokenCache cache)
{
    public async Task<string?> GetTokenAsync(string agentId, string tenantId)
    {
        if (!Guid.TryParse(agentId, out var agent) || agent == Guid.Empty
            || !Guid.TryParse(tenantId, out var tenant) || tenant == Guid.Empty)
        {
            throw new InvalidOperationException("OBS export requires valid runtime agent and tenant IDs.");
        }

        return await cache.RefreshObservabilityToken(
            agentId, tenantId, AcquireTokenAsync).ConfigureAwait(false);
    }

    private async Task<string?> AcquireTokenAsync(
        string agentId, string tenantId, string[] scopes)
    {
        if (scopes.Length == 0
            || scopes.Any(scope => !scope.EndsWith("/.default", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("OBS authentication requires resource /.default scopes.");
        }

        if (connections.GetConnection("ServiceConnection") is not IAgenticTokenProvider provider)
        {
            throw new InvalidOperationException("ServiceConnection must support agentic app-only tokens.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string token;
        try
        {
            token = await provider.GetAgenticInstanceTokenAsync(
                tenantId, agentId, scopes, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("OBS token acquisition timed out.");
        }
        catch (MsalException exception)
        {
            throw new InvalidOperationException(
                $"OBS token acquisition failed ({exception.ErrorCode}).");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("OBS token acquisition failed.");
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("OBS credential processing failed.");
        }

        ValidateToken(token, agentId, tenantId, scopes);
        return token;
    }

    internal static void ValidateToken(
        string token, string agentId, string tenantId, string[] scopes)
    {
        // Validate the contract of a token returned by the trusted credential provider.
        // This is not an inbound JWT signature validator.
        using var document = ReadPayload(token);
        var claims = document.RootElement;
        if (claims.ValueKind != JsonValueKind.Object)
        {
            throw InvalidToken();
        }

        var hasClientId = false;
        foreach (var name in new[] { "appid", "azp" })
        {
            if (claims.TryGetProperty(name, out var clientId))
            {
                hasClientId = true;
                if (clientId.ValueKind != JsonValueKind.String
                    || !string.Equals(clientId.GetString(), agentId, StringComparison.OrdinalIgnoreCase))
                {
                    throw InvalidToken();
                }
            }
        }

        var audience = ReadString(claims, "aud");
        var matchesAudience = audience is not null && scopes.Any(scope =>
            scope.EndsWith("/.default", StringComparison.Ordinal)
            && string.Equals(
                NormalizeAudience(scope[..^"/.default".Length]),
                NormalizeAudience(audience),
                StringComparison.OrdinalIgnoreCase));
        var hasIdentityType = claims.TryGetProperty("idtyp", out var identityType);
        var hasRoles = claims.TryGetProperty("roles", out var roles);
        if (!hasClientId || !matchesAudience
            || !string.Equals(ReadString(claims, "tid"), tenantId, StringComparison.OrdinalIgnoreCase)
            || claims.TryGetProperty("scp", out _)
            || (hasIdentityType && (identityType.ValueKind != JsonValueKind.String
                || identityType.GetString() != "app"))
            || (hasRoles && (roles.ValueKind != JsonValueKind.Array
                || roles.EnumerateArray().Any(role => role.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(role.GetString())))))
        {
            throw InvalidToken();
        }

        var oid = ReadString(claims, "oid");
        var subject = ReadString(claims, "sub");
        var appOnly = hasIdentityType
            || (hasRoles && roles.GetArrayLength() > 0)
            || (!string.IsNullOrWhiteSpace(oid) && oid == subject);
        if (!appOnly
            || !claims.TryGetProperty("exp", out var expiry)
            || expiry.ValueKind != JsonValueKind.Number
            || !expiry.TryGetInt64(out var seconds))
        {
            throw InvalidToken();
        }

        DateTimeOffset expiresAt;
        try
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InvalidToken();
        }

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            throw InvalidToken();
        }
    }

    private static JsonDocument ReadPayload(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw InvalidToken();
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw InvalidToken();
        }

        try
        {
            return JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or ArgumentException)
        {
            // Parser diagnostics can contain token text; do not retain the inner exception.
            throw InvalidToken();
        }
    }

    private static string? ReadString(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizeAudience(string audience)
    {
        var value = audience.StartsWith("api://", StringComparison.OrdinalIgnoreCase)
            ? audience[6..]
            : audience;
        return Guid.TryParse(value, out var id) ? id.ToString("D") : audience;
    }

    private static InvalidOperationException InvalidToken() =>
        new("OBS credential provider returned an invalid app-only token.");
}
