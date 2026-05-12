// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;

namespace Microsoft.W365APlaygroundAgent.Throttling;

/// <summary>
/// Enforces a per-user usage quota of <see cref="MaxTurnsPerWindow"/> turns within a rolling
/// <see cref="WindowHours"/>-hour window. State is in-memory and resets on App Service restart.
/// For a multi-instance production deployment, back this with AzureTableStorage or Redis so
/// per-user counts are shared across instances.
///
/// Algorithm — each user has a FIFO queue of UTC timestamps, one per consumed turn. On every
/// call, timestamps older than the window are pruned from the head; if the remaining queue is
/// at the cap, the request is denied, otherwise a new timestamp is enqueued at the tail.
/// Locking is per-queue rather than global so concurrent users do not contend on a single hot lock.
/// </summary>
public class UserTurnLimiter : IUserTurnLimiter
{
    private const int MaxTurnsPerWindow = 100;
    private const int WindowHours = 24;
    private static readonly TimeSpan Window = TimeSpan.FromHours(WindowHours);

    // Per-user queue of turn timestamps (UTC). Per-queue lock guards prune+enqueue atomically
    // — avoids one hot lock across all users.
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _userWindows = new();

    public bool TryConsume(string? aadObjectId, out int currentCount)
    {
        currentCount = 0;
        if (string.IsNullOrEmpty(aadObjectId)) return false;

        var queue = _userWindows.GetOrAdd(aadObjectId, _ => new Queue<DateTime>());
        lock (queue)
        {
            var cutoff = DateTime.UtcNow - Window;
            while (queue.Count > 0 && queue.Peek() < cutoff)
                queue.Dequeue();

            if (queue.Count >= MaxTurnsPerWindow)
            {
                currentCount = queue.Count;
                return false;
            }

            queue.Enqueue(DateTime.UtcNow);
            currentCount = queue.Count;
            return true;
        }
    }
}
