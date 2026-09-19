namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// Turning the offset a relay reports for a session into track-local time.
/// </summary>
/// <remarks>
/// <see cref="RedMist.TimingCommon.Models.Session.LocalTimeZoneOffset"/> is the only timezone signal
/// in the system: neither the event nor the organization carries one, and the track name is free
/// text. It is a double taken verbatim off the relay with no validation at ingest.
/// </remarks>
public static class TrackTime
{
    /// <summary>
    /// Turns a session's stored offset into a usable one, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero is read as absent rather than as Greenwich. The field defaults to zero, every track this
    /// system serves is hours away from UTC, and the cost of the two readings is asymmetric: treating
    /// a genuine UTC track as unknown puts a truthful note on times that are already right, while
    /// treating an unset field as UTC+0 silently labels UTC times as track local and sends somebody
    /// looking for a lap - or a viewership peak - at the wrong hour.
    /// </para>
    /// <para>
    /// Anything beyond the range of real offsets is a corrupt value and is discarded the same way.
    /// </para>
    /// <para>
    /// The result is rounded to whole minutes because <see cref="DateTimeOffset"/> accepts nothing
    /// else: an offset of 5.01 hours is 5:00:36, and handing that to a conversion throws. A
    /// fractional-second offset is one bad relay away and would otherwise take out every timestamp
    /// rather than one.
    /// </para>
    /// </remarks>
    /// <param name="localTimeZoneOffset">The session offset from UTC, in hours.</param>
    /// <returns>The offset, rounded to the minute, or null when there is not a usable one.</returns>
    public static TimeSpan? Offset(double localTimeZoneOffset)
    {
        if (localTimeZoneOffset == 0 || double.IsNaN(localTimeZoneOffset) ||
            Math.Abs(localTimeZoneOffset) > 14)
        {
            return null;
        }

        return TimeSpan.FromMinutes(Math.Round(localTimeZoneOffset * 60));
    }

    /// <summary>
    /// The first usable offset among an event's sessions, or null when none of them reported one.
    /// </summary>
    /// <remarks>
    /// First rather than most common: the sessions of one event are at one track, so they either
    /// agree or the later ones are corrupt. Callers pass them in the order the sessions ran.
    /// </remarks>
    public static TimeSpan? ForEvent(IEnumerable<double> sessionOffsets)
    {
        foreach (var offset in sessionOffsets)
        {
            if (Offset(offset) is { } usable)
            {
                return usable;
            }
        }

        return null;
    }

    /// <summary>
    /// Rounds a UTC instant down to a bucket boundary in track-local time.
    /// </summary>
    /// <remarks>
    /// Done in local rather than UTC so the buckets line up with the hour as the organizer reads it.
    /// For the whole- and half-hour offsets every real track uses this is the same answer as
    /// flooring in UTC, but it is correct by construction rather than by coincidence.
    /// </remarks>
    public static DateTime FloorToBucket(DateTime utc, TimeSpan offset, TimeSpan bucket)
    {
        var local = utc + offset;
        var floored = new DateTime(local.Ticks - (local.Ticks % bucket.Ticks), DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(floored - offset, DateTimeKind.Utc);
    }

    /// <summary>Rounds a UTC instant up to a bucket boundary in track-local time.</summary>
    public static DateTime CeilingToBucket(DateTime utc, TimeSpan offset, TimeSpan bucket)
    {
        var floored = FloorToBucket(utc, offset, bucket);
        return floored == utc ? floored : floored + bucket;
    }
}
