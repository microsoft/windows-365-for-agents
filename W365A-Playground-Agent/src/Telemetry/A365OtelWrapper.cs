// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Agents.A365.Observability.Runtime.Common;
using Microsoft.Agents.A365.Runtime.Utils;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.UserAuth;
using Microsoft.Agents.Builder.State;

namespace Microsoft.W365APlaygroundAgent.Telemetry;

public static class A365OtelWrapper
{
    public static async Task InvokeObservedAgentOperation(
        string operationName,
        ITurnContext turnContext,
        ITurnState turnState,
        UserAuthorization authSystem,
        string authHandlerName,
        ILogger? logger,
        Func<Task> func
        )
    {
        // Wrap the operation with AgentSDK observability.
        await AgentMetrics.InvokeObservedAgentOperation(
            operationName,
            turnContext,
            async () =>
            {
                // Resolve the tenant and agent id being used to communicate with A365 services. 
                (string agentId, string tenantId) = await ResolveTenantAndAgentId(turnContext, authSystem, authHandlerName);

                using var baggageScope = new BaggageBuilder()
                .TenantId(tenantId)
                .AgentId(agentId)
                .Build();

                // Invoke the actual operation.
                await func().ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Resolves the tenant ID and agent ID for OTel baggage from the turn context.</summary>
    private static async Task<(string agentId, string tenantId)> ResolveTenantAndAgentId(ITurnContext turnContext, UserAuthorization authSystem, string authHandlerName)
    {
        string agentId = "";
        if (turnContext.Activity.IsAgenticRequest())
        {
            agentId = turnContext.Activity.GetAgenticInstanceId();
        }
        else
        {
            if (authSystem != null && !string.IsNullOrEmpty(authHandlerName))
                agentId = Utility.ResolveAgentIdentity(turnContext, await authSystem.GetTurnTokenAsync(turnContext, authHandlerName));
        }
        if (string.IsNullOrEmpty(agentId)) agentId = Guid.Empty.ToString();
        string? tempTenantId = turnContext.Activity.Conversation?.TenantId ?? turnContext.Activity.Recipient?.TenantId;
        string tenantId = tempTenantId ?? Guid.Empty.ToString();

        return (agentId, tenantId);
    }

}
