// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

using Azure.Core;
using Azure.Identity;

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>Resolves the human who provisioned ("hired") a digital worker.</summary>
public interface IHirerResolver
{
    /// <summary>
    /// Returns the oid of the hiring human = the owner of the digital worker's ServiceIdentity,
    /// via Microsoft Graph (<c>GET /servicePrincipals/{agentInstanceId}/owners</c>). Null if it
    /// can't be resolved. Cached per instance with a bounded TTL (ownership is mutable).
    /// </summary>
    Task<string?> ResolveHirerOidAsync(string agentInstanceId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class GraphHirerResolver : IHirerResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly string[] GraphScope = ["https://graph.microsoft.com/.default"];

    private readonly ClientSecretCredential? _credential;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GraphHirerResolver> _logger;
    private readonly ConcurrentDictionary<string, (string? Oid, DateTimeOffset Expiry)> _cache = new();

    public GraphHirerResolver(IConfiguration config, IHttpClientFactory httpFactory,
        ILogger<GraphHirerResolver> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;

        var s = config.GetSection("Connections:ServiceConnection:Settings");
        var clientId = s["ClientId"];
        var secret = s["ClientSecret"];
        var tenantId = ExtractTenantId(s["AuthorityEndpoint"]) ?? config["TokenValidation:TenantId"];

        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(secret)
            && !string.IsNullOrWhiteSpace(tenantId))
        {
            _credential = new ClientSecretCredential(tenantId, clientId, secret);
        }
        else
        {
            // Prod may use UserManagedIdentity (no secret) — support is a FUTURE item; until then
            // hirer resolution is unavailable and the share offer is denied (fail closed).
            _logger.LogWarning("GraphHirerResolver: ServiceConnection client credentials unavailable; "
                + "hirer resolution disabled (shares will be denied).");
        }
    }

    public async Task<string?> ResolveHirerOidAsync(string agentInstanceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentInstanceId))
        {
            return null;
        }

        if (_cache.TryGetValue(agentInstanceId, out var e) && e.Expiry > DateTimeOffset.UtcNow)
        {
            return e.Oid;
        }

        if (_credential is null)
        {
            return null;
        }

        try
        {
            var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScope), ct);
            var http = _httpFactory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/servicePrincipals/{Uri.EscapeDataString(agentInstanceId)}/owners");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                _logger.LogWarning("GraphHirerResolver: owners lookup for {Instance} returned {Status}.",
                    agentInstanceId, status);
                // Don't cache the negative result on transient failures (throttling / server errors) so a
                // later offer can retry; only cache null for definitive client errors (e.g. 403/404).
                return IsTransientStatus(status) ? null : CacheAndReturn(agentInstanceId, null);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("value", out var owners))
            {
                // The hirer is the human (user) owner of the ServiceIdentity. App-only
                // Application.Read.All returns the owner's id + @odata.type but not the UPN
                // (which needs a broader directory scope) — the oid is all we need for the
                // opener==hirer check, so match on @odata.type == user and take the id.
                foreach (var o in owners.EnumerateArray())
                {
                    if (o.TryGetProperty("@odata.type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && string.Equals(type.GetString(), "#microsoft.graph.user", StringComparison.OrdinalIgnoreCase)
                        && o.TryGetProperty("id", out var id))
                    {
                        return CacheAndReturn(agentInstanceId, id.GetString());
                    }
                }
            }
            _logger.LogWarning("GraphHirerResolver: no user owner found for {Instance} (share will be denied).", agentInstanceId);
            return CacheAndReturn(agentInstanceId, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GraphHirerResolver: failed to resolve hirer for {Instance}.", agentInstanceId);
            return null; // don't cache transient failures
        }
    }

    private string? CacheAndReturn(string key, string? oid)
    {
        _cache[key] = (oid, DateTimeOffset.UtcNow + CacheTtl);
        return oid;
    }

    // Transient Graph failures (request timeout, throttling, server errors) shouldn't be cached as a
    // negative result — a later offer should be free to retry rather than fail closed for the full TTL.
    private static bool IsTransientStatus(int status) =>
        status is 408 or 429 || status >= 500;

    private static string? ExtractTenantId(string? authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var u) && u.Segments.Length > 1
            ? u.Segments[^1].Trim('/')
            : null;
}
