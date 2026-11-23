// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.W365APlaygroundAgent.Auth;
using Moq;
using Xunit;

namespace Microsoft.W365APlaygroundAgent.Tests;

public class McpAuthenticationTests
{
    internal const string Tenant = "11111111-1111-1111-1111-111111111111";
    internal const string Agent = "22222222-2222-2222-2222-222222222222";
    internal const string Audience = "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1";
    internal static readonly McpTokenKey Key = new(Tenant, "human", Agent, "agent-user", $"{Audience}/.default");
    internal static readonly McpIdentity Identity = new(Tenant, "human", Agent, "agent-user", "conversation", false);

    [Fact]
    public async Task WarmRequestsReuseTokenAndRefreshBeforeExpiryWithoutRecreatingTheHandler()
    {
        var clock = new Clock();
        var initial = Token(clock.GetUtcNow().AddMinutes(2));
        var refreshed = Token(clock.GetUtcNow().AddHours(1));
        var acquisitions = 0;
        using var wire = new Wire();
        using var http = Client(new McpTokenCache(clock, NullLogger<McpTokenCache>.Instance), initial,
            _ => { acquisitions++; return Task.FromResult(refreshed); }, wire);
        for (var i = 0; i < 20; i++)
        {
            using var response = await http.GetAsync("https://mcp.invalid");
        }
        Assert.Equal(0, acquisitions);
        Assert.All(wire.Tokens, value => Assert.Equal(initial, value));

        clock.Advance(TimeSpan.FromSeconds(60));
        using var renewed = await http.GetAsync("https://mcp.invalid");
        Assert.Equal(1, acquisitions);
        Assert.Equal(refreshed, wire.Tokens.Last());
    }

    [Fact]
    public async Task ConcurrentRenewalsShareOneAcquisitionAndCancelledWaiterDoesNotCancelIt()
    {
        var clock = new Clock();
        var cache = new McpTokenCache(clock, NullLogger<McpTokenCache>.Instance);
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisitions = 0;
        Task<string> Acquire(CancellationToken ct)
        {
            Interlocked.Increment(ref acquisitions);
            started.TrySetResult();
            return ready.Task.WaitAsync(ct);
        }
        var owner = cache.GetAsync(Key, Acquire, null, CancellationToken.None);
        await started.Task;
        using var cancellation = new CancellationTokenSource();
        var cancelled = cache.GetAsync(Key, Acquire, null, cancellation.Token);
        var waiters = Enumerable.Range(0, 20)
            .Select(_ => cache.GetAsync(Key, Acquire, null, CancellationToken.None)).ToArray();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        ready.SetResult(Token(clock.GetUtcNow().AddHours(1)));
        await Task.WhenAll(waiters.Append(owner));
        Assert.Equal(1, acquisitions);
        Assert.All(waiters, task => Assert.Same(owner.Result, task.Result));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("margin")]
    [InlineData("future")]
    [InlineData("malformed")]
    [InlineData("tenant")]
    [InlineData("audience")]
    [InlineData("provider-error")]
    public async Task UnusableCredentialsNeverReachTheWireAndDoNotLeakIntoErrors(string kind)
    {
        var clock = new Clock();
        var log = new CapturedLog();
        var invalid = kind switch
        {
            "expired" => Token(clock.GetUtcNow().AddSeconds(-1)),
            "margin" => Token(clock.GetUtcNow().AddSeconds(60)),
            "future" => Token(clock.GetUtcNow().AddHours(1), notBefore: clock.GetUtcNow().AddMinutes(5)),
            "tenant" => Token(clock.GetUtcNow().AddHours(1), tenant: "other"),
            "audience" => Token(clock.GetUtcNow().AddHours(1), audience: "other"),
            _ => "SENSITIVE_INVALID_TOKEN"
        };
        using var wire = new Wire();
        using var http = Client(new McpTokenCache(clock, log), Token(clock.GetUtcNow().AddSeconds(-1)),
            _ => kind == "provider-error"
                ? Task.FromException<string>(new InvalidOperationException(invalid))
                : Task.FromResult(invalid), wire);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync("https://mcp.invalid"));
        Assert.Empty(wire.Tokens);
        Assert.DoesNotContain(invalid, exception.ToString());
        Assert.DoesNotContain(invalid, string.Join("\n", log.Messages));
        Assert.Contains(log.Messages, message => message.Contains("McpTokenRefreshFailed"));
    }

    [Fact]
    public async Task RefreshCancellationReleasesTheGateAndNeverSendsTheExpiredToken()
    {
        var clock = new Clock();
        var cache = new McpTokenCache(clock, NullLogger<McpTokenCache>.Instance);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Acquire(CancellationToken ct)
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        }
        using var wire = new Wire();
        using var http = Client(cache, Token(clock.GetUtcNow().AddSeconds(-1)), Acquire, wire);
        var pending = http.GetAsync("https://mcp.invalid", cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(wire.Tokens);
        var fresh = Token(clock.GetUtcNow().AddHours(1));
        Assert.Equal(fresh, (await cache.GetAsync(Key, _ => Task.FromResult(fresh), null, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task CacheSeparatesTenantCallerAgentSubjectAndScope()
    {
        var clock = new Clock();
        var cache = new McpTokenCache(clock, NullLogger<McpTokenCache>.Instance);
        var keys = new[]
        {
            Key, Key with { Tenant = "other-tenant" }, Key with { Caller = "other-human" },
            Key with { Agent = "other-agent" }, Key with { Subject = "other-user" },
            Key with { Scope = "other-audience/.default" }
        };
        var acquisitions = 0;
        foreach (var key in keys)
        {
            var value = Token(clock.GetUtcNow().AddHours(1), key.Tenant, key.Scope[..key.Scope.LastIndexOf('/')]);
            var result = await cache.GetAsync(key, _ => { acquisitions++; return Task.FromResult(value); },
                null, CancellationToken.None);
            Assert.Equal(value, result.Value);
        }
        Assert.Equal(keys.Length, acquisitions);
    }

    [Fact]
    public async Task BoundedCacheEvictsIdleEntriesWithoutEvictingAnActiveRefresh()
    {
        var clock = new Clock();
        var cache = new McpTokenCache(clock, NullLogger<McpTokenCache>.Instance);
        var fresh = Token(clock.GetUtcNow().AddHours(1));
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cache.GetAsync(Key, _ => source.Task, null, CancellationToken.None);
        for (var i = 0; i <= McpTokenCache.Capacity; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await cache.GetAsync(Key with { Caller = $"human-{i}" }, _ => Task.FromResult(fresh),
                null, CancellationToken.None);
        }
        var waiter = cache.GetAsync(Key, _ => throw new InvalidOperationException("Duplicate acquisition"),
            null, CancellationToken.None);
        source.SetResult(fresh);
        Assert.Same(await pending, await waiter);

        var reacquired = false;
        await cache.GetAsync(Key with { Caller = "human-0" },
            _ => { reacquired = true; return Task.FromResult(fresh); }, null, CancellationToken.None);
        Assert.True(reacquired);
    }

    [Fact]
    public async Task RequestHeadersUseCurrentTurnAndInheritedContextIsClearedForCleanup()
    {
        var clock = new Clock();
        var first = Context("first message");
        var second = Context("second\r\nmessage");
        using var wire = new Wire();
        using var http = Client(new McpTokenCache(clock, NullLogger<McpTokenCache>.Instance),
            Token(clock.GetUtcNow().AddHours(1)), _ => throw new InvalidOperationException(), wire);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task inherited;
        using (McpRequestContext.Enter(first.Object))
        {
            using var response = await http.GetAsync("https://mcp.invalid");
            inherited = Task.Run(async () =>
            {
                await release.Task;
                Assert.Null(McpRequestContext.Current);
                using var background = await http.GetAsync("https://mcp.invalid");
            });
        }
        first.SetupGet(c => c.Activity).Throws(new ObjectDisposedException("completed turn"));
        release.SetResult();
        await inherited;
        using (McpRequestContext.Enter(second.Object))
        {
            using var response = await http.GetAsync("https://mcp.invalid");
        }
        Assert.Equal(new string?[] { "first message", null, "second message" }, wire.Messages);
        Assert.All(wire.Conversations, value => Assert.Equal("conversation", value));
    }

    [Fact]
    public async Task DifferentActiveConversationCannotUseAnotherConversationsTransport()
    {
        var clock = new Clock();
        using var wire = new Wire();
        using var http = Client(new McpTokenCache(clock, NullLogger<McpTokenCache>.Instance),
            Token(clock.GetUtcNow().AddHours(1)), _ => throw new InvalidOperationException(), wire);
        using var scope = McpRequestContext.Enter(Context("message", "another-conversation").Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync("https://mcp.invalid"));
        Assert.Empty(wire.Tokens);
    }

    [Fact]
    public async Task SuccessfulTelemetryDescribesActualTokenButNeverItsValue()
    {
        var clock = new Clock();
        var log = new CapturedLog();
        var value = Token(clock.GetUtcNow().AddHours(1));
        var result = await new McpTokenCache(clock, log).GetAsync(Key, _ => Task.FromResult(value),
            null, CancellationToken.None);
        var text = string.Join("\n", log.Messages);
        Assert.Contains(Audience, text);
        Assert.Contains("remainingSeconds=3600", text);
        Assert.Contains("source=provider", text);
        Assert.DoesNotContain(value, text);
        Assert.DoesNotContain(value, result.ToString());
    }

    internal static Mock<ITurnContext> Context(string message = "message", string conversation = "conversation")
    {
        var context = new Mock<ITurnContext>();
        context.SetupGet(c => c.Activity).Returns(new Activity
        {
            Text = message,
            ChannelId = Channels.Msteams,
            From = new ChannelAccount { Id = "human", AadObjectId = "human" },
            Recipient = new ChannelAccount
            {
                Role = RoleTypes.AgenticUser, AgenticAppId = Agent, AgenticUserId = "agent-user", TenantId = Tenant
            },
            Conversation = new ConversationAccount { Id = conversation, TenantId = Tenant }
        });
        context.SetupGet(c => c.StackState).Returns(new TurnContextStateCollection());
        return context;
    }

    internal static string Token(DateTimeOffset expiry, string tenant = Tenant,
        string audience = Audience, DateTimeOffset? notBefore = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["exp"] = expiry.ToUnixTimeSeconds(), ["tid"] = tenant, ["aud"] = audience, ["azp"] = Agent
        };
        if (notBefore is not null)
        {
            claims["nbf"] = notBefore.Value.ToUnixTimeSeconds();
        }
        static string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Encode("""{"alg":"RS256","typ":"JWT"}""")}.{Encode(JsonSerializer.Serialize(claims))}.test";
    }

    internal sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    private static HttpClient Client(McpTokenCache cache, string initial,
        Func<CancellationToken, Task<string>> acquire, HttpMessageHandler wire) =>
        new(new RefreshingMcpHandler(cache, Key, Identity, acquire, initial, "blueprint", "msteams", null, null)
        { InnerHandler = wire });

    private sealed class Wire : HttpMessageHandler
    {
        public ConcurrentQueue<string?> Tokens { get; } = new();
        public ConcurrentQueue<string?> Messages { get; } = new();
        public ConcurrentQueue<string?> Conversations { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Tokens.Enqueue(request.Headers.Authorization?.Parameter);
            Messages.Enqueue(request.Headers.TryGetValues("x-ms-usermessage", out var messages) ? messages.Single() : null);
            Conversations.Enqueue(request.Headers.TryGetValues("x-ms-conversation-id", out var ids) ? ids.Single() : null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class CapturedLog : ILogger<McpTokenCache>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
