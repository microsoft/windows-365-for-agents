// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;

/// <summary>
/// PIPELINE stage: an OpenTelemetry log processor that stamps the ambient turn's identity
/// (tenant_id / agent_user / user_oid / conversation_id / session_id) onto every
/// <see cref="LogRecord"/> emitted during a turn — so business code never has to attach identity
/// to its log calls (rule #3, Q9a). Identities are logs/trace-only, never metric dimensions.
///
/// Trace enrichment (BaseProcessor&lt;Activity&gt;) can plug in the same way later.
/// </summary>
internal sealed class TurnEnrichmentProcessor : BaseProcessor<LogRecord>
{
    private static readonly HashSet<string> IdentityKeys = new(StringComparer.Ordinal)
    {
        "tenant_id", "agent_user", "user_oid", "conversation_id", "session_id",
    };

    public override void OnEnd(LogRecord data)
    {
        var turn = TurnScopeAccessor.Ambient;
        if (turn is not null)
        {
            var tags = turn.Tags;

            // Last-write-wins: drop any pre-existing identity keys (set upstream or by another
            // processor) so we don't emit duplicate/conflicting attributes.
            var enriched = (data.Attributes ?? Array.Empty<KeyValuePair<string, object?>>())
                .Where(kv => !IdentityKeys.Contains(kv.Key))
                .ToList();

            enriched.Add(new("tenant_id", tags.TenantId));
            enriched.Add(new("agent_user", tags.AgentUser ?? "(none)"));
            enriched.Add(new("user_oid", tags.UserOid ?? "(none)"));
            enriched.Add(new("conversation_id", tags.ConversationId));
            enriched.Add(new("session_id", tags.SessionId ?? "(none)"));

            data.Attributes = enriched;
        }

        base.OnEnd(data);
    }
}
