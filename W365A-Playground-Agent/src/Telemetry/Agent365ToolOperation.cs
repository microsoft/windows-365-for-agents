// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;

namespace Microsoft.W365APlaygroundAgent.Telemetry;

/// <summary>
/// Owns a child <c>execute_tool</c> span for one function, MCP, or computer-use
/// operation, including sanitized arguments, retries, and final outcome.
/// </summary>
internal sealed class Agent365ToolOperation : IDisposable
{
    private readonly ExecuteToolScope _scope;

    internal Agent365ToolOperation(
        AgentDetails agentDetails,
        UserDetails userDetails,
        Request request,
        string toolName,
        string callId,
        string argumentSummary,
        string toolType,
        string? toolServerName,
        string internalToolKind,
        ActivityContext? parentContext)
    {
        _scope = ExecuteToolScope.Start(
            request,
            new ToolCallDetails(
                toolName,
                argumentSummary,
                callId,
                description: "Redacted tool invocation",
                toolType: toolType,
                toolServerName: toolServerName),
            agentDetails,
            userDetails,
            new SpanDetails(ActivityKind.Internal, parentContext));
        // Keep the app-specific CUA taxonomy separate from canonical gen_ai.tool.type.
        _scope.RecordAttributes(new Dictionary<string, object?>
        {
            ["w365a.tool.kind"] = internalToolKind
        });
    }

    public void Complete(
        bool success,
        int retryCount,
        bool recovered,
        bool? tokenRefreshed,
        string outcome)
    {
        _scope.RecordAttributes(new Dictionary<string, object?>
        {
            ["w365a.transport.retry.count"] = retryCount,
            ["w365a.transport.retry.recovered"] = recovered,
            ["w365a.transport.token_refreshed"] = tokenRefreshed,
            ["w365a.operation.outcome"] = outcome
        });
        _scope.RecordResponse(new Dictionary<string, object>
        {
            ["status"] = success ? "success" : "error",
            ["outcome"] = outcome
        });
        if (!success)
        {
            _scope.RecordError(Agent365ActivityTelemetry.RedactedFailure(new InvalidOperationException()));
        }
    }

    public void RecordCancellation() => _scope.RecordCancellation();

    public void RecordFailure(Exception exception, string outcome)
    {
        _scope.RecordAttributes(new Dictionary<string, object?>
        {
            ["w365a.operation.outcome"] = outcome,
            ["w365a.error.classification"] = exception.GetType().Name
        });
        _scope.RecordError(Agent365ActivityTelemetry.RedactedFailure(exception));
    }

    public void Dispose() => _scope.Dispose();
}