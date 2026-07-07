// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Microsoft.W365APlaygroundAgent.Telemetry;

// OpenTelemetry / Aspire wiring for the W365A Playground Agent.
// Enables stock instrumentation (AspNetCore/Http/Runtime) + the thin agent-enrichment layer
// (w365a.* metrics + per-turn log enrichment). Tracing can be enabled by adding a .WithTracing(...)
// call. Exporters chosen via env vars at runtime (OTEL_EXPORTER_OTLP_ENDPOINT for OTLP,
// APPLICATIONINSIGHTS_CONNECTION_STRING for Azure Monitor).
// See https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/standalone for the
// local Aspire dashboard.
public static class OpenTelemetryExtensions
{
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // Agent-telemetry DI (TurnMeter + ITurnScopeAccessor).
        builder.Services.AddAgentTelemetry();

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            // Pipeline stage: stamp ambient turn identity onto every log record (Q9a).
            logging.AddProcessor(new TurnEnrichmentProcessor());
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
            .Clear()
            .AddService(
                serviceName: "W365APlaygroundAgent",
                serviceVersion: "1.0.0",
                serviceInstanceId: Environment.MachineName)
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = builder.Environment.EnvironmentName,
                ["service.namespace"] = "Microsoft.W365APlaygroundAgent"
            }))
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Agent-workflow metrics: registers the "W365APlaygroundAgent" meter source
                    // (source name — NOT instrument names) + custom histogram bucket views.
                    .AddAgentTelemetryMetrics();
            });

        builder.AddOpenTelemetryExporters();
        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            builder.Services.AddOpenTelemetry()
                .UseAzureMonitor(options =>
                {
                    // Capture 100% of traces — no sampling (ground rule #7): the per-turn
                    // AgentTurnSummary event + token/count telemetry must never be dropped.
                    // (Logs are not subject to this trace sampler; this guarantees spans too.)
                    options.SamplingRatio = 1.0F;
                });
        }

        return builder;
    }
}
