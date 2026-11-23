// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Microsoft.W365APlaygroundAgent.Throttling;

/// <summary>Process-local admission by caller identity, never by client IP.</summary>
public sealed class UserBurstLimiter : IUserBurstLimiter, IDisposable
{
    private readonly PartitionedRateLimiter<string>? _limiter;

    public UserBurstLimiter(IOptions<ThrottlingOptions> options)
    {
        var settings = options.Value;
        settings.Validate();
        if (!settings.UserBurst.Enabled)
        {
            return;
        }
        var refillPeriod = TimeSpan.FromSeconds(60d / settings.UserBurst.TokensPerMinute);
        var capacity = settings.UserBurst.BurstCapacity;
        _limiter = PartitionedRateLimiter.Create<string, string>(caller =>
            RateLimitPartition.GetTokenBucketLimiter(caller, key => new TokenBucketRateLimiterOptions
            {
                TokenLimit = capacity,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = refillPeriod,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    public bool TryConsume(string caller, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrEmpty(caller);
        retryAfter = TimeSpan.Zero;
        if (_limiter is null)
        {
            return true;
        }
        using var lease = _limiter.AttemptAcquire(caller);
        if (lease.IsAcquired)
        {
            return true;
        }
        if (!lease.TryGetMetadata(MetadataName.RetryAfter, out retryAfter))
        {
            throw new InvalidOperationException("Token-bucket rejection did not include a retry interval.");
        }
        return false;
    }

    public void Dispose() => _limiter?.Dispose();
}
