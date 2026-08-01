using Microsoft.Extensions.Caching.Hybrid;

namespace RedMist.EventProcessor.Tests.Utilities;

/// <summary>
/// Test double for <see cref="HybridCache"/> that always runs the supplied factory.
/// </summary>
/// <remarks>
/// A Moq mock cannot stand in for HybridCache: its GetOrCreateAsync takes the key as a
/// ReadOnlySpan&lt;char&gt;, and Castle's generated proxy for a method with a byref-like
/// parameter fails at runtime with InvalidProgramException. Callers that swallow exceptions
/// then look like they succeeded while doing nothing at all, so this is a real implementation
/// rather than a mock.
/// </remarks>
public sealed class FakeHybridCache : HybridCache
{
    /// <summary>
    /// Values stored by <see cref="SetAsync"/>, and every key a factory was run for.
    /// </summary>
    public Dictionary<string, object?> Entries { get; } = [];

    /// <summary>
    /// Keys whose factory actually ran, i.e. cache misses. A key served from
    /// <see cref="Entries"/> is not recorded here.
    /// </summary>
    public List<string> FactoryInvocations { get; } = [];

    public override async ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state,
        Func<TState, CancellationToken, ValueTask<T>> factory, HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
    {
        if (Entries.TryGetValue(key, out var cached))
            return (T)cached!;

        FactoryInvocations.Add(key);
        var value = await factory(state, cancellationToken);
        Entries[key] = value;
        return value;
    }

    public override ValueTask SetAsync<T>(string key, T value, HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null, CancellationToken cancellationToken = default)
    {
        Entries[key] = value;
        return default;
    }

    public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Entries.Remove(key);
        return default;
    }

    public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) => default;
}