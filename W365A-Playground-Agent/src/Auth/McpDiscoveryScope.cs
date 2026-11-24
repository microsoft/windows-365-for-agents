// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Auth;

internal sealed class McpDiscoveryScope : IDisposable
{
    private sealed class Holder { public McpDiscoveryScope? Value; }
    private static readonly AsyncLocal<Holder?> Ambient = new();
    private readonly Holder? _previous;
    private readonly Holder _holder;
    private string[]? _discovered;
    private HashSet<string>? _loaded;

    private McpDiscoveryScope()
    {
        _previous = Ambient.Value;
        _holder = new Holder { Value = this };
        Ambient.Value = _holder;
    }

    internal static McpDiscoveryScope Begin() => new();
    internal static McpDiscoveryScope? Current => Ambient.Value?.Value;
    internal int? DiscoveredServerCount => _discovered?.Length;
    internal int? LoadedServerCount => _loaded?.Count;
    internal bool IsComplete =>
        _discovered is { Length: > 0 } discovered && _loaded is { } loaded
        && discovered.Length == loaded.Count
        && discovered.All(name => !string.IsNullOrWhiteSpace(name) && loaded.Contains(name));

    internal void RecordDiscovered(IEnumerable<string> names) => _discovered = names.ToArray();
    internal void RecordLoaded(IEnumerable<string> names) => _loaded = names.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        // Clear inherited transport execution contexts as well as the caller's context.
        _holder.Value = null;
        if (ReferenceEquals(Ambient.Value, _holder))
        {
            Ambient.Value = _previous;
        }
    }
}
