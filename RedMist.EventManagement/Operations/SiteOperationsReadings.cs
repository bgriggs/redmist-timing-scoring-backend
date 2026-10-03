using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.EventManagement.Models;
using StackExchange.Redis;
using System.Globalization;
using System.Text.Json;

namespace RedMist.EventManagement.Operations;

/// <summary>
/// What is in the relay connection hash, split into its two kinds of field.
/// </summary>
/// <param name="Heartbeats">The latest heartbeat for each event, by event id.</param>
/// <param name="Connections">Every relay hub connection still registered.</param>
public sealed record RelayState(
    Dictionary<int, RelayConnectionEventEntry> Heartbeats,
    List<RelayConnectionStatus> Connections);

/// <summary>
/// The pure parts of the site operations overview: turning what Redis holds into the figures the
/// page shows.
/// </summary>
/// <remarks>
/// Kept apart from the controller, which only fetches, so the rules here - which fields are which,
/// what a minute is, what counts as unhealthy - can be tested without a database or a cache.
/// </remarks>
public static class SiteOperationsReadings
{
    /// <summary>Complete minutes of message history shown, before the current minute.</summary>
    public const int MessageHistoryMinutes = 60;

    private static readonly string HeartbeatFieldPrefix = string.Format(Consts.RELAY_HEARTBEAT, string.Empty);
    private static readonly string ConnectionFieldPrefix = string.Format(Consts.RELAY_CONNECTION, string.Empty);

    /// <summary>
    /// Splits the relay connection hash into heartbeats and connections.
    /// </summary>
    /// <remarks>
    /// The two kinds of field share one hash and are told apart by their name prefix. A field that
    /// does not parse is skipped rather than failing the page: the hash is written by every relay hub
    /// replica and read by several services, and one bad value should cost one row, not the overview.
    /// </remarks>
    public static RelayState ParseRelayState(IEnumerable<HashEntry> entries)
    {
        var heartbeats = new Dictionary<int, RelayConnectionEventEntry>();
        var connections = new List<RelayConnectionStatus>();

        foreach (var entry in entries)
        {
            if (entry.Name.IsNullOrEmpty || entry.Value.IsNullOrEmpty)
            {
                continue;
            }

            var name = entry.Name.ToString();
            try
            {
                if (name.StartsWith(HeartbeatFieldPrefix, StringComparison.Ordinal))
                {
                    var heartbeat = JsonSerializer.Deserialize<RelayConnectionEventEntry>(entry.Value.ToString());
                    if (heartbeat is { EventId: > 0 })
                    {
                        heartbeat.Timestamp = UtcTimestamp.Normalize(heartbeat.Timestamp);
                        heartbeats[heartbeat.EventId] = heartbeat;
                    }
                }
                else if (name.StartsWith(ConnectionFieldPrefix, StringComparison.Ordinal))
                {
                    var connection = JsonSerializer.Deserialize<RelayConnectionStatus>(entry.Value.ToString());
                    if (connection != null && !string.IsNullOrEmpty(connection.ConnectionId))
                    {
                        connection.ConnectedTimestamp = UtcTimestamp.Normalize(connection.ConnectedTimestamp);
                        connections.Add(connection);
                    }
                }
            }
            catch (JsonException)
            {
                // Skipped; see remarks.
            }
        }

        return new RelayState(heartbeats, connections);
    }

    /// <summary>
    /// Builds one event's message figures from its totals hash and its per-minute hashes.
    /// </summary>
    /// <param name="totals">The event's <see cref="Consts.RELAY_MESSAGE_COUNTS"/> hash.</param>
    /// <param name="minutes">
    /// The event's per-minute hashes for consecutive minutes starting at <paramref name="firstMinute"/>,
    /// oldest first. The last is the current minute, still filling; the one before it is the last
    /// complete minute. A minute with no hash is an empty array.
    /// </param>
    /// <param name="firstMinute">The Unix minute of the first entry in <paramref name="minutes"/>.</param>
    public static SiteOperationsMessagesDto BuildMessages(HashEntry[] totals, IReadOnlyList<HashEntry[]> minutes, long firstMinute)
    {
        var fields = totals
            .Where(e => !e.Name.IsNullOrEmpty)
            .GroupBy(e => e.Name.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

        var lastComplete = minutes.Count >= 2 ? ToCounts(minutes[^2]) : [];

        var result = new SiteOperationsMessagesDto();
        foreach (var type in fields.Keys
            .Where(f => !f.EndsWith(RelayMessageCounter.LastSuffix, StringComparison.Ordinal))
            .OrderBy(TypeOrder)
            .ThenBy(t => t, StringComparer.Ordinal))
        {
            var row = new SiteOperationsMessageTypeDto
            {
                Type = type,
                Total = ParseCount(fields[type]),
                LastUtc = fields.TryGetValue(type + RelayMessageCounter.LastSuffix, out var last) ? ParseEpochMs(last) : null,
                PerMinuteLast = lastComplete.GetValueOrDefault(type),
            };
            result.ByType.Add(row);

            if (row.Type == RelayMessageTypes.Heartbeat)
            {
                continue;
            }

            result.Total += row.Total;
            result.PerMinuteLast += row.PerMinuteLast;
            if (row.LastUtc is { } at && (result.LastMessageUtc == null || at > result.LastMessageUtc))
            {
                result.LastMessageUtc = at;
            }
        }

        for (var i = 0; i < minutes.Count; i++)
        {
            result.PerMinute.Add(new SiteOperationsMinuteDto
            {
                MinuteUtc = RelayMessageCounter.MinuteStartUtc(firstMinute + i),
                Count = ToCounts(minutes[i])
                    .Where(kvp => kvp.Key != RelayMessageTypes.Heartbeat)
                    .Sum(kvp => kvp.Value),
            });
        }

        return result;
    }

    /// <summary>
    /// Whether a pod needs an operator's attention: not ready without simply having finished, or a
    /// container waiting on something.
    /// </summary>
    /// <remarks>
    /// A Succeeded pod is a job that ran to completion, which is not ready because there is nothing
    /// left to be ready for. A waiting reason counts on its own because CrashLoopBackOff can sit on a
    /// pod whose other containers are ready.
    /// </remarks>
    public static bool IsUnhealthy(PodHealth pod) =>
        (!pod.Ready && !string.Equals(pod.Phase, "Succeeded", StringComparison.Ordinal)) || pod.WaitingReason != null;

    /// <summary>Viewer counts summed across events, skipping any that could not be read.</summary>
    public static SiteViewerCountsDto SumViewers(IEnumerable<SiteViewerCountsDto?> counts, DateTime asOfUtc)
    {
        var total = new SiteViewerCountsDto { AsOfUtc = asOfUtc };
        foreach (var count in counts)
        {
            if (count == null)
            {
                continue;
            }

            total.Total += count.Total;
            foreach (var (type, n) in count.ByClientType)
            {
                total.ByClientType[type] = total.ByClientType.GetValueOrDefault(type) + n;
            }
        }

        return total;
    }

    private static int TypeOrder(string type)
    {
        var index = Array.IndexOf(RelayMessageTypes.All, type);
        return index < 0 ? int.MaxValue : index;
    }

    private static Dictionary<string, long> ToCounts(HashEntry[] entries)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!entry.Name.IsNullOrEmpty)
            {
                counts[entry.Name.ToString()] = ParseCount(entry.Value);
            }
        }
        return counts;
    }

    /// <summary>A count field, read as zero when it is missing or not a number, and never negative.</summary>
    private static long ParseCount(RedisValue value) =>
        long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;

    private static DateTime? ParseEpochMs(RedisValue value) =>
        long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : null;
}
