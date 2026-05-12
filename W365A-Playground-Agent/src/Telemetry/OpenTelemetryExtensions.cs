// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Microsoft.W365APlaygroundAgent.Telemetry;

// OpenTelemetry / Aspire wiring for the W365A Playground Agent.
// Currently configures metrics; tracing can be enabled by adding a .WithTracing(...) call
// to ConfigureOpenTelemetry. Exporters chosen via env vars at runtime
// (OTEL_EXPORTER_OTLP_ENDPOINT for OTLP, APPLICATIONINSIGHTS_CONNECTION_STRING for Azure Monitor).
// See https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/dashboard/standalone for the
// local Aspire dashboard.
public static class OpenTelemetryExtensions
{
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
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
                    .AddMeter("agent.messages.processed",
                        "agent.routes.executed",
                        "agent.conversations.active",
                        "agent.route.execution.duration",
                        "agent.message.processing.duration");
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
                .UseAzureMonitor();
        }

        return builder;
    }
}
