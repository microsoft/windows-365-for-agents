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
    public string? AgentInstanceId { get; set; }
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
    // Claim spellings (short + WS-Fed URI) for tenant id.
    private const string TidClaimShort = "tid";
    private const string TidClaimUri = "http://schemas.microsoft.com/identity/claims/tenantid";

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
        var previous = _current.Value;
        var scope = new TurnScope(_meter, tags, _loggerFactory.CreateLogger("AgentTurn"));
        // Push/pop: restore the previous ambient on dispose, but only if this scope is still current
        // (guards against out-of-order disposal on the same async flow).
        scope.OnDisposed = () => { if (ReferenceEquals(_current.Value, scope)) { _current.Value = previous; } };
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

        // ---- user_oid: the HUMAN operator, from Activity.From (NOT the token oid, which is the
        //      messaging-bot service principal). Event-only. ----
        tags.UserOid = ResolveDirectoryObjectId(activity?.From?.AadObjectId, activity?.From?.Id);
        if (string.IsNullOrEmpty(tags.UserOid))
        {
            _logger.LogWarning("[AgentTurn] user_oid (human) unresolved from Activity.From (isAgentic={IsAgentic}, channel={Channel}).",
                isAgentic, tags.Channel);
        }

        // ---- agent_user + agent_instance_id (agentic only) ----
        //  agent_user        = the agent's digital-worker USER account (Activity.Recipient) — a named
        //                      Entra user, 1:1 with the instance, and the Cloud PC entitlement holder.
        //  agent_instance_id = the runtime agent instance (secondary, for A365 drill-down).
        //  OBO/non-agentic agent identity is set later via SetAgentUser (ResolveAgentIdentity).
        if (isAgentic)
        {
            tags.AgentUser = ResolveDirectoryObjectId(activity?.Recipient?.AadObjectId, activity?.Recipient?.Id);
            tags.AgentInstanceId = activity?.GetAgenticInstanceId();
            if (string.IsNullOrEmpty(tags.AgentUser))
            {
                _logger.LogWarning("[AgentTurn] agent_user (digital worker) unresolved from Activity.Recipient (channel={Channel}).",
                    tags.Channel);
            }
        }

        return tags;
    }

    private static string? FindClaim(ClaimsIdentity? identity, string shortType, string uriType)
        => identity?.FindFirst(shortType)?.Value ?? identity?.FindFirst(uriType)?.Value;

    // Resolve a directory object id (GUID) from a ChannelAccount: prefer AadObjectId; else strip the
    // channel "8:orgid:" prefix off Id and accept only if it parses as a GUID (never stamp a
    // channel-scoped non-directory id as identity).
    private static string? ResolveDirectoryObjectId(string? aadObjectId, string? channelAccountId)
    {
        if (!string.IsNullOrEmpty(aadObjectId))
        {
            return aadObjectId;
        }

        if (string.IsNullOrEmpty(channelAccountId))
        {
            return null;
        }

        const string OrgIdPrefix = "8:orgid:";
        var id = channelAccountId.StartsWith(OrgIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? channelAccountId[OrgIdPrefix.Length..] : channelAccountId;
        return Guid.TryParse(id, out _) ? id : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v))
            {
                return v;
            }
        }
        return null;
    }
}
