// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Threading.RateLimiting;

namespace Microsoft.W365APlaygroundAgent.Throttling;

public sealed class ThrottlingOptions
{
    public const string SectionName = "Throttling";

    public int MessagesPerMinute { get; set; } = 5;
    public UserBurstOptions UserBurst { get; set; } = new();

    public void Validate()
    {
        if (MessagesPerMinute <= 0)
        {
            throw new InvalidOperationException("Throttling:MessagesPerMinute must be positive.");
        }
        if (UserBurst is null || UserBurst.TokensPerMinute <= 0
            || TimeSpan.FromSeconds(60d / UserBurst.TokensPerMinute) <= TimeSpan.Zero
            || UserBurst.BurstCapacity <= 0)
        {
            throw new InvalidOperationException(
                "Throttling:UserBurst requires positive TokensPerMinute and BurstCapacity, with a nonzero refill period.");
        }
    }

    public FixedWindowRateLimiterOptions CreateIngressOptions() => new()
    {
        PermitLimit = MessagesPerMinute,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };
}

public sealed class UserBurstOptions
{
    public bool Enabled { get; set; }
    public int TokensPerMinute { get; set; } = 5;
    public int BurstCapacity { get; set; } = 2;
}
