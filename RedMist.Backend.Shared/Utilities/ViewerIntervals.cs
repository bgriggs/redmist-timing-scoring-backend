using RedMist.Database.Models;

namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// An event's viewer sessions as the intervals every viewership number is swept from, with a count
/// of what had to be assumed or thrown away to get them.
/// </summary>
/// <param name="Intervals">The usable intervals, clamped to the plausible bounds.</param>
/// <param name="OpenSessions">Rows with no end yet, counted to the upper bound.</param>
/// <param name="AnomalousSessions">Rows discarded because they ended before they began.</param>
public sealed record ViewerIntervalSet(List<ViewerInterval> Intervals, int OpenSessions, int AnomalousSessions);

/// <summary>
/// Turns raw <see cref="EventViewerSession"/> rows into <see cref="ViewerInterval"/>s.
/// </summary>
/// <remarks>
/// One implementation for the post-event report and the organizer's live viewership view. The two
/// have to agree about what a row left open, a row that ends before it starts, or a timestamp far
/// outside the event means - otherwise the peak an organizer watched climb on race day would not be
/// the peak the report emails them afterwards, and they would reasonably conclude one of them is
/// wrong.
/// </remarks>
public static class ViewerIntervals
{
    /// <summary>
    /// Builds the intervals for a set of rows.
    /// </summary>
    /// <param name="sessions">The event's viewer sessions, in any order.</param>
    /// <param name="earliestPlausibleUtc">Lower bound for a usable start; earlier starts are clamped up to it.</param>
    /// <param name="latestPlausibleUtc">
    /// Upper bound for a usable end, and the end given to a row that is still open. The report passes
    /// its end-date bound; the live view passes the end of the event's last day plus the margin,
    /// capped at the moment it is computed.
    /// </param>
    /// <remarks>
    /// <para>
    /// Timestamps are forced to UTC on the way in. The columns are timestamp without time zone, so
    /// PostgreSQL hands them back with no Kind, and anything that later writes one out - a JSON
    /// response - would then omit the "Z" and have a browser read UTC as its own local time.
    /// </para>
    /// <para>
    /// A row left open is not discarded: a browser tab open all weekend is real viewership, and the
    /// pod that should have closed the row may simply have been restarted. It is clamped to the upper
    /// bound and counted, so a caller can say how much of its total rests on that assumption.
    /// </para>
    /// <para>
    /// A row that ends before it starts is discarded and counted as anomalous - and only as that, not
    /// also as open, because the footnote about open rows tells an organizer they were counted to the
    /// end, and this one was not counted at all. That includes an open row whose start lies beyond
    /// the upper bound, since clamping its end puts that end before the start.
    /// </para>
    /// </remarks>
    public static ViewerIntervalSet Build(IEnumerable<EventViewerSession> sessions,
        DateTime earliestPlausibleUtc, DateTime latestPlausibleUtc)
    {
        var intervals = new List<ViewerInterval>();
        var open = 0;
        var anomalous = 0;

        foreach (var session in sessions)
        {
            var recordedStart = UtcTimestamp.Normalize(session.StartUtc);
            var end = session.EndUtc is { } recordedEnd ? UtcTimestamp.Normalize(recordedEnd) : latestPlausibleUtc;

            if (end < recordedStart)
            {
                anomalous++;
                continue;
            }

            if (session.EndUtc == null)
            {
                open++;
            }

            var start = recordedStart < earliestPlausibleUtc ? earliestPlausibleUtc : recordedStart;
            if (end > latestPlausibleUtc)
            {
                end = latestPlausibleUtc;
            }
            if (end <= start)
            {
                continue;
            }

            intervals.Add(new ViewerInterval(start, end, ViewerClientTypes.Normalize(session.ClientType)));
        }

        return new ViewerIntervalSet(intervals, open, anomalous);
    }
}
