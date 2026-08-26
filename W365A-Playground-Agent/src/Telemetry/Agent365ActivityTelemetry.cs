// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;

using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.Agents.A365.Observability.Runtime.Common;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;
using Microsoft.Agents.A365.Runtime.Utils;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.UserAuth;
using Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;

namespace Microsoft.W365APlaygroundAgent.Telemetry;

public sealed class Agent365ActivityTelemetry
{
    private const string AgentName = "W365A Playground Agent";
    private const string AgentDescription = "Windows 365 computer-use agent";
    private const string OperationSource = "W365APlaygroundAgent";

    private readonly AgenticTokenCache _agenticTokenCache;
    private readonly ServiceTokenCache _serviceTokenCache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Agent365ActivityTelemetry> _logger;
    private readonly SemaphoreSlim _tokenWarmLock = new(1, 1);

    public Agent365ActivityTelemetry(
        AgenticTokenCache agenticTokenCache,
        ServiceTokenCache serviceTokenCache,
        IConfiguration configuration,
        ILogger<Agent365ActivityTelemetry> logger)
    {
        _agenticTokenCache = agenticTokenCache;
        _serviceTokenCache = serviceTokenCache;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<Agent365TurnOperation?> BeginTurnAsync(
        ITurnContext turnContext,
        UserAuthorization userAuthorization,
        string? authHandlerName,
        int inputTextLength,
        CancellationToken cancellationToken)
    {
        if (!turnContext.IsAgenticRequest())
        {
            return null;
        }

        var activity = turnContext.Activity;
        var runtimeAgentId = activity.GetAgenticInstanceId();
        var tenantId = activity.Conversation?.TenantId ?? activity.Recipient?.TenantId;
        var callerId = activity.From?.AadObjectId;
        var conversationId = activity.Conversation?.Id;
        var blueprintId = _configuration["Connections:ServiceConnection:Settings:AgentId"];

        if (string.IsNullOrWhiteSpace(runtimeAgentId)
            || string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(callerId)
            || string.IsNullOrWhiteSpace(conversationId)
            || string.IsNullOrWhiteSpace(blueprintId))
        {
            _logger.LogWarning(
                "Agent 365 Activity skipped because required attribution is missing " +
                "(agent={HasAgent}, tenant={HasTenant}, callerid={HasCallerId}, conversation={HasConversation}, blueprint={HasBlueprint}).",
                !string.IsNullOrWhiteSpace(runtimeAgentId),
                !string.IsNullOrWhiteSpace(tenantId),
                !string.IsNullOrWhiteSpace(callerId),
                !string.IsNullOrWhiteSpace(conversationId),
                !string.IsNullOrWhiteSpace(blueprintId));
            return null;
        }

        if (string.IsNullOrWhiteSpace(authHandlerName))
        {
            _logger.LogWarning("Agent 365 Activity exporter token was not prepared because the agentic auth handler is missing.");
        }
        else
        {
            await WarmExporterTokenAsync(
                runtimeAgentId,
                tenantId,
                turnContext,
                userAuthorization,
                authHandlerName,
                cancellationToken).ConfigureAwait(false);
        }

        Uri? endpoint = null;
        if (Uri.TryCreate(activity.ServiceUrl, UriKind.Absolute, out var parsedEndpoint))
        {
            endpoint = parsedEndpoint;
        }

        var agenticUserId = TurnScopeAccessor.ResolveDirectoryObjectId(
            activity.Recipient?.AadObjectId,
            activity.Recipient?.Id);
        var channelName = string.IsNullOrWhiteSpace(activity.ChannelId?.ToString())
            ? "msteams"
            : activity.ChannelId.ToString();
        var agentDetails = new AgentDetails(
            agentId: runtimeAgentId,
            agentName: AgentName,
            agentDescription: AgentDescription,
            agenticUserId: agenticUserId,
            agentBlueprintId: blueprintId,
            tenantId: tenantId);
        var userDetails = new UserDetails(userId: callerId);
        var request = new Request(
            content: RedactedText("user input", inputTextLength),
            sessionId: conversationId,
            channel: new Channel(channelName),
            conversationId: conversationId,
            operationSource: OperationSource);

        return new Agent365TurnOperation(agentDetails, userDetails, request, endpoint);
    }

    private async Task WarmExporterTokenAsync(
        string agentId,
        string tenantId,
        ITurnContext turnContext,
        UserAuthorization userAuthorization,
        string authHandlerName,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(await _serviceTokenCache
            .GetObservabilityToken(agentId, tenantId)
            .ConfigureAwait(false)))
        {
            return;
        }

        await _tokenWarmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(await _serviceTokenCache
                .GetObservabilityToken(agentId, tenantId)
                .ConfigureAwait(false)))
            {
                return;
            }

            var scopes = EnvironmentUtils.GetObservabilityAuthenticationScope();
            _agenticTokenCache.InvalidateToken(agentId, tenantId);
            _agenticTokenCache.RegisterObservability(
                agentId,
                tenantId,
                new AgenticTokenStruct(userAuthorization, turnContext, authHandlerName),
                scopes);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = await _agenticTokenCache
                    .GetObservabilityToken(agentId, tenantId)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var lifetime = TryGetRemainingLifetime(token);
                if (string.IsNullOrEmpty(token) || lifetime is null || lifetime <= TimeSpan.Zero)
                {
                    _logger.LogWarning("Agent 365 Activity exporter token acquisition returned no usable token.");
                    return;
                }

                _serviceTokenCache.RegisterObservability(agentId, tenantId, token, scopes, lifetime);
            }
            finally
            {
                // Never retain the live turn context after the token has been materialized.
                _agenticTokenCache.InvalidateToken(agentId, tenantId);
            }
        }
        finally
        {
            _tokenWarmLock.Release();
        }
    }

    private static TimeSpan? TryGetRemainingLifetime(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            return jwt.Payload.Expiration.HasValue ? jwt.ValidTo - DateTime.UtcNow : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static string RedactedText(string kind, int length) =>
        $"<redacted {kind}; length={Math.Max(0, length)}>";

    internal static Exception RedactedFailure(Exception exception) =>
        new Agent365TelemetryException(exception.GetType().Name);

    private sealed class Agent365TelemetryException(string classification) : Exception(classification);
}

public sealed class Agent365TurnOperation : IDisposable
{
    private readonly AgentDetails _agentDetails;
    private readonly UserDetails _userDetails;
    private readonly Request _request;
    private readonly InvokeAgentScope _scope;

    internal Agent365TurnOperation(
        AgentDetails agentDetails,
        UserDetails userDetails,
        Request request,
        Uri? endpoint)
    {
        _agentDetails = agentDetails;
        _userDetails = userDetails;
        _request = request;
        _scope = InvokeAgentScope.Start(
            request,
            new InvokeAgentScopeDetails(endpoint),
            agentDetails,
            new CallerDetails(userDetails));
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
        string toolServerName) =>
        new(
            _agentDetails,
            _userDetails,
            ChildRequest("<redacted tool request>"),
            toolName,
            callId,
            SummarizeArguments(argumentsJson),
            toolType,
            toolServerName,
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

public sealed class Agent365InferenceOperation : IDisposable
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
        _scope.RecordFinishReasons([outcome]);
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

public sealed class Agent365ToolOperation : IDisposable
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
        string toolServerName,
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
            _scope.RecordError(new InvalidOperationException(outcome));
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
