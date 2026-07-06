// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;

/// <summary>
/// Bounded classification of a tool call, used for the <c>tool_type</c> metric dimension.
/// CUA tool exposure isn't finalized, so lifecycle vs desktop-control are kept distinct;
/// <c>is_cua</c> is derived as "any cua_* kind".
/// </summary>
public enum ToolKind
{
    /// <summary>W365 session-lifecycle tool (StartSession / EndSession / GetSessionDetails). Dim: cua_lifecycle.</summary>
    CuaLifecycle,

    /// <summary>W365 Computer-Use desktop/browser action (computer_call / click / type / screenshot / browser_*). Dim: cua_desktopcontrol.</summary>
    CuaDesktopControl,

    /// <summary>Any non-CUA tool (Mail, Teams, weather, datetime, ...). Dim: others.</summary>
    Others,
}

internal static class ToolKindExtensions
{
    public static string ToDimValue(this ToolKind kind) => kind switch
    {
        ToolKind.CuaLifecycle => "cua_lifecycle",
        ToolKind.CuaDesktopControl => "cua_desktopcontrol",
        _ => "others",
    };

    public static bool IsCua(this ToolKind kind) => kind is ToolKind.CuaLifecycle or ToolKind.CuaDesktopControl;
}

/// <summary>
/// Per-turn ENRICHMENT unit. One instance per agent turn (disposable). Accumulates loop signals
/// during the turn (round-trips, tool calls, tokens) and, on <see cref="Dispose"/>, records the
/// per-turn metrics via <see cref="TurnMeter"/> and emits the unsampled <c>AgentTurnSummary</c> event.
///
/// A turn is marked is_cua as soon as any cua_* tool call is recorded (ground rule #2).
/// Identity fields (tenant/agent/user/conversation/session) go to logs+event only — never metrics.
/// </summary>
public sealed class TurnScope : IDisposable
{
    private readonly TurnMeter _meter;
    private readonly ILogger _logger;
    private readonly long _startTimestamp;

    // Identity + bounded dims captured at BeginTurn (mutable — enriched mid-turn).
    internal TurnTags Tags { get; }

    // Accumulated per-turn measures.
    private int _llmRoundtrips;
    private int _mcpToolCalls;
    private int _mcpToolCallFailures;
    private int _inputTokens;
    private int _outputTokens;
    private int _cachedTokens;
    private int _reasoningTokens;
    private bool _isCua;
    private bool _success = true;
    private string? _errorType;
    private string _model = "unknown";
    private bool _disposed;

    internal TurnScope(TurnMeter meter, TurnTags tags, ILogger logger)
    {
        _meter = meter;
        Tags = tags;
        _logger = logger;
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    // ---- called by business logic (thin, intent-revealing) -------------------------------

    public void SetAgentUser(string? agentUser) { if (!string.IsNullOrEmpty(agentUser)) Tags.AgentUser = agentUser; }

    public void SetUserOid(string? oid) { if (!string.IsNullOrEmpty(oid)) Tags.UserOid = oid; }

    public void SetModel(string? model) { if (!string.IsNullOrEmpty(model)) _model = model!; }

    /// <summary>Selected/active W365 session for this turn (latest wins if it switches mid-turn).</summary>
    public void SetSessionId(string? sessionId) { if (!string.IsNullOrEmpty(sessionId)) Tags.SessionId = sessionId; }

    /// <summary>Distinct W365 sessions this turn has touched (event-only).</summary>
    public void SetSessionCount(int count) { if (count > 0) Tags.SessionCount = count; }

    /// <summary>Records one LLM round-trip's usage. cached/reasoning are event-only extras (subsets of input/output).</summary>
    public void RecordModelRoundtrip(int inputTokens, int outputTokens, string? model = null, int cachedTokens = 0, int reasoningTokens = 0)
    {
        _llmRoundtrips++;
        _inputTokens += inputTokens;
        _outputTokens += outputTokens;
        _cachedTokens += cachedTokens;
        _reasoningTokens += reasoningTokens;
        if (!string.IsNullOrEmpty(model)) _model = model!;
        _meter.RecordRoundtripTokens(inputTokens, outputTokens, _model);
    }

    /// <summary>Records one MCP tool call. Caller supplies server explicitly so tool_name = server/tool never loses the server.</summary>
    public void RecordToolCall(string server, string tool, ToolKind kind, bool success, double durationMs)
    {
        _mcpToolCalls++;
        if (!success) _mcpToolCallFailures++;
        if (kind.IsCua()) _isCua = true;
        _meter.RecordToolCall(ToolNameOf(server, tool), kind.ToDimValue(), success, durationMs);
    }

    /// <summary>Records the dedicated StartSession round-trip latency (+ marks the turn as CUA).</summary>
    public void RecordStartSession(double durationMs, bool success)
    {
        _isCua = true;
        _meter.RecordStartSession(durationMs, success);
    }

    public void SetSuccess(bool success) => _success = success;

    public void SetError(string? errorType) { _success = false; _errorType = errorType; }

    // Compose the bounded tool_name dim. Model-driven unknown names never reach a record site
    // (unresolved tools are rejected before invocation), so the set is naturally bounded.
    private static string ToolNameOf(string server, string tool)
    {
        if (string.IsNullOrEmpty(server)) server = "unknown";
        if (string.IsNullOrEmpty(tool)) tool = "unknown";
        return $"{server}/{tool}";
    }

    // ---- flush on dispose ----------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            var durationMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            var totalTokens = _inputTokens + _outputTokens;

            // Pipeline A — per-turn metrics (bounded dims only).
            _meter.RecordTurn(_isCua, Tags.Channel, _success, _llmRoundtrips, _mcpToolCalls, _mcpToolCallFailures, durationMs, totalTokens, _model);

            // Pipeline B — one unsampled structured event carrying identity.
            TurnSummaryEvent.Emit(_logger, new TurnSummary(
                TenantId: Tags.TenantId,
                AgentUser: Tags.AgentUser ?? "(none)",
                UserOid: Tags.UserOid ?? "(none)",
                ConversationId: Tags.ConversationId,
                SessionId: Tags.SessionId ?? "(none)",
                SessionCount: Tags.SessionCount,
                Channel: Tags.Channel,
                IsCua: _isCua,
                Model: _model,
                LlmRoundtrips: _llmRoundtrips,
                McpToolCalls: _mcpToolCalls,
                McpToolCallFailures: _mcpToolCallFailures,
                InputTokens: _inputTokens,
                OutputTokens: _outputTokens,
                TotalTokens: totalTokens,
                CachedTokens: _cachedTokens,
                ReasoningTokens: _reasoningTokens,
                DurationMs: durationMs,
                Success: _success,
                ErrorType: _errorType ?? "(none)"));
        }
        catch
        {
            // Telemetry must never break the business flow (rule #3). Swallow.
        }
    }
}
