// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.W365APlaygroundAgent.ComputerUse;
using Xunit;

namespace Microsoft.W365APlaygroundAgent.Tests;

public class ScreenShareModelResultTests
{
    private const string BackendUrl = "https://example.invalid/computers/session/screenshare?api-version=1.0&mode=view";
    private const string TaskUrl = "https://example.invalid/task-documentation";

    [Theory]
    [InlineData("screenShareUrl")]
    [InlineData("ScreenShareUrl")]
    public void RemovesBackendUrlAndPreservesSessionAndTaskLinks(string propertyName)
    {
        var original = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["sessionId"] = "session-1",
            [propertyName] = BackendUrl,
            ["documentationUrl"] = TaskUrl,
            ["status"] = "ready",
            ["optional"] = null
        });

        var result = ResponsesOrchestrator.RemoveScreenShareUrlsFromModelResult(original);

        using var filtered = JsonDocument.Parse(result);
        Assert.False(filtered.RootElement.TryGetProperty(propertyName, out _));
        Assert.Equal("session-1", filtered.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(TaskUrl, filtered.RootElement.GetProperty("documentationUrl").GetString());
        Assert.Equal("ready", filtered.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, filtered.RootElement.GetProperty("optional").ValueKind);
        using var retained = JsonDocument.Parse(original);
        Assert.Equal(BackendUrl, retained.RootElement.GetProperty(propertyName).GetString());
    }

    [Fact]
    public void RemovesUrlsInsideMcpTextAndNestedSessionArrays()
    {
        var inner = JsonSerializer.Serialize(new
        {
            sessions = new[] { new { sessionId = "session-1", screenShareUrl = BackendUrl } },
            documentationUrl = TaskUrl
        });
        var original = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = inner } },
            isError = false
        });

        var result = ResponsesOrchestrator.RemoveScreenShareUrlsFromModelResult(original);

        using var outer = JsonDocument.Parse(result);
        Assert.False(outer.RootElement.GetProperty("isError").GetBoolean());
        var block = outer.RootElement.GetProperty("content")[0];
        Assert.Equal("text", block.GetProperty("type").GetString());
        using var payload = JsonDocument.Parse(block.GetProperty("text").GetString()!);
        var session = payload.RootElement.GetProperty("sessions")[0];
        Assert.Equal("session-1", session.GetProperty("sessionId").GetString());
        Assert.False(session.TryGetProperty("screenShareUrl", out _));
        Assert.Equal(TaskUrl, payload.RootElement.GetProperty("documentationUrl").GetString());
    }

    [Theory]
    [InlineData("(no result)")]
    [InlineData("not JSON: https://example.invalid/task-documentation")]
    [InlineData("{invalid-json")]
    [InlineData("null")]
    [InlineData("{ \"status\": \"ready\" }")]
    public void LeavesResultsWithoutBackendUrlUnchanged(string original)
    {
        Assert.Equal(original, ResponsesOrchestrator.RemoveScreenShareUrlsFromModelResult(original));
    }
}
