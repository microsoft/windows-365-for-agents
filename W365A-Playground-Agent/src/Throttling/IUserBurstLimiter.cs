// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Throttling;

public interface IUserBurstLimiter
{
    bool TryConsume(string caller, out TimeSpan retryAfter);
}
