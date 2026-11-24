// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.A365.Tooling.Models;
using Microsoft.Agents.A365.Tooling.Services;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.W365APlaygroundAgent.ComputerUse;
using Microsoft.W365APlaygroundAgent.Auth;
using Microsoft.W365APlaygroundAgent.Screenshare;
using Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using Xunit;

namespace Microsoft.W365APlaygroundAgent.Tests;

public class McpSessionRecoveryTests
{
    private const string Start = "mcp_W365ComputerUse_StartSession";
    private const string End = "mcp_W365ComputerUse_EndSession";
    private const string Details = "mcp_W365ComputerUse_GetSessionDetails";
    private const string CloudPcId = "cloud-pc-1";
    private const string ScreenShareUrl = "https://computer.invalid/computers/one/screenshare?api-version=test";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoveryTracksTokenAndTransportFailuresBeforeRetrying(bool failToken)
    {
        var clock = new McpAuthenticationTests.Clock();
        const string otherAudience = "33333333-3333-3333-3333-333333333333";
        var failing = true;
        var initial = McpAuthenticationTests.Token(clock.GetUtcNow().AddHours(1));
        var connection = new Mock<IAccessTokenProvider>(MockBehavior.Strict);
        connection.As<IAgenticTokenProvider>();
        var connections = new Mock<IConnections>(MockBehavior.Strict);
        connections.Setup(p => p.GetConnection("test")).Returns(connection.Object);
        var provider = new Mock<IMcpTokenProvider>(MockBehavior.Strict);
        provider.Setup(p => p.GetTokenAsync(It.IsAny<MCPServerConfig>(), It.IsAny<CancellationToken>()))
            .Returns((MCPServerConfig server, CancellationToken _) =>
                failing && failToken && server.mcpServerName == "dynamic-secondary"
                    ? Task.FromException<string>(new InvalidOperationException("Simulated token acquisition failure"))
                    : Task.FromResult(McpAuthenticationTests.Token(clock.GetUtcNow().AddHours(1), audience: server.audience!)));

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        await using var wire = new McpServer { ToolNames = ["renamed_action", "new_action"] };
        using var invoker = new HttpMessageInvoker(wire, disposeHandler: false);
        app.Run(async context =>
        {
            if (failing && !failToken && context.Request.Path.StartsWithSegments("/secondary"))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), "https://mcp.invalid")
            { Content = new StreamContent(context.Request.Body) };
            foreach (var header in context.Request.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            using var response = await invoker.SendAsync(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            if (response.Content.Headers.ContentType is { } contentType)
            {
                context.Response.ContentType = contentType.ToString();
            }
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await app.StartAsync();
        using var discoveryHandler = new DiscoveryHandler(JsonSerializer.SerializeToNode(new[]
        {
            new { id = "primary", mcpServerName = "dynamic-primary", url = $"{app.Urls.Single()}/primary", audience = McpAuthenticationTests.Audience },
            new { id = "secondary", mcpServerName = "dynamic-secondary", url = $"{app.Urls.Single()}/secondary", audience = otherAudience }
        })!);
        var discoveryClients = new List<HttpClient>();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() =>
        {
            var http = new HttpClient(discoveryHandler, disposeHandler: false);
            discoveryClients.Add(http);
            return http;
        });
        using var services = new ServiceCollection().BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["AgentApplication:AgenticAuthHandlerName"] = "agentic",
            ["AgentApplication:UserAuthorization:Handlers:agentic:Settings:AlternateBlueprintConnectionName"] = "test"
        }).Build();
        var service = new RefreshingMcpToolServerConfigurationService(
            NullLogger<IMcpToolServerConfigurationService>.Instance, config, services, factory.Object,
            connections.Object, new McpTokenCache(clock, NullLogger<McpTokenCache>.Instance));
        var clients = new HashSet<IMcpClient>();
        try
        {
            foreach (var recovered in new[] { false, true })
            {
                failing = !recovered;
                using var scope = McpDiscoveryScope.Begin();
                var result = await service.EnumerateToolsFromServersAsync(McpAuthenticationTests.Agent,
                    initial, provider.Object, McpAuthenticationTests.Context().Object,
                    new ToolOptions { McpClientInitializationTimeoutSeconds = 5 });
                foreach (var tools in result.ToolsByServer.Values)
                {
                    clients.Add(Assert.IsAssignableFrom<IMcpClient>(typeof(McpClientTool)
                        .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tools[0])));
                }
                Assert.Equal(2, scope.DiscoveredServerCount);
                Assert.Equal(recovered ? 2 : 1, scope.LoadedServerCount);
                Assert.Equal(recovered, scope.IsComplete);
                Assert.Contains("dynamic-primary", result.ToolsByServer.Keys);
                Assert.All(result.ToolsByServer.Values, tools =>
                    Assert.Contains(tools, tool => tool.Name == "renamed_action"));
                if (failToken && !recovered)
                {
                    Assert.Single(result.Servers);
                }
            }
            Assert.Equal(2, discoveryHandler.Requests);
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
            foreach (var http in discoveryClients)
            {
                http.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("named")]
    [InlineData("mapped")]
    [InlineData("bearer")]
    [InlineData("nonagentic")]
    public async Task ServiceUsesPerAudienceCredentialsAndPreservesLegacyAuthentication(string mode)
    {
        var clock = new McpAuthenticationTests.Clock();
        const string audience = "33333333-3333-3333-3333-333333333333";
        var initial = McpAuthenticationTests.Token(clock.GetUtcNow().AddMinutes(2), audience: audience);
        var fresh = McpAuthenticationTests.Token(clock.GetUtcNow().AddHours(1), audience: audience);
        var connection = new Mock<IAccessTokenProvider>(MockBehavior.Strict);
        var agentic = connection.As<IAgenticTokenProvider>();
        agentic.Setup(p => p.GetAgenticUserTokenAsync(McpAuthenticationTests.Tenant, McpAuthenticationTests.Agent,
            "agent-user", It.Is<IList<string>>(s => s.SequenceEqual(new[] { $"api://{audience}/.default" })),
            It.IsAny<CancellationToken>())).ReturnsAsync(fresh);
        var connections = new Mock<IConnections>(MockBehavior.Strict);
        if (mode == "named")
        {
            connections.Setup(p => p.GetConnection("test-connection")).Returns(connection.Object);
        }
        else if (mode == "mapped")
        {
            connections.Setup(p => p.GetTokenProvider(It.IsAny<System.Security.Claims.ClaimsIdentity>(),
                It.IsAny<IActivity>())).Returns(connection.Object);
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentApplication:AgenticAuthHandlerName"] = "agentic",
            ["AgentApplication:UserAuthorization:Handlers:agentic:Settings:AlternateBlueprintConnectionName"] =
                mode == "named" ? "test-connection" : null,
            ["BEARER_TOKEN_MCP_TEST"] = mode == "bearer" ? initial : null
        }).Build();

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        await using var wire = new McpServer();
        using var invoker = new HttpMessageInvoker(wire, disposeHandler: false);
        app.Run(async context =>
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), "https://mcp.invalid")
            { Content = new StreamContent(context.Request.Body) };
            foreach (var header in context.Request.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            using var response = await invoker.SendAsync(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            if (response.Content.Headers.ContentType is { } contentType)
            {
                context.Response.ContentType = contentType.ToString();
            }
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await app.StartAsync();
        using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var service = new RefreshingMcpToolServerConfigurationService(
            NullLogger<IMcpToolServerConfigurationService>.Instance, config, services,
            services.GetRequiredService<IHttpClientFactory>(), connections.Object,
            new McpTokenCache(clock, NullLogger<McpTokenCache>.Instance));
        var turn = McpAuthenticationTests.Context();
        if (mode == "nonagentic")
        {
            turn.Object.Activity.Recipient.Role = "bot";
        }
        IList<McpClientTool> tools;
        using (McpRequestContext.Enter(turn.Object))
        {
            tools = await service.GetMcpClientToolsAsync(turn.Object, new MCPServerConfig
            {
                id = "test", mcpServerName = "mcp_test", url = app.Urls.Single(), audience = $"api://{audience}",
                Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {initial}" }
            }, McpAuthenticationTests.Token(clock.GetUtcNow().AddHours(1)), new ToolOptions());
        }
        // The application already uses this pinned SDK field for direct CUA calls.
        await using var client = Assert.IsAssignableFrom<IMcpClient>(typeof(McpClientTool)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tools[0]));
        var refreshable = mode is "named" or "mapped";
        if (refreshable)
        {
            turn.SetupGet(t => t.Activity).Throws(new ObjectDisposedException("completed turn"));
        }
        clock.Advance(TimeSpan.FromMinutes(2));
        using (McpRequestContext.Enter(McpAuthenticationTests.Context("second turn").Object))
        {
            await tools.Single(t => t.Name == Start).InvokeAsync(new AIFunctionArguments());
        }
        Assert.Equal(initial, Assert.Single(wire.Requests, r => r.Operation == "initialize").Token);
        var start = Assert.Single(wire.Requests, r => r.Operation == Start);
        Assert.Equal(refreshable ? fresh : initial, start.Token);
        Assert.Equal(refreshable ? "second turn" : "message", start.UserMessage);
        Assert.Equal(1, wire.Initializations);
        agentic.Verify(p => p.GetAgenticUserTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IList<string>>(), It.IsAny<CancellationToken>()), refreshable ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task TokenRenewalDuringCheckoutPreservesMcpAndCloudPcSessions()
    {
        var clock = new McpAuthenticationTests.Clock();
        var cache = new McpTokenCache(clock, NullLogger<McpTokenCache>.Instance);
        var initial = McpAuthenticationTests.Token(clock.GetUtcNow().AddMinutes(2));
        var fresh = McpAuthenticationTests.Token(clock.GetUtcNow().AddHours(1));
        var acquisitions = 0;
        await using var server = new McpServer
        {
            WrapTransport = wire => new RefreshingMcpHandler(cache, McpAuthenticationTests.Key,
                McpAuthenticationTests.Identity, _ => { acquisitions++; return Task.FromResult(fresh); },
                initial, "blueprint", "msteams", null, null) { InnerHandler = wire },
            OnOperation = operation => { if (operation == Start) clock.Advance(TimeSpan.FromMinutes(2)); }
        };
        using var context = McpRequestContext.Enter(McpAuthenticationTests.Context().Object);
        using var fixture = await TurnFixture.CreateAsync(server);
        await fixture.RunAsync();
        await fixture.RunAsync();
        await fixture.RunAsync();

        Assert.Equal(1, acquisitions);
        Assert.Equal(1, server.Initializations);
        Assert.Equal(initial, Assert.Single(server.Requests, r => r.Operation == Start).Token);
        var end = Assert.Single(server.Requests, r => r.Operation == End);
        Assert.Equal(fresh, end.Token);
        Assert.Equal(CloudPcId, end.CloudPcId);
        Assert.All(server.Requests.Where(r => r.Operation != "initialize"), r => Assert.Equal("mcp-1", r.TransportId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownCanRenewWithoutTurnContextAndHonorsCancellation(bool cancelFirst)
    {
        var clock = new McpAuthenticationTests.Clock();
        var cache = new McpTokenCache(clock, NullLogger<McpTokenCache>.Instance);
        var initial = McpAuthenticationTests.Token(clock.GetUtcNow().AddMinutes(2));
        var fresh = McpAuthenticationTests.Token(clock.GetUtcNow().AddHours(1));
        var blocked = cancelFirst;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Acquire(CancellationToken ct)
        {
            if (blocked)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return fresh;
        }
        await using var server = new McpServer
        {
            WrapTransport = wire => new RefreshingMcpHandler(cache, McpAuthenticationTests.Key,
                McpAuthenticationTests.Identity, Acquire, initial, "blueprint", "msteams", null, null)
            { InnerHandler = wire }
        };
        using var fixture = await TurnFixture.CreateAsync(server);
        using (McpRequestContext.Enter(McpAuthenticationTests.Context().Object))
        {
            await fixture.RunAsync();
        }
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(McpRequestContext.Current);
        if (cancelFirst)
        {
            using var cancellation = new CancellationTokenSource();
            var ending = fixture.Orchestrator.EndAllSessionsAsync(cancellation.Token);
            await started.Task;
            cancellation.Cancel();
            await ending;
            Assert.DoesNotContain(server.Requests, r => r.Operation == End);
            blocked = false;
        }
        await fixture.Orchestrator.EndAllSessionsAsync(CancellationToken.None);
        var end = Assert.Single(server.Requests, r => r.Operation == End);
        Assert.Equal(fresh, end.Token);
        Assert.Equal(CloudPcId, end.CloudPcId);
        Assert.Null(end.UserMessage);
        Assert.Equal(1, server.Initializations);
    }

    [Theory]
    [InlineData(HttpTransportMode.AutoDetect)]
    [InlineData(HttpTransportMode.StreamableHttp)]
    public async Task Session404ReinitializesWithoutOldHeaderAndRetriesOnlyRejectedRequest(HttpTransportMode mode)
    {
        await using var server = new McpServer { Mode = mode, FailureOperation = "rejected" };
        await server.ConnectAsync();
        var recovery = server.CreateRecovery();
        await recovery.ExecuteAsync("before", ct => server.CallAsync("before", ct), CancellationToken.None);
        await recovery.ExecuteAsync("rejected", async ct =>
        {
            try { return await server.CallAsync("rejected", ct); }
            catch (HttpRequestException ex) { throw new InvalidOperationException("Wrapped transport failure", ex); }
        }, CancellationToken.None);

        Assert.Equal(2, server.Initializations);
        Assert.All(server.Requests.Where(r => r.Operation == "initialize"), r => Assert.Null(r.TransportId));
        Assert.Single(server.Requests, r => r.Operation == "before");
        Assert.Equal(new[] { "mcp-1", "mcp-2" },
            server.Requests.Where(r => r.Operation == "rejected").Select(r => r.TransportId));
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(404, false)]
    public async Task OtherHttpErrorsAndSessionless404AreNotReplayed(int status, bool issueSessionId)
    {
        await using var server = new McpServer
        {
            FailureOperation = "operation",
            FailureStatus = (HttpStatusCode)status,
            IssueSessionId = issueSessionId
        };
        await server.ConnectAsync();
        var recovery = server.CreateRecovery();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            recovery.ExecuteAsync("operation", ct => server.CallAsync("operation", ct), CancellationToken.None));

        Assert.Equal(1, server.Initializations);
        Assert.Single(server.Requests, r => r.Operation == "operation");
    }

    [Fact]
    public async Task Domain404AndTimeoutDoNotTriggerReinitialization()
    {
        await using var server = new McpServer { ReturnToolError = true };
        await server.ConnectAsync();
        var recovery = server.CreateRecovery();

        var result = await recovery.ExecuteAsync("domain-error",
            ct => server.CallAsync("domain-error", ct), CancellationToken.None);
        Assert.True(result.IsError);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            recovery.ExecuteAsync<int>("timeout", _ => throw new TimeoutException(), CancellationToken.None));
        Assert.Equal(1, server.Initializations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRecoveryBlocksFurtherRequestsForTheTurn(bool failInitialization)
    {
        await using var server = new McpServer { FailureOperation = "operation", FailuresRemaining = 2 };
        await server.ConnectAsync();
        var recovery = server.CreateRecovery(failInitialization);

        await Assert.ThrowsAsync<McpSessionRecoveryException>(() =>
            recovery.ExecuteAsync("operation", ct => server.CallAsync("operation", ct), CancellationToken.None));
        var sent = server.Requests.Count;
        await Assert.ThrowsAsync<McpSessionRecoveryException>(() =>
            recovery.ExecuteAsync("later", ct => server.CallAsync("later", ct), CancellationToken.None));

        Assert.True(recovery.IsUnavailable);
        Assert.Null(server.CurrentClient);
        Assert.Equal(sent, server.Requests.Count);
        Assert.Equal(failInitialization ? 1 : 2, server.Initializations);
    }

    [Fact]
    public async Task RecoveryBudgetIsSharedAcrossRequestsWithinATurn()
    {
        await using var server = new McpServer { FailureOperation = "operation" };
        await server.ConnectAsync();
        var recovery = server.CreateRecovery();
        await recovery.ExecuteAsync("operation", ct => server.CallAsync("operation", ct), CancellationToken.None);
        server.FailuresRemaining = 1;

        await Assert.ThrowsAsync<McpSessionRecoveryException>(() =>
            recovery.ExecuteAsync("operation", ct => server.CallAsync("operation", ct), CancellationToken.None));

        Assert.Equal(2, server.Initializations);
        Assert.Equal(3, server.Requests.Count(r => r.Operation == "operation"));
    }

    [Theory]
    [InlineData(Start, 404)]
    [InlineData("scoped-tools/list", 404)]
    [InlineData("take_screenshot", 404)]
    [InlineData("clipboard_read", 404)]
    [InlineData("type_text", 404)]
    [InlineData(End, 404)]
    [InlineData(Start, 401)]
    public async Task OrchestratorRecoversEachMcpPathWithoutLosingCheckoutOrDuplicatingOutputs(
        string failureOperation, int status)
    {
        await using var server = new McpServer
        {
            FailureOperation = failureOperation,
            FailureStatus = (HttpStatusCode)status
        };
        using var fixture = await TurnFixture.CreateAsync(server);

        await fixture.RunAsync();
        await fixture.RunAsync();
        await fixture.RunAsync();

        Assert.Equal(2, server.Initializations);
        var accepted = server.Requests.Where(r => !r.Rejected).ToArray();
        Assert.Single(accepted, r => r.Operation == Start);
        Assert.Single(accepted, r => r.Operation == End);
        Assert.Single(accepted, r => r.Operation == "clipboard_read");
        Assert.Single(accepted, r => r.Operation == "click");
        Assert.Single(accepted, r => r.Operation == "type_text");
        Assert.Single(accepted, r => r.Operation == "move_mouse");
        Assert.All(server.Requests.Where(r => r.Operation is End or "take_screenshot"
            or "clipboard_read" or "click" or "type_text" or "move_mouse" or "scoped-tools/list"),
            r => Assert.Equal(CloudPcId, r.CloudPcId));
        var finalHistory = fixture.Model.Inputs.Last().EnumerateArray().ToArray();
        foreach (var callId in new[] { "start", "clipboard", "computer", "end" })
        {
            Assert.Single(finalHistory, item =>
                item.TryGetProperty("type", out var type)
                && type.GetString() is "function_call_output" or "computer_call_output"
                && item.GetProperty("call_id").GetString() == callId);
        }
        Assert.False(fixture.Orchestrator.ConsumeToolRefreshRequest("conversation"));
    }

    [Fact]
    public async Task UnrecoverableComputerActionStopsBatchAndPreservesPairedOutput()
    {
        await using var server = new McpServer { FailureOperation = "type_text", FailuresRemaining = 2 };
        using var fixture = await TurnFixture.CreateAsync(server);
        await fixture.RunAsync();
        await fixture.RunAsync();

        Assert.Single(server.Requests, r => r.Operation == "click");
        Assert.Equal(2, server.Requests.Count(r => r.Operation == "type_text"));
        Assert.DoesNotContain(server.Requests, r => r.Operation == "move_mouse");
        Assert.Single(fixture.Model.Inputs.Last().EnumerateArray(), item =>
            item.TryGetProperty("type", out var type)
            && type.GetString() == "computer_call_output"
            && item.GetProperty("call_id").GetString() == "computer");
        Assert.Contains("do not repeat", fixture.Model.Inputs.Last().GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Orchestrator.ConsumeToolRefreshRequest("conversation"));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(0)]
    public async Task NonSessionReplayFailurePreservesFreshClientForCheckIn(int status)
    {
        await using var server = new McpServer { FailureOperation = "operation" };
        await server.ConnectAsync();
        var recovery = server.CreateRecovery();
        Exception failure = status == 0
            ? new TimeoutException("Replay timed out")
            : new HttpRequestException("Replay rejected", null, (HttpStatusCode)status);
        var attempts = 0;

        var actual = await Assert.ThrowsAnyAsync<Exception>(() =>
            recovery.ExecuteAsync("operation", async ct =>
            {
                if (++attempts == 2)
                {
                    throw failure;
                }
                return await server.CallAsync("operation", ct);
            }, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.False(recovery.IsUnavailable);
        var freshClient = Assert.IsAssignableFrom<IMcpClient>(server.CurrentClient);
        await recovery.ExecuteAsync(End, ct => server.CallAsync(End, ct), CancellationToken.None);
        Assert.Same(freshClient, server.CurrentClient);
        Assert.Equal(2, server.Initializations);
        Assert.Equal("mcp-2", Assert.Single(server.Requests, r => r.Operation == End).TransportId);
    }

    [Theory]
    [InlineData("http429")]
    [InlineData("mcp-error")]
    [InlineData("missing-session")]
    public async Task FailedCheckoutDoesNotReofferAnOlderSession(string failureKind)
    {
        await using var server = new McpServer
        {
            FailureOperation = Start,
            FailureStatus = HttpStatusCode.TooManyRequests,
            FailuresRemaining = 0
        };
        using var fixture = await TurnFixture.CreateAsync(server);
        await fixture.RunAsync();
        Assert.Equal(CloudPcId, Assert.Single(fixture.OfferedSessions));
        fixture.OfferedSessions.Clear();
        var screenshotsBeforeFailure = server.Requests.Count(r => r.Operation == "take_screenshot");

        if (failureKind == "http429")
        {
            server.FailuresRemaining = 1;
        }
        else
        {
            server.StartResultOverride = JsonSerializer.SerializeToNode(new
            {
                isError = failureKind == "mcp-error",
                content = new[] { new
                {
                    type = "text",
                    text = failureKind == "mcp-error"
                        ? JsonSerializer.Serialize(new { sessionId = "error-only-session", screenShareUrl = ScreenShareUrl })
                        : "Checkout did not return a session ID."
                } }
            });
        }
        fixture.Model.SetOutputs(
            """[{"type":"function_call","call_id":"failed-checkout","name":"mcp_W365ComputerUse_StartSession","arguments":"{}"}]""",
            """[{"type":"function_call","call_id":"details","name":"mcp_W365ComputerUse_GetSessionDetails","arguments":"{}"}]""",
            "[]");

        await fixture.RunAsync();

        Assert.Empty(fixture.OfferedSessions);
        Assert.Equal(CloudPcId, Assert.Single(server.Requests, r => r.Operation == Details).CloudPcId);
        Assert.Equal(screenshotsBeforeFailure, server.Requests.Count(r => r.Operation == "take_screenshot"));
        Assert.Equal(1, server.Initializations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSessionReofferRequiresSuccessfulComputerWork(bool failComputerWork)
    {
        await using var server = new McpServer();
        using var fixture = await TurnFixture.CreateAsync(server);
        await fixture.RunAsync();
        Assert.Equal(CloudPcId, Assert.Single(fixture.OfferedSessions));
        fixture.OfferedSessions.Clear();
        server.ReturnToolError = failComputerWork;
        fixture.Model.SetOutputs(
            """[{"type":"computer_call","call_id":"computer","action":{"type":"click","x":1,"y":2}}]""",
            "[]");

        await fixture.RunAsync();

        if (failComputerWork)
        {
            Assert.Empty(fixture.OfferedSessions);
        }
        else
        {
            Assert.Equal(CloudPcId, Assert.Single(fixture.OfferedSessions));
        }
        Assert.Single(server.Requests, r => r.Operation == Start);
    }

    private sealed class TurnFixture : IDisposable
    {
        private readonly McpServer _server;
        private readonly HttpClient _modelClient;
        private readonly ITurnContext _context;
        private IList<AITool> _tools = [];
        public ScriptedModel Model { get; } = new();
        public ResponsesOrchestrator Orchestrator { get; }
        public List<string> OfferedSessions { get; } = [];

        private TurnFixture(McpServer server)
        {
            _server = server;
            _modelClient = new HttpClient(Model);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient("WebClient")).Returns(_modelClient);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AIServices:AzureOpenAI:Endpoint"] = "https://model.invalid",
                ["AIServices:AzureOpenAI:DeploymentName"] = "test-model",
                ["AIServices:AzureOpenAI:ApiKey"] = "test-key"
            }).Build();
            Orchestrator = new ResponsesOrchestrator(factory.Object, config,
                NullLogger<ResponsesOrchestrator>.Instance,
                Mock.Of<ITurnScopeAccessor>(), Mock.Of<IScreenshareTicketStore>());
            var context = new Mock<ITurnContext>();
            context.SetupGet(c => c.Activity).Returns(new Activity
            {
                ChannelId = Channels.Msteams,
                Recipient = new ChannelAccount { Role = RoleTypes.AgenticUser }
            });
            context.Setup(c => c.SendActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResourceResponse { Id = "test-message" });
            _context = context.Object;
        }

        public static async Task<TurnFixture> CreateAsync(McpServer server)
        {
            var fixture = new TurnFixture(server);
            await server.ConnectAsync();
            fixture._tools = await server.ListToolsAsync();
            return fixture;
        }

        public async Task RunAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Orchestrator.RunAsync("conversation", "test", "test", _tools, async ct =>
            {
                await _server.ConnectAsync(ct);
                _tools = await _server.ListToolsAsync(ct);
                return new ToolReacquireResult(_tools, true);
            }, _context, null, (sessionId, _, _) =>
            {
                OfferedSessions.Add(sessionId);
                return Task.CompletedTask;
            }, cts.Token);
        }

        public void Dispose() => _modelClient.Dispose();
    }

    private sealed class ScriptedModel : HttpMessageHandler
    {
        private readonly Queue<string> _outputs = new([
            """[{"type":"function_call","call_id":"start","name":"mcp_W365ComputerUse_StartSession","arguments":"{}"}]""",
            "[]",
            """[{"type":"function_call","call_id":"clipboard","name":"clipboard_read","arguments":"{}"},{"type":"computer_call","call_id":"computer","actions":[{"type":"click","x":1,"y":2},{"type":"type","text":"once"},{"type":"move","x":3,"y":4}]}]""",
            "[]",
            """[{"type":"function_call","call_id":"end","name":"mcp_W365ComputerUse_EndSession","arguments":"{}"}]""",
            "[]"
        ]);
        public List<JsonElement> Inputs { get; } = [];

        public void SetOutputs(params string[] outputs)
        {
            _outputs.Clear();
            foreach (var output in outputs)
            {
                _outputs.Enqueue(output);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Inputs.Add(document.RootElement.GetProperty("input").Clone());
            return JsonResponse(new JsonObject
            {
                ["id"] = $"response-{Inputs.Count}",
                ["output"] = JsonNode.Parse(_outputs.Dequeue())
            });
        }
    }

    private sealed record RecordedRequest(string Operation, string? TransportId, string? CloudPcId, bool Rejected,
        string? Token, string? UserMessage);

    private sealed class McpServer : HttpMessageHandler, IAsyncDisposable
    {
        private readonly List<IMcpClient> _clients = [];
        private readonly List<HttpClient> _httpClients = [];
        private readonly List<HttpMessageHandler> _wrappers = [];
        public Func<HttpMessageHandler, HttpMessageHandler>? WrapTransport { get; init; }
        public Action<string>? OnOperation { get; init; }
        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
        public IMcpClient? CurrentClient { get; set; }
        public HttpTransportMode Mode { get; init; } = HttpTransportMode.AutoDetect;
        public bool IssueSessionId { get; init; } = true;
        public bool ReturnToolError { get; set; }
        public string[]? ToolNames { get; init; }
        public JsonNode? StartResultOverride { get; set; }
        public string? FailureOperation { get; init; }
        public HttpStatusCode FailureStatus { get; init; } = HttpStatusCode.NotFound;
        public int FailuresRemaining { get; set; } = 1;
        public int Initializations { get; private set; }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            var handler = WrapTransport?.Invoke(this) ?? this;
            if (!ReferenceEquals(handler, this))
            {
                _wrappers.Add(handler);
            }
            var http = new HttpClient(handler, disposeHandler: false);
            _httpClients.Add(http);
            CurrentClient = await McpClientFactory.CreateAsync(new SseClientTransport(
                new SseClientTransportOptions { Endpoint = new Uri("https://mcp.invalid"), TransportMode = Mode }, http),
                cancellationToken: ct);
            _clients.Add(CurrentClient);
        }

        public async Task<IList<AITool>> ListToolsAsync(CancellationToken ct = default) =>
            (await CurrentClient!.ListToolsAsync(cancellationToken: ct)).Cast<AITool>().ToList();

        public async Task<CallToolResponse> CallAsync(string name, CancellationToken ct) =>
            await CurrentClient!.CallToolAsync(name, cancellationToken: ct);

        public McpSessionRecovery CreateRecovery(bool failInitialization = false) => new(
            () => CurrentClient, () => CurrentClient = null,
            ct => failInitialization
                ? Task.FromException(new HttpRequestException("Initialization failed"))
                : ConnectAsync(ct),
            NullLogger.Instance);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
            var rpc = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            var method = rpc["method"]!.GetValue<string>();
            var transportId = request.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.Single() : null;
            if (method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            var scoped = rpc["params"]?["_meta"]?["sessionId"]?.GetValue<string>();
            var operation = method == "tools/call" ? rpc["params"]!["name"]!.GetValue<string>()
                : scoped is not null ? "scoped-tools/list" : method;
            var cloudPcId = scoped ?? rpc["params"]?["arguments"]?["sessionId"]?.GetValue<string>();
            var reject = operation == FailureOperation && FailuresRemaining > 0;
            Requests.Enqueue(new RecordedRequest(operation, transportId, cloudPcId, reject,
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("x-ms-usermessage", out var messages) ? messages.Single() : null));
            OnOperation?.Invoke(operation);
            if (reject)
            {
                FailuresRemaining--;
                return new HttpResponseMessage(FailureStatus);
            }

            JsonNode result;
            if (method == "initialize")
            {
                Initializations++;
                result = new JsonObject
                {
                    ["protocolVersion"] = rpc["params"]!["protocolVersion"]!.DeepClone(),
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" }
                };
            }
            else if (method == "tools/list")
            {
                var names = scoped is null ? ToolNames ?? [Start, End, Details] : ["clipboard_read"];
                result = JsonSerializer.SerializeToNode(new
                {
                    tools = names.Select(name => new { name, inputSchema = new { type = "object", properties = new { } } })
                })!;
            }
            else if (operation == Start && StartResultOverride is not null)
            {
                result = StartResultOverride.DeepClone();
            }
            else if (operation == "take_screenshot" && !ReturnToolError)
            {
                result = JsonNode.Parse("""{"content":[{"type":"image","data":"AQID","mimeType":"image/png"}]}""")!;
            }
            else
            {
                result = JsonSerializer.SerializeToNode(new
                {
                    isError = ReturnToolError,
                    content = new[] { new
                    {
                        type = "text",
                        text = ReturnToolError ? "404 Cloud PC not found"
                            : operation == Start ? JsonSerializer.Serialize(new { sessionId = CloudPcId, screenShareUrl = ScreenShareUrl }) : "ok"
                    } }
                })!;
            }
            var response = JsonResponse(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = rpc["id"]!.DeepClone(),
                ["result"] = result
            });
            if (method == "initialize" && IssueSessionId)
            {
                response.Headers.Add("Mcp-Session-Id", $"mcp-{Initializations}");
            }
            return response;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients)
            {
                await client.DisposeAsync();
            }
            foreach (var http in _httpClients)
            {
                http.Dispose();
            }
            foreach (var handler in _wrappers)
            {
                handler.Dispose();
            }
            Dispose();
        }
    }

    private sealed class DiscoveryHandler(JsonNode servers) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Requests++;
            return Task.FromResult(JsonResponse(servers));
        }
    }

    private static HttpResponseMessage JsonResponse(JsonNode body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
    };
}
