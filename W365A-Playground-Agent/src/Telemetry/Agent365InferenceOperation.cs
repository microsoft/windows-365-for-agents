// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;

namespace Microsoft.W365APlaygroundAgent.Telemetry;

/// <summary>
/// Owns a child <c>chat</c> span for one logical model request, including its
/// retries, token usage, redacted output summary, and final outcome.
/// </summary>
internal sealed class Agent365InferenceOperation : IDisposable
{
    private readonly InferenceScope _scope;

    internal Agent365InferenceOperation(
        AgentDetails agentDetails,
        UserDetails userDetails,
        Request request,
        string model,
        ActivityContext? parentContext)
    {
        _scope = InferenceScope.Start(
            request,
            new InferenceCallDetails(InferenceOperationType.Chat, model, "azure.openai"),
            agentDetails,
            userDetails,
            new SpanDetails(ActivityKind.Client, parentContext));
    }

    public void Complete(
        string responseId,
        string outcome,
        int inputTokens,
        int outputTokens,
        int cachedTokens,
        int reasoningTokens,
        int outputItems,
        int outputTextLength,
        int retryCount)
    {
        _scope.RecordInputTokens(inputTokens);
        _scope.RecordOutputTokens(outputTokens);
        _scope.RecordOutputMessages([
            Agent365ActivityTelemetry.RedactedText(
                $"model output; items={Math.Max(0, outputItems)}",
                outputTextLength)
        ]);
        _scope.RecordAttributes(new Dictionary<string, object?>
        {
            ["gen_ai.response.id"] = responseId,
            ["gen_ai.usage.input_tokens.cached"] = cachedTokens,
            ["gen_ai.usage.output_tokens.reasoning"] = reasoningTokens,
            ["w365a.transport.retry.count"] = retryCount,
            ["w365a.transport.retry.recovered"] = retryCount > 0,
            ["w365a.operation.outcome"] = outcome
        });
    }

    public void RecordCancellation() => _scope.RecordCancellation();

    public void RecordFailure(Exception exception, int retryCount)
    {
        _scope.RecordAttributes(new Dictionary<string, object?>
        {
            ["w365a.transport.retry.count"] = retryCount,
            ["w365a.operation.outcome"] = "error",
            ["w365a.error.classification"] = exception.GetType().Name
        });
        _scope.RecordError(Agent365ActivityTelemetry.RedactedFailure(exception));
    }

    public void Dispose() => _scope.Dispose();
}