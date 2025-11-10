using RedMist.Backend.Shared.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// Provides functionality to retrieve current event entries.
/// </summary>
/// <remarks>This class is typically used to query the state of relay connections and their associated events in
/// real time. It relies on the provided Redis cache to obtain up-to-date information about active relay connections.
/// Thread safety depends on the underlying implementation of the connection multiplexer.</remarks>
/// <param name="cacheMux">The connection multiplexer used to access the Redis cache containing relay event connection data. Cannot be null.</param>
public class EventsChecker(IConnectionMultiplexer cacheMux)
{

    /// <summary>
    /// Based on active signalR connections of relays, get the current events for those relays.
    /// </summary>
    /// <returns></returns>
    public async Task<List<RelayConnectionEventEntry>> GetCurrentEventsAsync()
    {
        var cache = cacheMux.GetDatabase();
        var hashKey = new RedisKey(Consts.RELAY_EVENT_CONNECTIONS);

        var entries = await cache.HashGetAllAsync(hashKey);
        var result = new List<RelayConnectionEventEntry>();
        foreach (var entry in entries)
        {
            if (entry.Name.IsNullOrEmpty || entry.Value.IsNullOrEmpty) continue;

            var eventEntry = JsonSerializer.Deserialize<RelayConnectionEventEntry>(entry.Value!);
            if (eventEntry != null && eventEntry.EventId > 0)
            {
                result.Add(eventEntry);
            }
        }
        return result;
    }
}
