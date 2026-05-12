// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Throttling;

public interface IUserTurnLimiter
{
    /// <summary>
    /// Attempts to consume one turn for the given caller within the rolling 24h window.
    /// Returns false (denied) if the per-user cap has been reached or the caller has no identity.
    /// <paramref name="currentCount"/> reflects the post-consume count when allowed, or the
    /// current observed count when denied.
    /// </summary>
    bool TryConsume(string? aadObjectId, out int currentCount);
}
