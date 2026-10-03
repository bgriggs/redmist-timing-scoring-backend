using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// The kinds of message a relay sends, as named in the relay message counters.
/// </summary>
/// <remarks>
/// Field names in Redis and row names on the site operations page, so they are part of what the page
/// reads: renaming one starts a new count under the new name and strands the old one until teardown.
/// Lowercase, and deliberately not the event status stream's field prefixes - those name what the
/// processor is handed (<c>x2pass</c>, <c>ftcar</c>), these name what the relay sent.
/// </remarks>
public static class RelayMessageTypes
{
    public const string RMonitor = "rmonitor";
    public const string Multiloop = "multiloop";
    public const string Flagtronics = "flagtronics";
    public const string Session = "session";
    public const string Passings = "passings";
    public const string Loops = "loops";
    public const string Flags = "flags";
    public const string Competitors = "competitors";

    /// <summary>
    /// The relay checking in. Counted like the rest but left out of every "messages" total, because
    /// it arrives every ten seconds whether or not the timing system is sending anything - a relay
    /// whose timing feed has died still heartbeats, and folded into the totals it would make a dead
    /// feed look alive.
    /// </summary>
    public const string Heartbeat = "heartbeat";

    /// <summary>Every type, in the order they are presented.</summary>
    public static readonly string[] All =
        [RMonitor, Multiloop, Flagtronics, Passings, Flags, Loops, Session, Competitors, Heartbeat];
}

/// <summary>
/// Counts what the relay hub receives, per event and message type, for the site operations page.
/// </summary>
/// <remarks>
/// <para>
/// Each count is five writes: the running total, the last-received time and the backstop expiry of the
/// event's hash (<see cref="Consts.RELAY_MESSAGE_COUNTS"/>), and the count and expiry of the current
/// minute's hash
/// (<see cref="Consts.RELAY_MESSAGE_MINUTE"/>). They go out as one batch, fire-and-forget, so a relay
/// message costs no extra round trip: the hub method returns as soon as the commands are queued on
/// the connection, and nothing on the relay's path waits for Redis to answer.
/// </para>
/// <para>
/// Never throws. These are operational counters, and a relay message that fails to be counted must
/// not fail to be delivered: a Redis problem is logged at debug and the message goes on.
/// </para>
/// <para>
/// Takes the time as an argument rather than reading a clock, so a test can say which minute a count
/// lands in.
/// </para>
/// </remarks>
public static class RelayMessageCounter
{
    /// <summary>Suffix of the field holding a type's last-received time.</summary>
    public const string LastSuffix = "-last";

    /// <summary>The UTC minute a moment falls in, as whole minutes since the Unix epoch.</summary>
    public static long UnixMinute(DateTime utc) =>
        new DateTimeOffset(UtcTimestamp.Normalize(utc)).ToUnixTimeSeconds() / 60;

    /// <summary>The start of a Unix minute, as a UTC instant.</summary>
    public static DateTime MinuteStartUtc(long unixMinute) =>
        DateTimeOffset.FromUnixTimeSeconds(unixMinute * 60).UtcDateTime;

    /// <summary>
    /// Records <paramref name="count"/> messages of <paramref name="type"/> for an event.
    /// </summary>
    /// <param name="cache">The Redis database to write to.</param>
    /// <param name="eventId">The event the relay addressed. Nothing is recorded unless it is positive.</param>
    /// <param name="type">One of <see cref="RelayMessageTypes"/>.</param>
    /// <param name="count">How many messages arrived. Nothing is recorded unless it is positive.</param>
    /// <param name="nowUtc">When they arrived.</param>
    /// <param name="logger">Where a failure to record is logged.</param>
    /// <remarks>
    /// An event id of zero is what a relay sends before anyone has picked an event, and is not an
    /// event: counting it would build one shared hash for every unconfigured relay on the site, which
    /// nothing ever tears down.
    /// </remarks>
    public static void Record(IDatabase cache, int eventId, string type, long count, DateTime nowUtc, ILogger logger)
    {
        if (eventId <= 0 || count <= 0)
        {
            return;
        }

        try
        {
            var now = UtcTimestamp.Normalize(nowUtc);
            var countsKey = new RedisKey(string.Format(Consts.RELAY_MESSAGE_COUNTS, eventId));
            var minuteKey = new RedisKey(string.Format(Consts.RELAY_MESSAGE_MINUTE, eventId, UnixMinute(now)));
            var lastMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();

            var batch = cache.CreateBatch();
            _ = batch.HashIncrementAsync(countsKey, type, count, CommandFlags.FireAndForget);
            _ = batch.HashSetAsync(countsKey, type + LastSuffix, lastMs, When.Always, CommandFlags.FireAndForget);
            // Sliding, not set once: a fixed expiry would cut a long event's totals off partway
            // through. Teardown is still what deletes them; this only catches ids never torn down.
            _ = batch.KeyExpireAsync(countsKey, Consts.RELAY_MESSAGE_COUNTS_TTL, CommandFlags.FireAndForget);
            _ = batch.HashIncrementAsync(minuteKey, type, count, CommandFlags.FireAndForget);
            // Refreshed on every write rather than set once, so a minute key cannot be left without an
            // expiry by the one write that created it being lost.
            _ = batch.KeyExpireAsync(minuteKey, Consts.RELAY_MESSAGE_MINUTE_TTL, CommandFlags.FireAndForget);
            batch.Execute();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to count {count} {type} message(s) for event {eventId}", count, type, eventId);
        }
    }
}
