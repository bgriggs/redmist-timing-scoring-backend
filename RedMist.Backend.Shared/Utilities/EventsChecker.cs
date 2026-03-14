using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// Provides functionality to retrieve current event entries.
/// </summary>
/// <remarks>This class is typically used to query the state of relay connections and their associated events in
/// real time. It relies on the provided Redis cache to obtain up-to-date information about active relay connections.
/// Events that are archived or have an end date older than 24 hours are excluded.
/// Thread safety depends on the underlying implementation of the connection multiplexer.</remarks>
/// <param name="cacheMux">The connection multiplexer used to access the Redis cache containing relay event connection data. Cannot be null.</param>
/// <param name="tsContext">The database context factory used to query event details.</param>
/// <param name="hybridCache">The hybrid cache used to cache event data.</param>
public class EventsChecker(IConnectionMultiplexer cacheMux, IDbContextFactory<TsContext> tsContext, HybridCache hybridCache)
{
    private static readonly HybridCacheEntryOptions cacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(15),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// Based on active signalR connections of relays, get the current events for those relays.
    /// Events that are archived or have an end date older than 24 hours are excluded.
    /// </summary>
    /// <returns></returns>
    public virtual async Task<List<RelayConnectionEventEntry>> GetCurrentEventsAsync()
    {
        var cache = cacheMux.GetDatabase();
        var hashKey = new RedisKey(Consts.RELAY_EVENT_CONNECTIONS);

        var entries = await cache.HashGetAllAsync(hashKey);
        var result = new List<RelayConnectionEventEntry>();
        foreach (var entry in entries)
        {
            if (entry.Name.IsNullOrEmpty || entry.Value.IsNullOrEmpty) continue;

            var eventEntry = JsonSerializer.Deserialize<RelayConnectionEventEntry>(entry.Value.ToString());
            if (eventEntry != null && eventEntry.EventId > 0)
            {
                if (await IsEventActiveAsync(eventEntry.EventId))
                {
                    result.Add(eventEntry);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Checks whether an event is active by verifying it is not archived and its end date is within the last 24 hours.
    /// </summary>
    private async Task<bool> IsEventActiveAsync(int eventId)
    {
        var cachedEvent = await hybridCache.GetOrCreateAsync(
            $"events-checker:{eventId}",
            async cancel =>
            {
                await using var db = await tsContext.CreateDbContextAsync(cancel);
                var ev = await db.Events
                    .AsNoTracking()
                    .Where(e => e.Id == eventId)
                    .Select(e => new { e.IsArchived, e.EndDate })
                    .FirstOrDefaultAsync(cancel);
                return ev;
            },
            cacheOptions);

        if (cachedEvent is null)
            return false;

        if (cachedEvent.IsArchived)
            return false;

        if (cachedEvent.EndDate < DateTime.UtcNow.AddHours(-24))
            return false;

        return true;
    }
}
