// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Agents.Builder;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Microsoft.W365APlaygroundAgent.Telemetry;

public static class AgentMetrics
{
    public static readonly string SourceName = "W365APlaygroundAgent";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    private static readonly Meter Meter = new(SourceName, "1.0.0");

    public static readonly Counter<long> MessageProcessedCounter = Meter.CreateCounter<long>(
        "agent.messages.processed",
        "messages",
        "Number of messages processed by the agent");

    public static readonly Counter<long> RouteExecutedCounter = Meter.CreateCounter<long>(
        "agent.routes.executed",
        "routes",
        "Number of routes executed by the agent");

    public static readonly Histogram<double> MessageProcessingDuration = Meter.CreateHistogram<double>(
        "agent.message.processing.duration",
        "ms",
        "Duration of message processing in milliseconds");

    public static readonly Histogram<double> RouteExecutionDuration = Meter.CreateHistogram<double>(
        "agent.route.execution.duration",
        "ms",
        "Duration of route execution in milliseconds");

    public static readonly UpDownCounter<long> ActiveConversations = Meter.CreateUpDownCounter<long>(
        "agent.conversations.active",
        "conversations",
        "Number of active conversations");

    /// <summary>
    /// Starts an OTel activity for a turn handler. Returns null when no listener is attached
    /// (e.g. tracing disabled), so callers must use null-conditional access.
    /// </summary>
    private static Activity? InitializeMessageHandlingActivity(string handlerName, ITurnContext context)
    {
        var activity = ActivitySource.StartActivity(handlerName);
        activity?.SetTag("Activity.Type", context.Activity.Type.ToString());
        activity?.SetTag("Agent.IsAgentic", context.IsAgenticRequest());
        activity?.SetTag("Caller.Id", context.Activity.From?.Id);
        activity?.SetTag("Conversation.Id", context.Activity.Conversation?.Id);
        activity?.SetTag("Channel.Id", context.Activity.ChannelId?.ToString());
        activity?.SetTag("Message.Text.Length", context.Activity.Text?.Length ?? 0);

        activity?.AddEvent(new ActivityEvent("Message.Processed", DateTimeOffset.UtcNow, new()
        {
            ["Agent.IsAgentic"] = context.IsAgenticRequest(),
            ["Caller.Id"] = context.Activity.From?.Id,
            ["Channel.Id"] = context.Activity.ChannelId?.ToString(),
            ["Message.Id"] = context.Activity.Id,
            ["Message.Text"] = context.Activity.Text
        }));
        return activity;
    }

    private static void FinalizeMessageHandlingActivity(Activity? activity, ITurnContext context, long duration, bool success)
    {
        MessageProcessingDuration.Record(duration,
                new("Conversation.Id", context.Activity.Conversation?.Id ?? "unknown"),
                new("Channel.Id", context.Activity.ChannelId?.ToString() ?? "unknown"));

        RouteExecutedCounter.Add(1,
            new("Route.Type", "message_handler"),
            new("Conversation.Id", context.Activity.Conversation?.Id ?? "unknown"));

        activity?.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        activity?.Dispose();  // Dispose() calls Stop() internally
    }

    public static async Task InvokeObservedHttpOperation(string operationName, Func<Task> func)
    {
        using var activity = ActivitySource.StartActivity(operationName);
        try
        {
            await func();
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", DateTimeOffset.UtcNow, new()
            {
                ["exception.type"] = ex.GetType().FullName,
                ["exception.message"] = ex.Message,
                ["exception.stacktrace"] = ex.StackTrace
            }));
            throw;
        }
    }

    public static async Task InvokeObservedAgentOperation(string operationName, ITurnContext context, Func<Task> func)
    {
        MessageProcessedCounter.Add(1);
        // Init the activity for observability
        var activity = InitializeMessageHandlingActivity(operationName, context);
        var routeStopwatch = Stopwatch.StartNew();
        bool success = false;
        try
        {
            // Must await — returning func() bypasses the catch/finally for any exception
            // thrown after the first await point inside func, and finalises the activity
            // before the operation actually completes.
            await func().ConfigureAwait(false);
            success = true;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", DateTimeOffset.UtcNow, new()
            {
                ["exception.type"] = ex.GetType().FullName,
                ["exception.message"] = ex.Message,
                ["exception.stacktrace"] = ex.StackTrace
            }));
            throw;
        }
        finally
        {
            routeStopwatch.Stop();
            FinalizeMessageHandlingActivity(activity, context, routeStopwatch.ElapsedMilliseconds, success);
        }
    }
}
