// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.W365APlaygroundAgent.Auth;
using ModelContextProtocol.Client;
using Xunit;

namespace Microsoft.W365APlaygroundAgent.Tests;

public class McpDiscoveryScopeTests
{
    [Fact]
    public void CompletenessUsesServerOutcomesNotToolNamesOrCounts()
    {
        using var scope = McpDiscoveryScope.Begin();
        Assert.False(scope.IsComplete);
        var discovered = new List<string> { "arbitrary-server", "another-server" };
        scope.RecordDiscovered(discovered);
        discovered.RemoveAt(1);
        var successful = new Dictionary<string, IList<McpClientTool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["arbitrary-server"] = []
        };
        scope.RecordLoaded(successful.Keys);
        Assert.Equal(2, scope.DiscoveredServerCount);
        Assert.False(scope.IsComplete);
        successful["ANOTHER-SERVER"] = [];
        scope.RecordLoaded(successful.Keys);
        Assert.True(scope.IsComplete);
    }

    [Fact]
    public void EmptyUnknownAndDuplicateDiscoveryCannotAuthorizeCaching()
    {
        using var scope = McpDiscoveryScope.Begin();
        scope.RecordLoaded([]);
        Assert.False(scope.IsComplete);
        scope.RecordDiscovered([]);
        Assert.False(scope.IsComplete);
        scope.RecordDiscovered(["duplicate", "DUPLICATE"]);
        scope.RecordLoaded(["duplicate"]);
        Assert.False(scope.IsComplete);
    }

    [Fact]
    public async Task ConcurrentScopesAreIsolatedAndDisposedScopesClearInheritedContexts()
    {
        using var parent = McpDiscoveryScope.Begin();
        parent.RecordDiscovered(["parent"]);
        parent.RecordLoaded(["parent"]);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var jobs = Enumerable.Range(0, 100).Select(index => Task.Run(async () =>
        {
            using (var scope = McpDiscoveryScope.Begin())
            {
                scope.RecordDiscovered([$"server-{index}"]);
                if (Interlocked.Increment(ref started) == 100)
                {
                    allStarted.SetResult();
                }
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                scope.RecordLoaded([$"server-{index}"]);
                Assert.Same(scope, McpDiscoveryScope.Current);
                Assert.True(scope.IsComplete);
            }
            Assert.Same(parent, McpDiscoveryScope.Current);
        })).ToArray();
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
        }
        await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(parent.IsComplete);

        var inheritedRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task inherited;
        using (McpDiscoveryScope.Begin())
        {
            inherited = Task.Run(async () =>
            {
                await inheritedRelease.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Null(McpDiscoveryScope.Current);
            });
        }
        inheritedRelease.SetResult();
        await inherited;
        Assert.Same(parent, McpDiscoveryScope.Current);
    }
}
