// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.W365APlaygroundAgent.Throttling;
using Xunit;

namespace Microsoft.W365APlaygroundAgent.Tests;

public class ThrottlingPolicyTests
{
    [Fact]
    public void ConfiguredCallerBucketsApplyAlongsideIngressLimit()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Throttling:MessagesPerMinute"] = "4",
                ["Throttling:UserBurst:Enabled"] = "true",
                ["Throttling:UserBurst:TokensPerMinute"] = "1",
                ["Throttling:UserBurst:BurstCapacity"] = "2"
            }).Build();
        var settings = configuration.GetSection(ThrottlingOptions.SectionName).Get<ThrottlingOptions>()!;
        settings.Validate();
        using var ingress = new FixedWindowRateLimiter(settings.CreateIngressOptions());
        using var callers = new UserBurstLimiter(Options.Create(settings));
        for (var message = 0; message < 2; message++)
        {
            foreach (var caller in new[] { "caller-a", "caller-b" })
            {
                using var admission = ingress.AttemptAcquire();
                Assert.True(admission.IsAcquired);
                Assert.True(callers.TryConsume(caller, out _));
            }
        }
        Assert.False(callers.TryConsume("caller-a", out _));
        Assert.False(callers.TryConsume("caller-b", out _));
        using var overflow = ingress.AttemptAcquire();
        Assert.False(overflow.IsAcquired);
    }

    [Fact]
    public void ConfiguredIngressLimitRejectsOverflowWithoutQueueing()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Throttling:MessagesPerMinute"] = "2" }).Build();
        var options = configuration.GetSection(ThrottlingOptions.SectionName).Get<ThrottlingOptions>()!;
        options.Validate();
        using var limiter = new FixedWindowRateLimiter(options.CreateIngressOptions());
        using var first = limiter.AttemptAcquire();
        using var second = limiter.AttemptAcquire();
        using var third = limiter.AttemptAcquire();
        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(third.IsAcquired);
        Assert.Equal(0, limiter.GetStatistics()!.CurrentQueuedCount);
    }

    [Fact]
    public void DisabledBurstPolicyPreservesExistingAdmission()
    {
        using var limiter = new UserBurstLimiter(Options.Create(new ThrottlingOptions()));
        for (var index = 0; index < 20; index++)
        {
            Assert.True(limiter.TryConsume("caller", out _));
        }
    }

    [Fact]
    public void BurstIsSharedAcrossCallsButIsolatedBetweenHumans()
    {
        using var limiter = Create();
        Assert.True(limiter.TryConsume("human-a", out _));
        Assert.True(limiter.TryConsume("human-a", out _));
        Assert.False(limiter.TryConsume("human-a", out var retryAfter));
        Assert.InRange(retryAfter.TotalSeconds, 0.001, 12);
        Assert.True(limiter.TryConsume("human-b", out _));
        Assert.True(limiter.TryConsume("human-b", out _));
        Assert.False(limiter.TryConsume("human-b", out _));
    }

    [Fact]
    public async Task SimultaneousRequestsCannotOverdrawOneHumansBucket()
    {
        using var limiter = Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => limiter.TryConsume("same-human", out var retryAfter))));
        Assert.Equal(2, results.Count(acquired => acquired));
    }

    [Fact]
    public async Task ConsumedTokenEventuallyRefills()
    {
        using var limiter = Create(tokensPerMinute: 600, burst: 1);
        Assert.True(limiter.TryConsume("human", out _));
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25);
            if (limiter.TryConsume("human", out _))
            {
                return;
            }
        }
        Assert.Fail("The exhausted caller bucket did not replenish.");
    }

    [Theory]
    [InlineData(0, 5, 2)]
    [InlineData(5, 0, 2)]
    [InlineData(5, 5, 0)]
    public void InvalidConfigurationFailsBeforeServingTraffic(int ingress, int refill, int burst)
    {
        var options = new ThrottlingOptions
        {
            MessagesPerMinute = ingress,
            UserBurst = new UserBurstOptions { Enabled = true, TokensPerMinute = refill, BurstCapacity = burst }
        };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static UserBurstLimiter Create(int tokensPerMinute = 5, int burst = 2) =>
        new(Options.Create(new ThrottlingOptions
        {
            UserBurst = new UserBurstOptions
            {
                Enabled = true, TokensPerMinute = tokensPerMinute, BurstCapacity = burst
            }
        }));
}
