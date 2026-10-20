// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.W365APlaygroundAgent.ComputerUse;
using Microsoft.W365APlaygroundAgent.Screenshare;
using Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;
using Moq;
using Xunit;

namespace Microsoft.W365APlaygroundAgent.Tests;

public class ResponsesOrchestratorCacheTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(120)]
    public async Task NewConversationReclaimsIdleOverflowInLruOrder(int initialCount)
    {
        using var client = new HttpClient(new EmptyModelHandler());
        var (orchestrator, cache) = Create(client, initialCount, busyCount: 0);

        await RunTurnAsync(orchestrator, "new-conversation");

        Assert.Equal(100, cache.Count);
        Assert.True(cache.Contains("new-conversation"));
        var removed = initialCount - 99;
        for (var i = 0; i < initialCount; i++)
        {
            Assert.Equal(i >= removed, cache.Contains($"conversation-{i}"));
        }
    }

    [Fact]
    public async Task AdmissionRemovesAllAvailableIdleStatesButPreservesBusyStates()
    {
        using var client = new HttpClient(new EmptyModelHandler());
        var (orchestrator, cache) = Create(client, total: 120, busyCount: 100);

        await RunTurnAsync(orchestrator, "new-conversation");

        Assert.Equal(101, cache.Count);
        Assert.True(cache.Contains("new-conversation"));
        for (var i = 0; i < 120; i++)
        {
            Assert.Equal(i < 100, cache.Contains($"conversation-{i}"));
        }
    }

    [Fact]
    public async Task AdmissionAllowsOverflowWhenEveryExistingStateIsBusy()
    {
        using var client = new HttpClient(new EmptyModelHandler());
        var (orchestrator, cache) = Create(client, total: 120, busyCount: 120);

        await RunTurnAsync(orchestrator, "new-conversation");

        Assert.Equal(121, cache.Count);
        Assert.True(cache.Contains("new-conversation"));
        for (var i = 0; i < 120; i++)
        {
            Assert.True(cache.Contains($"conversation-{i}"));
        }
    }

    [Fact]
    public async Task ExistingConversationDoesNotTriggerAdmissionEviction()
    {
        using var client = new HttpClient(new EmptyModelHandler());
        var (orchestrator, cache) = Create(client, total: 120, busyCount: 0);
        var original = cache["conversation-119"];

        await RunTurnAsync(orchestrator, "conversation-119");

        Assert.Equal(120, cache.Count);
        Assert.Same(original, cache["conversation-119"]);
    }

    private static (ResponsesOrchestrator Orchestrator, IDictionary Cache) Create(
        HttpClient client, int total, int busyCount)
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(f => f.CreateClient("WebClient")).Returns(client);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AIServices:AzureOpenAI:Endpoint"] = "https://model.invalid",
                ["AIServices:AzureOpenAI:DeploymentName"] = "test-model",
                ["AIServices:AzureOpenAI:ApiKey"] = "test-key"
            }).Build();
        var orchestrator = new ResponsesOrchestrator(factory.Object, configuration,
            NullLogger<ResponsesOrchestrator>.Instance,
            Mock.Of<ITurnScopeAccessor>(), Mock.Of<IScreenshareTicketStore>());

        var field = typeof(ResponsesOrchestrator).GetField(
            "_conversations", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Conversation cache field not found.");
        var cache = Assert.IsAssignableFrom<IDictionary>(field.GetValue(orchestrator));
        var stateType = field.FieldType.GetGenericArguments()[1];
        var busy = stateType.GetProperty("ActiveOrQueuedTurns")
            ?? throw new InvalidOperationException("Conversation usage counter not found.");
        var accessed = stateType.GetProperty("LastAccessed")
            ?? throw new InvalidOperationException("Conversation access time not found.");
        for (var i = 0; i < total; i++)
        {
            var state = Activator.CreateInstance(stateType, nonPublic: true)
                ?? throw new InvalidOperationException("Cannot create conversation state.");
            busy.SetValue(state, i < busyCount ? 1 + i % 2 : 0);
            accessed.SetValue(state, DateTime.UnixEpoch.AddSeconds(i));
            cache.Add($"conversation-{i}", state);
        }
        return (orchestrator, cache);
    }

    private static Task RunTurnAsync(ResponsesOrchestrator orchestrator, string key)
    {
        var context = new Mock<ITurnContext>();
        context.SetupGet(c => c.Activity).Returns(new Activity
        {
            ChannelId = Channels.Msteams,
            Recipient = new ChannelAccount { Role = RoleTypes.AgenticUser }
        });
        return Task.Run(() => orchestrator.RunAsync(
            key, "Synthetic cache test", "Test instructions", [],
            _ => throw new InvalidOperationException("Unexpected tool reacquisition."),
            context.Object, null, null, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class EmptyModelHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"test-response\",\"output\":[]}",
                    Encoding.UTF8, "application/json")
            });
    }
}
