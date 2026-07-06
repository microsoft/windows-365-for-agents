// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Security.Claims;
using Microsoft.Agents.Builder;

namespace Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;

/// <summary>
/// Mutable bag of the identity + bounded dimensions captured for a turn.
/// Identity fields (tenant/agent/user/conversation/session) go ONLY to the pipeline-B event /
/// log enrichment — never to metric dimensions.
/// </summary>
internal sealed class TurnTags
{
    // Identity (logs/event only).
    public string TenantId { get; set; } = "unknown";
    public string? AgentUser { get; set; }
    public string? UserOid { get; set; }
    public string ConversationId { get; set; } = "unknown";
    public string? SessionId { get; set; }
    public int SessionCount { get; set; }

    // Bounded enum-ish value safe as a metric dimension.
    public string Channel { get; set; } = "unknown";
}

/// <summary>App-lifetime service: begins a turn scope and exposes the ambient current turn.</summary>
public interface ITurnScopeAccessor
{
    /// <summary>Begins a new turn scope, captures identity from the context, and sets it as ambient.</summary>
    TurnScope BeginTurn(ITurnContext turnContext);

    /// <summary>The turn scope currently in flight on this async flow, if any.</summary>
    TurnScope? CurrentTurn { get; }
}

/// <summary>
/// SERVICE: creates <see cref="TurnScope"/>s and tracks the ambient current turn via AsyncLocal so
/// deep call sites (orchestrator) and the log-enrichment processor can reach it without threading a
/// parameter through business logic (rule #3, minimally intrusive).
/// </summary>
internal sealed class TurnScopeAccessor : ITurnScopeAccessor
{
    // Claim spellings (short + WS-Fed URI) for tenant and user object id.
    private const string TidClaimShort = "tid";
    private const string TidClaimUri = "http://schemas.microsoft.com/identity/claims/tenantid";
    private const string OidClaimShort = "oid";
    private const string OidClaimUri = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private static readonly AsyncLocal<TurnScope?> _current = new();

    private readonly TurnMeter _meter;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TurnScopeAccessor> _logger;

    public TurnScopeAccessor(TurnMeter meter, ILoggerFactory loggerFactory)
    {
        _meter = meter;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TurnScopeAccessor>();
    }

    /// <summary>Ambient current turn, also readable by <see cref="TurnEnrichmentProcessor"/>.</summary>
    public TurnScope? CurrentTurn => _current.Value;

    internal static TurnScope? Ambient => _current.Value;

    public TurnScope BeginTurn(ITurnContext turnContext)
    {
        var tags = ExtractTags(turnContext);
        var scope = new TurnScope(_meter, tags, _loggerFactory.CreateLogger("AgentTurn"));
        _current.Value = scope;
        return scope;
    }

    /// <summary>Captures identity + bounded dims from the turn context (migrated from A365OtelWrapper).</summary>
    private TurnTags ExtractTags(ITurnContext turnContext)
    {
        var activity = turnContext.Activity;
        var identity = turnContext.Identity as ClaimsIdentity;
        var isAgentic = activity?.IsAgenticRequest() ?? false;

        var tags = new TurnTags
        {
            ConversationId = activity?.Conversation?.Id ?? "unknown",
            Channel = activity?.ChannelId?.ToString()?.ToLowerInvariant() ?? "unknown",
        };

        // ---- tenant_id: tid claim first (authoritative), then Activity fallbacks (Q4) ----
        var tidClaim = FindClaim(identity, TidClaimShort, TidClaimUri);
        if (string.IsNullOrEmpty(tidClaim))
        {
            _logger.LogWarning("[AgentTurn] tid claim missing (isAgentic={IsAgentic}, hasIdentity={HasIdentity}); falling back to Activity tenant.",
                isAgentic, identity is not null);
        }

        tags.TenantId = FirstNonEmpty(
            tidClaim,
            activity?.Conversation?.TenantId,
            activity?.Recipient?.TenantId) ?? "unknown";

        if (tags.TenantId == "unknown")
        {
            _logger.LogWarning("[AgentTurn] tenant_id could not be resolved from any source (isAgentic={IsAgentic}, hasIdentity={HasIdentity}, channel={Channel}).",
                isAgentic, identity is not null, tags.Channel);
        }

        // ---- agent_user: agentic instance id up front; OBO path set later via SetAgentUser (Q5) ----
        if (isAgentic)
        {
            var agenticId = activity?.GetAgenticInstanceId();
            if (!string.IsNullOrEmpty(agenticId)) tags.AgentUser = agenticId;
        }

        // ---- user_oid: best-effort, both claim spellings, event-only (Q6) ----
        tags.UserOid = FindClaim(identity, OidClaimShort, OidClaimUri);
        if (string.IsNullOrEmpty(tags.UserOid))
        {
            _logger.LogWarning("[AgentTurn] user_oid could not be resolved (isAgentic={IsAgentic}, hasIdentity={HasIdentity}).",
                isAgentic, identity is not null);
        }

        return tags;
    }

    private static string? FindClaim(ClaimsIdentity? identity, string shortType, string uriType)
        => identity?.FindFirst(shortType)?.Value ?? identity?.FindFirst(uriType)?.Value;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }
}
