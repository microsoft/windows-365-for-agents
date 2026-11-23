// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace Microsoft.W365APlaygroundAgent.Auth;

internal sealed record McpTokenKey(
    string Tenant, string Caller, string Agent, string Subject, string Scope);

internal sealed record McpAccessToken(
    string Value, DateTimeOffset ExpiresAt, string Audience, DateTimeOffset? NotBefore)
{
    public override string ToString() => $"MCP token for {Audience}, expires {ExpiresAt:O}";
}

internal sealed class McpTokenCache(TimeProvider clock, ILogger<McpTokenCache> logger)
{
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    internal const int Capacity = 1000;
    private static readonly Meter Meter = new("W365APlaygroundAgent");
    private static readonly Counter<long> Renewals = Meter.CreateCounter<long>("mcp.auth.token.renewals");
    private static readonly Histogram<double> Remaining = Meter.CreateHistogram<double>("mcp.auth.token.remaining", "s");
    private readonly ConcurrentDictionary<McpTokenKey, Entry> _entries = new();
    private readonly object _admissionGate = new();

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public McpAccessToken? Token;
        public long LastUsed;
        public bool Retired;
    }

    internal async Task<McpAccessToken> GetAsync(
        McpTokenKey key, Func<CancellationToken, Task<string>> acquire,
        string? initialToken, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = GetEntry(key);
            Interlocked.Exchange(ref entry.LastUsed, clock.GetUtcNow().UtcTicks);
            var token = Volatile.Read(ref entry.Token);
            if (Usable(token))
            {
                return token!;
            }

            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (entry.Retired)
                {
                    continue;
                }
                token = Volatile.Read(ref entry.Token);
                if (Usable(token))
                {
                    return token!;
                }
                if (token is null && initialToken is not null)
                {
                    var seed = Parse(initialToken, key);
                    if (Usable(seed))
                    {
                        Publish(entry, key, seed, "initial", null);
                        return seed;
                    }
                    token = seed;
                }

                McpAccessToken fresh;
                try
                {
                    fresh = Parse(await acquire(cancellationToken).ConfigureAwait(false), key);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Usable(fresh))
                    {
                        logger.LogWarning(
                            "McpTokenRejected audience={Audience} expiresUtc={ExpiresUtc} remainingSeconds={RemainingSeconds}.",
                            fresh.Audience, fresh.ExpiresAt, (fresh.ExpiresAt - clock.GetUtcNow()).TotalSeconds);
                        throw new InvalidOperationException("MCP credential has insufficient remaining lifetime.");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Renewals.Add(1, new KeyValuePair<string, object?>("outcome", "cancelled"));
                    throw;
                }
                catch (Exception ex)
                {
                    Renewals.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
                    logger.LogError("McpTokenRefreshFailed agent={Agent} scope={Scope} errorType={ErrorType}.",
                        key.Agent, key.Scope, ex.GetType().Name);
                    // Provider errors can contain assertions. Do not copy their text into model output or logs.
                    throw new InvalidOperationException("MCP authentication could not obtain a usable credential.");
                }
                Publish(entry, key, fresh, "provider", token is null ? null : token.Value != fresh.Value);
                Renewals.Add(1, new KeyValuePair<string, object?>("outcome", "succeeded"));
                return fresh;
            }
            finally
            {
                entry.Gate.Release();
            }
        }
    }

    private Entry GetEntry(McpTokenKey key)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            return entry;
        }
        lock (_admissionGate)
        {
            if (_entries.TryGetValue(key, out entry))
            {
                return entry;
            }
            if (_entries.Count >= Capacity)
            {
                foreach (var candidate in _entries.OrderBy(p => Interlocked.Read(ref p.Value.LastUsed)))
                {
                    if (!candidate.Value.Gate.Wait(0))
                    {
                        continue;
                    }
                    try
                    {
                        _entries.TryRemove(candidate.Key, out _);
                        candidate.Value.Retired = true;
                        break;
                    }
                    finally
                    {
                        candidate.Value.Gate.Release();
                    }
                }
            }
            if (_entries.Count >= Capacity)
            {
                logger.LogError("McpTokenCacheFull capacity={Capacity}. All entries are refreshing.", Capacity);
                throw new InvalidOperationException("MCP authentication cache is at capacity.");
            }
            entry = new Entry();
            _entries[key] = entry;
            return entry;
        }
    }

    internal void RecordSend(McpAccessToken token)
    {
        Remaining.Record((token.ExpiresAt - clock.GetUtcNow()).TotalSeconds);
    }

    private bool Usable(McpAccessToken? token) =>
        token is not null && token.ExpiresAt - clock.GetUtcNow() > RefreshMargin
        && (token.NotBefore is null || token.NotBefore <= clock.GetUtcNow());

    private void Publish(Entry entry, McpTokenKey key, McpAccessToken token, string source, bool? changed)
    {
        Volatile.Write(ref entry.Token, token);
        logger.LogInformation(
            "McpTokenReady tenant={Tenant} agent={Agent} audience={Audience} expiresUtc={ExpiresUtc} remainingSeconds={RemainingSeconds} source={Source} tokenChanged={TokenChanged}.",
            key.Tenant, key.Agent, token.Audience, token.ExpiresAt,
            (token.ExpiresAt - clock.GetUtcNow()).TotalSeconds, source, changed);
    }

    internal static McpAccessToken Parse(string token, McpTokenKey key)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new FormatException();
            }
            var parts = token.Split('.');
            if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
            {
                throw new FormatException();
            }
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            var separator = key.Scope.LastIndexOf('/');
            if (separator <= 0 || root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number
                || !exp.TryGetInt64(out var seconds)
                || !root.TryGetProperty("aud", out var aud) || aud.ValueKind != JsonValueKind.String
                || !string.Equals(NormalizeAudience(aud.GetString()!), NormalizeAudience(key.Scope[..separator]),
                    StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("tid", out var tid) || tid.ValueKind != JsonValueKind.String
                || !string.Equals(tid.GetString(), key.Tenant, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException();
            }
            DateTimeOffset? notBefore = null;
            if (root.TryGetProperty("nbf", out var nbf))
            {
                if (nbf.ValueKind != JsonValueKind.Number || !nbf.TryGetInt64(out var nbfSeconds))
                {
                    throw new FormatException();
                }
                notBefore = DateTimeOffset.FromUnixTimeSeconds(nbfSeconds);
            }
            return new(token, DateTimeOffset.FromUnixTimeSeconds(seconds), aud.GetString()!, notBefore);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException("MCP credential has invalid expiry, audience, or tenant metadata.");
        }
    }

    private static string NormalizeAudience(string audience)
    {
        var value = audience.StartsWith("api://", StringComparison.OrdinalIgnoreCase) ? audience[6..] : audience;
        return Guid.TryParse(value, out var id) ? id.ToString("D") : audience;
    }
}
