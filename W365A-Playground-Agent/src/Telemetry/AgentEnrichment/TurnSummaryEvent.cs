// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;

/// <summary>
/// Pipeline-B LOG signal: the immutable per-turn summary emitted exactly once per turn.
/// Carries identity (safe in logs) + measures, enabling Business insights
/// (dcount(tenant/agent/user), per-tenant token sums) via backend queries.
/// Emitted UNSAMPLED (sampling would corrupt counts/sums — ground rule #7).
/// </summary>
internal readonly record struct TurnSummary(
    string TenantId,
    string AgentUser,
    string AgentInstanceId,
    string UserOid,
    string ConversationId,
    string SessionId,
    int SessionCount,
    string Channel,
    bool IsCua,
    string Model,
    int LlmRoundtrips,
    int McpToolCalls,
    int McpToolCallFailures,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    int CachedTokens,
    int ReasoningTokens,
    double DurationMs,
    bool Success,
    string ErrorType);

/// <summary>
/// Emits <see cref="TurnSummary"/> as a structured, source-generated log event
/// (EventId 6001 / EventName "AgentTurnSummary").
///
/// SAMPLING: this event MUST NOT be sampled (ground rule #7) — token sums / distinct counts would be
/// corrupted. Azure Monitor applies adaptive sampling to dependency/request telemetry; ILogger log
/// records are ingested into the "traces" table and are NOT subject to that trace sampler by default.
/// If a sampling override is later introduced, exclude EventName "AgentTurnSummary".
/// </summary>
internal static partial class TurnSummaryEvent
{
    public const string EventName = "AgentTurnSummary";

    // Stable numeric EventId so Kusto queries / alerts can key on it across versions. The value is
    // arbitrary (app-chosen), but MUST stay fixed once shipped — do not renumber. Kept in the app's
    // own 6000-series to avoid collision with framework/library event ids.
    public const int EventId = 6001;

    public static void Emit(ILogger logger, TurnSummary s) => LogTurnSummary(
        logger,
        s.TenantId, s.AgentUser, s.AgentInstanceId, s.UserOid, s.ConversationId, s.SessionId, s.SessionCount,
        s.Channel, s.IsCua, s.Model, s.LlmRoundtrips, s.McpToolCalls, s.McpToolCallFailures,
        s.InputTokens, s.OutputTokens, s.TotalTokens, s.CachedTokens, s.ReasoningTokens,
        s.DurationMs, s.Success, s.ErrorType);

    [LoggerMessage(
        EventId = EventId,
        EventName = EventName,
        Level = LogLevel.Information,
        Message = "AgentTurnSummary tenant={tenant_id} agentUser={agent_user} agentInstance={agent_instance_id} oid={user_oid} conv={conversation_id} " +
                  "session={session_id} sessionCount={session_count} channel={channel} isCua={is_cua} model={model} " +
                  "roundtrips={llm_roundtrips} toolcalls={mcp_toolcalls} toolcallFailures={mcp_toolcall_failures} " +
                  "inTok={input_tokens} outTok={output_tokens} totTok={total_tokens} cachedTok={cached_tokens} " +
                  "reasoningTok={reasoning_tokens} durMs={duration_ms} success={success} err={error_type}")]
    private static partial void LogTurnSummary(
        ILogger logger,
        string tenant_id,
        string agent_user,
        string agent_instance_id,
        string user_oid,
        string conversation_id,
        string session_id,
        int session_count,
        string channel,
        bool is_cua,
        string model,
        int llm_roundtrips,
        int mcp_toolcalls,
        int mcp_toolcall_failures,
        int input_tokens,
        int output_tokens,
        int total_tokens,
        int cached_tokens,
        int reasoning_tokens,
        double duration_ms,
        bool success,
        string error_type);
}
