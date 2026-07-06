// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;

/// <summary>
/// DEFINITIONS: owns the OpenTelemetry <see cref="Meter"/> and the agent-workflow metric
/// instruments, plus their names, bounded-dimension keys, histogram bucket boundaries, and
/// thin Record* helpers. App-lifetime singleton.
///
/// Design rules:
///  - Metric dimensions MUST be low-cardinality bounded enums. NEVER put identities
///    (tenant_id / agent_user / user_oid / conversation_id / session_id) on a metric —
///    those belong to logs/traces only.
///  - Names are custom under "w365a.*" (no experimental gen_ai.*).
///
/// NOTE: file/type name "TurnMeter" is provisional — final name to be decided at code review.
/// </summary>
internal sealed class TurnMeter
{
    // Meter source name — reused from the previous AgentMetrics implementation so existing
    // OTel registration keeps working during migration.
    public const string MeterName = "W365APlaygroundAgent";
    public const string MeterVersion = "1.0.0";

    // ---- instrument names (subject-based: turn.* / mcp.* / llm.*; cua is a dimension) ----
    public const string TurnsName = "w365a.turn.count";
    public const string LlmRoundtripsName = "w365a.turn.llm_roundtrips";
    public const string ToolCallsName = "w365a.turn.toolcalls";
    public const string ToolCallFailuresName = "w365a.turn.toolcall_failures";
    public const string TurnDurationName = "w365a.turn.duration";
    public const string ToolCallDurationName = "w365a.mcp.toolcall.duration";
    public const string RoundtripTokensName = "w365a.llm.roundtrip.tokens";
    public const string TurnTokensName = "w365a.turn.tokens";
    public const string TokensName = "w365a.llm.tokens.total";

    // ---- bounded dimension keys ----------------------------------------------------------
    public const string DimIsCua = "is_cua";
    public const string DimChannel = "channel";
    public const string DimSuccess = "success";
    public const string DimToolName = "tool_name";
    public const string DimToolType = "tool_type";
    public const string DimOutcome = "outcome";
    public const string DimTokenType = "token_type";
    public const string DimModel = "model";

    // ---- explicit histogram bucket boundaries ---------------------------------------------
    // These numeric arrays are DELIBERATE configuration, not magic constants: OpenTelemetry
    // histograms require explicit bucket boundaries, and the defaults (≤10s) don't fit our ranges.
    // Each profile is chosen to give useful resolution across the value range it measures.
    //
    // toolcall-ms — per MCP tool-call latency; spans fast tool calls (tens of ms) AND Cloud PC
    //   StartSession provisioning (up to minutes; StartSession is tool_name="W365ComputerUse/StartSession").
    //   TO BE OBSERVED: StartSession runs far longer than every other tool; watch whether it skews this
    //   histogram's unfiltered percentiles in prod and, if so, split it out or exclude it.
    // long-ms      — end-to-end turn latency (sub-second to several minutes for long CUA turns).
    // tokens       — per-turn / per-round-trip token counts; CUA turns re-send screenshots each
    //   round-trip, pushing input tokens into the hundreds of thousands, hence the range to 1e6.
    // small-count  — fan-out / tool-calls per turn; Fibonacci-ish boundaries for fine low-end resolution.
    public static readonly double[] BucketsToolCallMs = { 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000, 120000, 300000 };
    public static readonly double[] BucketsLongMs = { 250, 500, 1000, 2500, 5000, 10000, 30000, 60000, 120000, 300000 };
    public static readonly double[] BucketsTokens = { 100, 500, 1000, 5000, 10000, 50000, 100000, 250000, 500000, 1000000 };
    public static readonly double[] BucketsSmallCount = { 1, 2, 3, 5, 8, 13, 21, 34, 55 };

    private readonly Meter _meter;

    // ---- the 9 instruments ---------------------------------------------------------------
    private readonly Counter<long> _turns;
    private readonly Histogram<int> _llmRoundtrips;
    private readonly Histogram<int> _toolCalls;
    private readonly Histogram<int> _toolCallFailures;
    private readonly Histogram<double> _turnDuration;
    private readonly Histogram<double> _toolCallDuration;
    private readonly Histogram<int> _roundtripTokens;
    private readonly Histogram<int> _turnTokens;
    private readonly Counter<long> _tokens;

    public TurnMeter(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName, MeterVersion);

        _turns = _meter.CreateCounter<long>(TurnsName, unit: "{turn}", description: "Count of agent turns.");
        _llmRoundtrips = _meter.CreateHistogram<int>(LlmRoundtripsName, unit: "{roundtrip}", description: "LLM round-trips per turn (fan-out).");
        _toolCalls = _meter.CreateHistogram<int>(ToolCallsName, unit: "{call}", description: "MCP tool calls per turn.");
        _toolCallFailures = _meter.CreateHistogram<int>(ToolCallFailuresName, unit: "{call}", description: "Failed MCP tool calls per turn.");
        _turnDuration = _meter.CreateHistogram<double>(TurnDurationName, unit: "ms", description: "End-to-end turn duration.");
        _toolCallDuration = _meter.CreateHistogram<double>(ToolCallDurationName, unit: "ms", description: "Per MCP tool-call duration (incl. StartSession via tool_name).");
        _roundtripTokens = _meter.CreateHistogram<int>(RoundtripTokensName, unit: "{token}", description: "Tokens per LLM round-trip.");
        _turnTokens = _meter.CreateHistogram<int>(TurnTokensName, unit: "{token}", description: "Tokens per turn.");
        _tokens = _meter.CreateCounter<long>(TokensName, unit: "{token}", description: "Total tokens consumed (for sums).");
    }

    // ---- Record* helpers (bounded dims only; NEVER identities) ----------------------------

    /// <summary>Records the per-turn instruments at turn end.</summary>
    public void RecordTurn(bool isCua, string channel, bool success, int llmRoundtrips, int toolCalls, int toolCallFailures, double durationMs, int turnTokens, string model)
    {
        _turns.Add(1, new TagList { { DimIsCua, isCua }, { DimChannel, channel }, { DimSuccess, success } });
        _llmRoundtrips.Record(llmRoundtrips, new TagList { { DimIsCua, isCua } });
        _toolCalls.Record(toolCalls);
        _toolCallFailures.Record(toolCallFailures);
        _turnDuration.Record(durationMs, new TagList { { DimIsCua, isCua } });
        _turnTokens.Record(turnTokens, new TagList { { DimModel, model } });
    }

    /// <summary>Records tokens for a single LLM round-trip (input + output as two token_type series).</summary>
    public void RecordRoundtripTokens(int inputTokens, int outputTokens, string model)
    {
        _roundtripTokens.Record(inputTokens, new TagList { { DimTokenType, "input" }, { DimModel, model } });
        _roundtripTokens.Record(outputTokens, new TagList { { DimTokenType, "output" }, { DimModel, model } });
        _tokens.Add(inputTokens, new TagList { { DimTokenType, "input" }, { DimModel, model } });
        _tokens.Add(outputTokens, new TagList { { DimTokenType, "output" }, { DimModel, model } });
    }

    /// <summary>Records a single MCP tool-call duration + outcome (success/failure).
    /// StartSession is included here as tool_name="W365ComputerUse/StartSession" (bounded).</summary>
    public void RecordToolCall(string toolName, string toolType, bool success, double durationMs)
        => _toolCallDuration.Record(durationMs, new TagList
        {
            { DimToolName, toolName },
            { DimToolType, toolType },
            { DimOutcome, success ? "success" : "failure" },
        });
}
