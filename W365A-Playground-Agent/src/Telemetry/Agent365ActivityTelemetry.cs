// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Net;

using Microsoft.Agents.A365.Observability.Runtime.Tracing.Contracts;
using Microsoft.Agents.A365.Observability.Runtime.Tracing.Scopes;
using Microsoft.Agents.Builder;
using Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;

namespace Microsoft.W365APlaygroundAgent.Telemetry;

/// <summary>
/// Creates authenticated Agent 365 root operations for agentic Teams turns and applies
/// the user, agent, tenant, conversation, and endpoint attribution required for ingestion.
/// </summary>
public sealed class Agent365ActivityTelemetry
{
    private const string AgentName = "W365A Playground Agent";
    private const string AgentDescription = "Windows 365 computer-use agent";
    private const string OperationSource = "W365APlaygroundAgent";

    private readonly IConfiguration _configuration;
    private readonly ILogger<Agent365ActivityTelemetry> _logger;

    public Agent365ActivityTelemetry(
        IConfiguration configuration,
        ILogger<Agent365ActivityTelemetry> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    internal Agent365TurnOperation? BeginTurn(
        ITurnContext turnContext,
        int inputTextLength)
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

        // Agent 365 requires server attributes; use the SDK sample's safe fallback when
        // the channel does not provide a valid service URL.
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
        // No trustworthy client IP is available from the activity, so emit the required
        // unspecified address rather than inventing a caller address.
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

    internal static string RedactedText(string kind, int length) =>
        $"<redacted {kind}; length={Math.Max(0, length)}>";

    internal static Exception RedactedFailure(Exception exception) =>
        new Agent365TelemetryException(exception.GetType().Name);

    private sealed class Agent365TelemetryException(string classification) : Exception(classification);
}