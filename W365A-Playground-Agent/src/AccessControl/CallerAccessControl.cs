// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Identity.Client;
using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace Microsoft.W365APlaygroundAgent.AccessControl;

/// <summary>
/// Determines whether a caller (identified by their Entra OID) is authorized to use this agent.
///
/// Authorization is granted if the caller:
///   1. Appears in the static <c>AccessControl:AllowedOids</c> list in configuration, OR
///   2. Exists as a native member of the blueprint tenant (Graph direct OID lookup), OR
///   3. Exists as a B2B guest in the blueprint tenant (Graph identities filter by home OID).
///
/// Results are cached for <see cref="AuthCacheTtlHours"/> hour(s), aligned with AAD token lifetime.
/// For immediate revocation, restart the App Service to clear the in-memory cache.
///
/// Note: this implementation uses MSAL ClientSecret auth to acquire the Graph token. For
/// production deployments using <c>UserManagedIdentity</c> instead of <c>ClientSecret</c>,
/// swap the MSAL builder for <c>ManagedIdentityCredential</c> (or <c>WithCertificate</c>) —
/// there is no client secret in that flow.
/// </summary>
public class CallerAccessControl : ICallerAccessControl
{
    // TTL aligned with AAD token lifetime and standard directory membership cache conventions.
    // Access grants/revocations (adding or removing a tenant member/guest) propagate within this window.
    private const int AuthCacheTtlHours = 1;

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CallerAccessControl> _logger;
    private readonly IConfidentialClientApplication _msalApp;
    private readonly ConcurrentDictionary<string, (bool allowed, DateTime expiry)> _authCache = new();

    public CallerAccessControl(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<CallerAccessControl> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _msalApp = ConfidentialClientApplicationBuilder
            .Create(_configuration["Connections:ServiceConnection:Settings:ClientId"]!)
            .WithClientSecret(_configuration["Connections:ServiceConnection:Settings:ClientSecret"]!)
            .WithAuthority(_configuration["Connections:ServiceConnection:Settings:AuthorityEndpoint"]!)
            .Build();
    }

    public async Task<bool> IsAuthorizedAsync(string? aadObjectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(aadObjectId))
        {
            _logger.LogWarning("AccessControl: caller has no AadObjectId — denying.");
            return false;
        }

        // (0) Cache check — avoid Graph on every turn.
        if (_authCache.TryGetValue(aadObjectId, out var cached) && DateTime.UtcNow < cached.expiry)
            return cached.allowed;

        // (1) Static OID allowlist — no network call.
        var allowedOids = _configuration.GetSection("AccessControl:AllowedOids").Get<string[]>() ?? [];
        if (Array.Exists(allowedOids, oid => oid.Equals(aadObjectId, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("AccessControl: OID={OID} matched static allowlist.", aadObjectId);
            _authCache[aadObjectId] = (true, DateTime.UtcNow.AddHours(AuthCacheTtlHours));
            return true;
        }

        try
        {
            var token = await GetGraphTokenAsync(cancellationToken);
            var http = _httpClientFactory.CreateClient("WebClient");

            // (2) Native member check: direct OID lookup in the blueprint tenant.
            using var memberReq = new HttpRequestMessage(HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(aadObjectId)}?$select=id");
            memberReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var memberResp = await http.SendAsync(memberReq, cancellationToken);
            if (memberResp.StatusCode == System.Net.HttpStatusCode.OK)
            {
                _logger.LogInformation("AccessControl: OID={OID} is a native member of the BP tenant — ALLOWED.", aadObjectId);
                _authCache[aadObjectId] = (true, DateTime.UtcNow.AddHours(AuthCacheTtlHours));
                return true;
            }

            // (3) B2B guest check: a guest's OID *in the blueprint tenant* differs from their home
            // OID, so a direct GET /users/{oid} returns 404. Search the identities collection by
            // home OID instead. Requires ConsistencyLevel:eventual + $count=true (AAD advanced query).
            var filterUrl = $"https://graph.microsoft.com/v1.0/users" +
                $"?$filter=identities/any(i:i/issuerAssignedId eq '{Uri.EscapeDataString(aadObjectId)}')" +
                $"&$select=id&$count=true";
            using var guestReq = new HttpRequestMessage(HttpMethod.Get, filterUrl);
            guestReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            guestReq.Headers.Add("ConsistencyLevel", "eventual");
            using var guestResp = await http.SendAsync(guestReq, cancellationToken);
            if (guestResp.StatusCode == System.Net.HttpStatusCode.OK)
            {
                var body = await guestResp.Content.ReadAsStringAsync(cancellationToken);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var found = doc.RootElement.TryGetProperty("@odata.count", out var countEl) && countEl.GetInt32() > 0;
                if (found)
                {
                    _logger.LogInformation("AccessControl: OID={OID} is a B2B guest in the BP tenant — ALLOWED.", aadObjectId);
                    _authCache[aadObjectId] = (true, DateTime.UtcNow.AddHours(AuthCacheTtlHours));
                    return true;
                }
            }

            _logger.LogInformation("AccessControl: OID={OID} not found in allowlist or BP tenant — DENIED.", aadObjectId);
            _authCache[aadObjectId] = (false, DateTime.UtcNow.AddHours(AuthCacheTtlHours));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AccessControl: Graph lookup failed for OID={OID} — denying.", aadObjectId);
            return false;
        }
    }

    /// <summary>
    /// Acquires an app-level Graph token using client credentials.
    /// MSAL caches the token internally and only refreshes near expiry (~1 hour lifetime).
    /// </summary>
    private async Task<string> GetGraphTokenAsync(CancellationToken cancellationToken)
    {
        var result = await _msalApp
            .AcquireTokenForClient(["https://graph.microsoft.com/.default"])
            .ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }
}
