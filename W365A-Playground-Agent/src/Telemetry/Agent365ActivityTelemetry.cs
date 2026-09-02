// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.IdentityModel.Tokens.Jwt;
using System.Net;

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

    internal async Task<Agent365TurnOperation?> BeginTurnAsync(
        ITurnContext turnContext,
        UserAuthorization userAuthorization,
        string? authHandlerName,
        int inputTextLength,
        CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue<bool>("EnableAgent365Exporter"))
        {
            return null;
        }

        if (!turnContext.IsAgenticRequest())
        {
            return null;
        }

        var activity = turnContext.Activity;
        var runtimeAgentId = activity.GetAgenticInstanceId();
        var tenantId = activity.Conversation?.TenantId ?? activity.Recipient?.TenantId;
        var callerObjectId = activity.From?.AadObjectId;
        var conversationId = activity.Conversation?.Id;
        var blueprintId = _configuration["Connections:ServiceConnection:Settings:AgentId"];

        if (string.IsNullOrWhiteSpace(runtimeAgentId)
            || string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(callerObjectId)
            || string.IsNullOrWhiteSpace(conversationId)
            || string.IsNullOrWhiteSpace(blueprintId))
        {
            _logger.LogWarning(
                "Agent 365 Activity skipped because required attribution is missing " +
                "(agent={HasAgent}, tenant={HasTenant}, user={HasUser}, conversation={HasConversation}, blueprint={HasBlueprint}).",
                !string.IsNullOrWhiteSpace(runtimeAgentId),
                !string.IsNullOrWhiteSpace(tenantId),
                !string.IsNullOrWhiteSpace(callerObjectId),
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
            try
            {
                await WarmExporterTokenAsync(
                    runtimeAgentId,
                    tenantId,
                    turnContext,
                    userAuthorization,
                    authHandlerName,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent 365 Activity exporter token could not be prepared; continuing the user turn.");
            }
        }

        var endpoint = Uri.TryCreate(activity.ServiceUrl, UriKind.Absolute, out var parsedEndpoint)
            ? parsedEndpoint
            : new Uri("https://localhost/");

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
        var userDetails = new UserDetails(
            userId: callerObjectId,
            userName: activity.From?.Name,
            userClientIP: IPAddress.Any);
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