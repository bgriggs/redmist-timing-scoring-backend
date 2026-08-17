using StackExchange.Redis;

namespace RedMist.Backend.Shared.Utilities;

public static class RedisBatchExtensions
{
    /// <summary>
    /// Reads several keys, costing about one round trip for the whole set rather than one each.
    ///
    /// Every request is issued before any of them is awaited, so the multiplexer writes them to
    /// the connection back to back and the replies come back together. That matters wherever a
    /// per-item read sits on a hot path: the latency stops scaling with how many items there are.
    ///
    /// Deliberately not a single multi-key GET, which is one round trip but cannot span hash slots
    /// on a clustered cache. Per-item keys generally do span them.
    /// </summary>
    /// <returns>The values, in the order the keys were given. Missing keys come back without a value.</returns>
    public static async Task<RedisValue[]> StringGetAllAsync(this IDatabase cache, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
            return [];

        var reads = new Task<RedisValue>[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            reads[i] = cache.StringGetAsync(keys[i]);

        return await Task.WhenAll(reads);
    }
}
