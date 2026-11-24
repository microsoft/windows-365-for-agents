// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.W365APlaygroundAgent.ComputerUse;
using Microsoft.W365APlaygroundAgent.Screenshare;
using Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;
using Moq;
using Xunit;

namespace Microsoft.W365APlaygroundAgent.Tests;

public class ModelResilienceTests
{
    [Fact]
    public async Task TimeoutRetriesWithLongerAttempt()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (attempt, ct) =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(attempt == 1 ? 60 : 119));
            ct.ThrowIfCancellationRequested();
            return Success();
        };

        await fixture.RunAsync();

        Assert.Equal(2, fixture.Handler.Bodies.Count);
        Assert.Equal(fixture.Handler.Bodies[0], fixture.Handler.Bodies[1]);
        Assert.Equal(TimeSpan.FromSeconds(179).Ticks, fixture.Clock.GetTimestamp());
    }

    [Fact]
    public async Task BothTimeoutsStopAtTotalBudgetWithoutThirdAttempt()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (attempt, ct) =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(attempt == 1 ? 60 : 120));
            ct.ThrowIfCancellationRequested();
            return Success();
        };

        var error = await Assert.ThrowsAsync<TimeoutException>(() => fixture.RunAsync());

        Assert.Contains("180-second", error.Message);
        Assert.Equal(2, fixture.Handler.Bodies.Count);
        Assert.Equal(TimeSpan.FromSeconds(180).Ticks, fixture.Clock.GetTimestamp());
    }

    [Fact]
    public async Task TransientRetryDoesNotReplayCompletedTool()
    {
        using var fixture = new Fixture();
        var executions = 0;
        fixture.Tools.Add(AIFunctionFactory.Create(() => ++executions, name: "record_action"));
        fixture.Handler.Respond = (attempt, _) => attempt switch
        {
            1 => Success("""[{"type":"function_call","call_id":"action","name":"record_action","arguments":"{}"}]"""),
            2 => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => Success()
        };

        var running = fixture.RunAsync();
        await fixture.Clock.WaitForBackoffAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, executions);
        Assert.Equal(3, fixture.Handler.Bodies.Count);
        Assert.Equal(fixture.Handler.Bodies[1], fixture.Handler.Bodies[2]);
    }

    [Fact]
    public async Task BackoffCountsAgainstTotalBudget()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (attempt, ct) =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(attempt == 1 ? 59 : 118));
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };

        var running = fixture.RunAsync();
        await fixture.Clock.WaitForBackoffAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => running);
        Assert.Contains("180-second", error.Message);
        Assert.Equal(2, fixture.Handler.Bodies.Count);
        Assert.Equal(TimeSpan.FromSeconds(180).Ticks, fixture.Clock.GetTimestamp());
    }

    [Fact]
    public async Task PersistentServerErrorStopsAfterOneRetry()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var running = fixture.RunAsync();
        await fixture.Clock.WaitForBackoffAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<HttpRequestException>(() => running);
        Assert.Equal(2, fixture.Handler.Bodies.Count);
    }

    [Fact]
    public async Task TransientConnectionFailureRetriesOnce()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (attempt, _) => attempt == 1
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "test connection reset")
            : Success();
        var running = fixture.RunAsync();
        await fixture.Clock.WaitForBackoffAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));

        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.Handler.Bodies.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task NonTransientStatusDoesNotRetry(int status)
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (_, _) => new HttpResponseMessage((HttpStatusCode)status);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.RunAsync());

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Single(fixture.Handler.Bodies);
    }

    [Fact]
    public async Task CallerCancellationDuringBackoffDoesNotRetry()
    {
        using var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        fixture.Handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var running = fixture.RunAsync(caller.Token);
        await fixture.Clock.WaitForBackoffAsync();

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Single(fixture.Handler.Bodies);
    }

    [Fact]
    public async Task RetryAfterBeyondBudgetIsNotShortened()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(181));
            return response;
        };

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.RunAsync());

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Single(fixture.Handler.Bodies);
        Assert.Equal(0L, fixture.Clock.GetTimestamp());
    }

    [Fact]
    public async Task RateLimitRetainsFourAttemptLimit()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var running = fixture.RunAsync();
        for (var retry = 0; retry < 3; retry++)
        {
            await fixture.Clock.WaitForBackoffAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(Math.Pow(2, retry + 1) + 1));
        }

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => running);

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(4, fixture.Handler.Bodies.Count);
    }

    private static HttpResponseMessage Success(string output = "[]") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"id":"response","output":{{output}}}""", Encoding.UTF8, "application/json")
    };

    private sealed class Fixture : IDisposable
    {
        public ManualClock Clock { get; } = new();
        public ScriptedHandler Handler { get; } = new();
        public List<AITool> Tools { get; } = [];
        private readonly HttpClient _client;
        private readonly ResponsesOrchestrator _orchestrator;
        private readonly ITurnContext _context;

        public Fixture()
        {
            _client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient("WebClient")).Returns(_client);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AIServices:AzureOpenAI:Endpoint"] = "https://model.invalid",
                ["AIServices:AzureOpenAI:DeploymentName"] = "test-model",
                ["AIServices:AzureOpenAI:ApiKey"] = "test-key"
            }).Build();
            _orchestrator = new ResponsesOrchestrator(factory.Object, config,
                NullLogger<ResponsesOrchestrator>.Instance, Mock.Of<ITurnScopeAccessor>(),
                Mock.Of<IScreenshareTicketStore>(), Clock);
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

        public Task RunAsync(CancellationToken cancellationToken = default) =>
            _orchestrator.RunAsync("conversation", "test", "test", Tools,
                _ => throw new InvalidOperationException("Model recovery must not reacquire MCP tools."),
                _context, null, null, cancellationToken);

        public void Dispose() => _client.Dispose();
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public Func<int, CancellationToken, HttpResponseMessage> Respond { get; set; } = (_, _) => Success();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return Respond(Bodies.Count, ct);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ClockTimer> _timers = [];
        private readonly Channel<TimeSpan> _backoffs = global::System.Threading.Channels.Channel.CreateUnbounded<TimeSpan>();
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                timer.Change(dueTime, period);
            }
            if (dueTime > TimeSpan.Zero && dueTime < TimeSpan.FromSeconds(10))
                _backoffs.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task WaitForBackoffAsync()
        {
            await _backoffs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Advance(TimeSpan elapsed)
        {
            long target;
            lock (_gate) { _ticks += elapsed.Ticks; target = _ticks; }
            while (true)
            {
                ClockTimer? timer;
                lock (_gate)
                {
                    timer = _timers.Where(t => !t.Disposed && t.Due <= target).MinBy(t => t.Due);
                    if (timer is null) return;
                    timer.Due = timer.Period <= TimeSpan.Zero
                        ? long.MaxValue : timer.Due + timer.Period.Ticks;
                }
                timer.Callback(timer.State);
            }
        }

        private sealed class ClockTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; set; }
            public TimeSpan Period { get; private set; }
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                    Period = period;
                    return true;
                }
            }
            public void Dispose() { lock (clock._gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
