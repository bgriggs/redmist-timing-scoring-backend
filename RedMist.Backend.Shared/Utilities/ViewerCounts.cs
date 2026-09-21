using StackExchange.Redis;

namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// How many connections are watching one event, right now, broken down by client type.
/// </summary>
/// <param name="EventId">The event these counts describe.</param>
/// <param name="AsOfUtc">When the hash was read. The age of this is how a caller tells a real zero from a stopped feed.</param>
/// <param name="Total">Connections watching.</param>
/// <param name="ByClientType">Counts per client type. A type with none is absent rather than zero.</param>
/// <remarks>
/// Connections, not people: a viewer who loses signal and reconnects is briefly two, and a phone
/// that backgrounds is one fewer. It is a live gauge, not a headcount.
///
/// No MessagePack attributes on purpose. The hub is configured with ContractlessStandardResolver,
/// so this serializes as a map keyed by property name and needs no key numbering - which also keeps
/// it out of RedMist.TimingCommon, where a keyed member cannot be removed or renumbered without
/// crashing readers built against the old contract.
/// </remarks>
public record ViewerCountSnapshot(int EventId, DateTime AsOfUtc, int Total, Dictionary<string, int> ByClientType);

/// <summary>
/// What Red Mist currently sees for one running event, for an organizer's dashboard card.
/// </summary>
/// <param name="EventId">The event this describes.</param>
/// <param name="AsOfUtc">When this was taken.</param>
/// <param name="SessionId">The running session, or 0 when none has started.</param>
/// <param name="SessionName">The running session's name.</param>
/// <param name="IsPracticeQualifying">Whether that session is practice or qualifying rather than a race.</param>
/// <param name="Flag">The effective track flag.</param>
/// <param name="CarCount">Cars currently in the session.</param>
/// <param name="LastDataUtc">When timing data last arrived, or null if none has.</param>
/// <param name="RelayLastHeartbeatUtc">When the relay last checked in, or null if it never has.</param>
/// <remarks>
/// <para>
/// A second message rather than fields folded into <see cref="ViewerCountSnapshot"/>, so a client
/// already handling viewer counts does not have to change to receive this, and so the two can be
/// reasoned about separately even though they share a tick.
/// </para>
/// <para>
/// <see cref="RelayLastHeartbeatUtc"/> is a timestamp rather than a "connected" boolean on purpose.
/// Connected-or-not is a judgement about how old is too old, and a threshold chosen here would be
/// invisible to the page displaying it and wrong the moment the heartbeat cadence changed. The age
/// is the fact; what counts as stale belongs with whoever is drawing the indicator.
/// </para>
/// </remarks>
public record EventStatusSummary(
    int EventId,
    DateTime AsOfUtc,
    int SessionId,
    string SessionName,
    bool IsPracticeQualifying,
    string Flag,
    int CarCount,
    DateTime? LastDataUtc,
    DateTime? RelayLastHeartbeatUtc);

/// <summary>
/// Reads the live connection hash an event's status API replicas maintain.
/// </summary>
/// <remarks>
/// One implementation because two callers need the identical answer: the hub returns a snapshot the
/// moment a dashboard subscribes, and the per-event publisher pushes one every few seconds. If those
/// disagreed, a page would show one number on open and a different one a tick later with nothing
/// having changed.
/// </remarks>
public static class ViewerCounts
{
    /// <summary>
    /// Reads one event's counts. Returns an empty snapshot rather than throwing if Redis is unwell.
    /// </summary>
    /// <remarks>
    /// A failed read is reported as zero rather than as an error because the caller is a dashboard
    /// tile: the timestamp travels with it, so a stale or failed read shows as an aging number
    /// rather than as a count anyone would trust. It matters most on the subscribe path, where one
    /// unreachable event would otherwise fail the whole call - leaving a dashboard already joined to
    /// some groups but believing the subscription failed, and showing nothing for events that were
    /// perfectly readable.
    /// </remarks>
    public static async Task<ViewerCountSnapshot> ReadAsync(IDatabase cache, int eventId, DateTime asOfUtc)
    {
        var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;

        var key = string.Format(Consts.STATUS_EVENT_CONNECTIONS, eventId);
        HashEntry[] entries;
        try
        {
            entries = await cache.HashGetAllAsync(key);
        }
        catch (RedisException)
        {
            return new ViewerCountSnapshot(eventId, asOfUtc, 0, byType);
        }

        foreach (var entry in entries)
        {
            var clientType = entry.Value.ToString();
            if (string.IsNullOrWhiteSpace(clientType))
            {
                // A connection tracked without a type still counts as somebody watching.
                clientType = "Web";
            }

            byType[clientType] = byType.TryGetValue(clientType, out var n) ? n + 1 : 1;
            total++;
        }

        return new ViewerCountSnapshot(eventId, asOfUtc, total, byType);
    }
}
