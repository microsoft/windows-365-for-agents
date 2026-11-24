// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;

namespace Microsoft.W365APlaygroundAgent.Auth;

internal sealed record McpIdentity(
    string Tenant, string Caller, string Agent, string Subject, string Conversation, bool InstanceOnly)
{
    internal static McpIdentity FromTurn(ITurnContext context)
    {
        var activity = context.Activity;
        var identity = new McpIdentity(activity.GetAgenticTenantId(),
            activity.From?.AadObjectId ?? activity.From?.Id ?? "",
            activity.GetAgenticInstanceId(), activity.GetAgenticUser() ?? "",
            activity.Conversation?.Id ?? "",
            string.Equals(activity.Recipient?.Role, RoleTypes.AgenticIdentity, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(identity.Tenant) || string.IsNullOrWhiteSpace(identity.Caller)
            || string.IsNullOrWhiteSpace(identity.Agent) || string.IsNullOrWhiteSpace(identity.Conversation)
            || (!identity.InstanceOnly && string.IsNullOrWhiteSpace(identity.Subject)))
        {
            throw new InvalidOperationException("MCP authentication identity is incomplete.");
        }
        return identity;
    }
}

internal sealed class McpRequestContext : IDisposable
{
    private sealed class Holder { public McpRequestContext? Value; }
    private static readonly AsyncLocal<Holder?> Ambient = new();
    private readonly Holder? _previous;
    private readonly Holder _holder;
    internal McpIdentity? Identity { get; }
    internal string Message { get; }
    internal string? TraceId { get; }
    internal string? SpanId { get; }

    private McpRequestContext(ITurnContext context)
    {
        Identity = context.IsAgenticRequest()
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BEARER_TOKEN"))
            ? McpIdentity.FromTurn(context) : null;
        Message = SanitizeMessage(context.Activity.Text ?? "");
        TraceId = context.StackState.TryGetValue("O11yTraceId", out var trace) ? trace as string : null;
        SpanId = context.StackState.TryGetValue("O11ySpanId", out var span) ? span as string : null;
        _previous = Ambient.Value;
        _holder = new Holder { Value = this };
        Ambient.Value = _holder;
    }

    internal static McpRequestContext Enter(ITurnContext context) => new(context);
    internal static McpRequestContext? Current => Ambient.Value?.Value;

    private static string SanitizeMessage(string value)
    {
        // Match the SDK's ASCII header representation, computed once per turn rather than per request.
        var buffer = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            buffer.Append(character switch
            {
                '\u2018' or '\u2019' => "'",
                '\u201C' or '\u201D' => "\"",
                '\u2013' or '\u2014' => "-",
                '\u2026' => "...",
                >= ' ' and <= '~' => character.ToString(),
                _ => " "
            });
        }
        return Regex.Replace(buffer.ToString(), @"\s+", " ").Trim();
    }

    public void Dispose()
    {
        // Clear inherited execution contexts too, including a transport's background receive loop.
        _holder.Value = null;
        if (ReferenceEquals(Ambient.Value, _holder))
        {
            Ambient.Value = _previous;
        }
    }
}
