// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry.Metrics;

namespace Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;

/// <summary>
/// ENABLE: the public API surface of the agent-telemetry enrichment layer. Wires the enrichment
/// "pipeline" into DI and the OpenTelemetry builders. Kept exporter-agnostic (Azure Monitor today →
/// Geneva later; ground rule #1) — no exporter-specific calls here.
/// </summary>
internal static class AgentTelemetryExtensions
{
    /// <summary>Registers the agent-telemetry services (meter definitions + turn-scope accessor).</summary>
    public static IServiceCollection AddAgentTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<TurnMeter>();
        services.AddSingleton<ITurnScopeAccessor, TurnScopeAccessor>();
        return services;
    }

    /// <summary>Registers the meter source and the custom histogram bucket views (ground rule #4).</summary>
    public static MeterProviderBuilder AddAgentTelemetryMetrics(this MeterProviderBuilder builder)
    {
        builder.AddMeter(TurnMeter.MeterName);

        builder
            .AddView(TurnMeter.ToolCallDurationName, Explicit(TurnMeter.BucketsToolCallMs))
            .AddView(TurnMeter.TurnDurationName, Explicit(TurnMeter.BucketsLongMs))
            .AddView(TurnMeter.RoundtripTokensName, Explicit(TurnMeter.BucketsTokens))
            .AddView(TurnMeter.TurnTokensName, Explicit(TurnMeter.BucketsTokens))
            .AddView(TurnMeter.LlmRoundtripsName, Explicit(TurnMeter.BucketsSmallCount))
            .AddView(TurnMeter.ToolCallsName, Explicit(TurnMeter.BucketsSmallCount))
            .AddView(TurnMeter.ToolCallFailuresName, Explicit(TurnMeter.BucketsSmallCount));

        return builder;
    }

    private static ExplicitBucketHistogramConfiguration Explicit(double[] boundaries)
        => new() { Boundaries = boundaries };
}
