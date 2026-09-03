// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;

using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;

namespace Microsoft.W365APlaygroundAgent.Telemetry;

/// <summary>
/// Owns the root <c>invoke_agent</c> span for one human-to-agent turn and creates
/// correlated inference and tool child operations.
/// </summary>
internal sealed class Agent365TurnOperation : IDisposable
{
    private readonly AgentDetails _agentDetails;
    private readonly UserDetails _userDetails;
    private readonly Request _request;
    private readonly InvokeAgentScope _scope;

    internal Agent365TurnOperation(
        AgentDetails agentDetails,
        UserDetails userDetails,
        Request request,
        Uri endpoint)
    {
        _agentDetails = agentDetails;
        _userDetails = userDetails;
        _request = request;
        _scope = InvokeAgentScope.Start(
            request,
            new InvokeAgentScopeDetails(endpoint),
            agentDetails,
            new CallerDetails(userDetails));
        // The SDK omits the default HTTPS port, but Agent 365 requires server.port.
        _scope.SetTagMaybe("server.port", endpoint.Port.ToString(CultureInfo.InvariantCulture));
    }

    public Agent365InferenceOperation StartInference(string model, int inputItems, int serializedCharacters) =>
        new(
            _agentDetails,
            _userDetails,
            ChildRequest(Agent365ActivityTelemetry.RedactedText(
                $"model input; items={Math.Max(0, inputItems)}",
                serializedCharacters)),
            model,
            _scope.GetActivityContext());

    public Agent365ToolOperation StartTool(
        string toolName,
        string callId,
        string argumentsJson,
        string toolType,
        string? toolServerName,
        string internalToolKind) =>
        new(
            _agentDetails,
            _userDetails,
            ChildRequest("<redacted tool request>"),
            toolName,
            callId,
            SummarizeArguments(argumentsJson),
            toolType,
            toolServerName,
            internalToolKind,
            _scope.GetActivityContext());

    public void RecordOutput(int messageCount, int textLength) =>
        _scope.RecordResponse(
            Agent365ActivityTelemetry.RedactedText(
                $"agent output; messages={Math.Max(0, messageCount)}",
                textLength));

    public void RecordCancellation() => _scope.RecordCancellation();

    public void RecordFailure(Exception exception) =>
        _scope.RecordError(Agent365ActivityTelemetry.RedactedFailure(exception));

    public void Dispose() => _scope.Dispose();

    private Request ChildRequest(string content) =>
        new(
            content,
            _request.SessionId,
            _request.Channel,
            _request.ConversationId,
            _request.OperationSource);

    private static string SummarizeArguments(string argumentsJson)
    {
        // Preserve only payload shape and value types; never export argument names or values.
        var summary = new Dictionary<string, object>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                summary["$root"] = DescribeValue(document.RootElement);
            }
            else
            {
                var properties = document.RootElement.EnumerateObject().ToArray();
                summary["$root"] = $"object(fields={properties.Length})";
                summary["$fieldTypes"] = properties
                    .Take(64)
                    .Select(property => DescribeValue(property.Value))
                    .ToArray();
                summary["$truncated"] = properties.Length > 64;
            }
        }
        catch (JsonException)
        {
            summary["$parse"] = "invalid-json";
            summary["$length"] = argumentsJson.Length;
        }

        return JsonSerializer.Serialize(summary);
    }

    private static string DescribeValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"string(length={value.GetString()?.Length ?? 0})",
        JsonValueKind.Array => $"array(count={value.GetArrayLength()})",
        JsonValueKind.Object => $"object(fields={value.EnumerateObject().Count()})",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown"
    };
}