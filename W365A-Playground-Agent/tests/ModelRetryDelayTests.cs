// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Net.Http.Headers;
using Microsoft.W365APlaygroundAgent.ComputerUse;
using Xunit;

namespace Microsoft.W365APlaygroundAgent.Tests;

public class ModelRetryDelayTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void FallbackAddsJitterWithoutShorteningBackoff(int attempt, double seconds)
    {
        var delay = ResponsesOrchestrator.CalculateModelRetryDelay(
            null, attempt, DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(250));
        Assert.Equal(TimeSpan.FromSeconds(seconds + .25), delay);
    }

    [Fact]
    public void RetryAfterDeltaIsAMinimumNotAFullJitterUpperBound()
    {
        var header = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        var delay = ResponsesOrchestrator.CalculateModelRetryDelay(
            header, 1, DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(750));
        Assert.Equal(TimeSpan.FromSeconds(30.75), delay);
    }

    [Fact]
    public void RetryAfterDateIsHonoredAndPastDatesNeverProduceNegativeDelay()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var future = new RetryConditionHeaderValue(now.AddSeconds(20));
        var past = new RetryConditionHeaderValue(now.AddSeconds(-20));
        Assert.Equal(TimeSpan.FromSeconds(20.5), ResponsesOrchestrator.CalculateModelRetryDelay(
            future, 1, now, TimeSpan.FromMilliseconds(500)));
        Assert.Equal(TimeSpan.FromMilliseconds(500), ResponsesOrchestrator.CalculateModelRetryDelay(
            past, 1, now, TimeSpan.FromMilliseconds(500)));
    }
}
