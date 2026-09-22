namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// The rules deciding what stretch of time an event's viewership covers.
/// </summary>
/// <remarks>
/// Shared by the post-event report and the organizer's live viewership view, so that "since when"
/// and "which timestamps are believable" are answered once. The live view's window starts exactly
/// where the report's will, which is what lets an organizer compare the two without a quarter-hour
/// of unexplained difference at the front.
/// </remarks>
public static class ViewershipWindow
{
    /// <summary>
    /// The post-event report's bucket width, and the grid every window start is aligned to.
    /// </summary>
    /// <remarks>
    /// The live view charts at a finer width but still starts its window on this grid, so its start
    /// is the report's start. A minute-aligned start would be up to fourteen minutes later for the
    /// same rows.
    /// </remarks>
    public static readonly TimeSpan AlignmentBucket = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far either side of the event's own dates a viewer timestamp is still believed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Guards against one corrupt timestamp producing a window of weeks. The event's dates are used
    /// only as this bound, never as the window, because they land at midnight.
    /// </para>
    /// <para>
    /// Hours rather than days, and deliberately: this is what bounds the report's window, and it
    /// should bound it no wider than the event needs. The span from the lower bound to the upper is
    /// twelve hours, plus every day of the event, plus twelve hours - 96 hours for a Friday-to-Sunday
    /// event, 120 for Thursday to Sunday.
    /// </para>
    /// <para>
    /// The report never truncates its window inside that span, whatever
    /// <c>PostEventReportSettings.MaxWindow</c> says: a single stray early connection would otherwise
    /// pin the window start, and the truncation at the far end would cut the final day's racing off
    /// the report - and put it at odds with the live view, which counts the whole span.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan PlausibilityMargin = TimeSpan.FromHours(12);

    /// <summary>The earliest believable viewer timestamp for an event.</summary>
    /// <param name="eventStartDate">The event's start date as stored.</param>
    public static DateTime EarliestPlausibleUtc(DateTime eventStartDate)
        => DateTime.SpecifyKind(eventStartDate, DateTimeKind.Utc) - PlausibilityMargin;

    /// <summary>
    /// The latest believable viewer timestamp for an event, and the end given to a row left open:
    /// the end of the event's last day plus the margin, and never later than now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The end date is read as the calendar day it is (<see cref="EventDates.LastDayEndUtc"/>). It is
    /// stored as midnight at the start of the last day, and the margin used to be added to that
    /// midnight - which lands on the last morning in UTC, before a track behind UTC has started that
    /// day's racing. The report then counted nothing from the final day, recorded an open final-day
    /// row as anomalous because its clamped end fell before its start, and for a single-day event in
    /// the Americas found nothing to say at all. Adding the margin after the whole day covers the last
    /// day in full at any track within twelve hours of UTC, and still bounds a stray row left open
    /// after the event.
    /// </para>
    /// <para>
    /// Never later than now, because nobody has watched the future. For the report that only applies
    /// if it runs before the bound has passed - with a settle period under twelve hours - and the live
    /// view reaches the bound only once the event is over.
    /// </para>
    /// <para>
    /// The one upper bound for both the post-event report and the live viewership view, so the two
    /// cannot disagree about which rows count.
    /// </para>
    /// </remarks>
    /// <param name="eventEndDate">The event's end date as stored.</param>
    /// <param name="nowUtc">The current time.</param>
    public static DateTime LatestPlausibleUtc(DateTime eventEndDate, DateTime nowUtc)
    {
        var latest = EventDates.LastDayEndUtc(eventEndDate) + PlausibilityMargin;
        return latest > nowUtc ? nowUtc : latest;
    }

    /// <summary>
    /// Where the window begins: the earliest interval's start, rounded down to the alignment grid in
    /// track-local time.
    /// </summary>
    /// <remarks>
    /// The window comes from the sessions, not from the event's start and end dates: those land at
    /// midnight and would produce a chart with forty empty buckets before anyone arrived.
    /// </remarks>
    /// <param name="intervals">The usable intervals. Must not be empty.</param>
    /// <param name="trackOffset">The track's offset from UTC, or zero when it is unknown.</param>
    public static DateTime Start(IReadOnlyCollection<ViewerInterval> intervals, TimeSpan trackOffset)
        => TrackTime.FloorToBucket(intervals.Min(i => i.StartUtc), trackOffset, AlignmentBucket);

    /// <summary>
    /// The end given to an event's last racing session when it never recorded one of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last viewer activity seen for the event, or failing that the end of the event's last day,
    /// and never later than now. Handed to <see cref="SessionWindowResolver"/> as its fallback.
    /// </para>
    /// <para>
    /// The end of the last day rather than the end date itself, which is that day's first moment: a
    /// final session with no viewers and no end of its own would otherwise be given an end before it
    /// began, and dropped.
    /// </para>
    /// </remarks>
    /// <param name="lastActivityUtc">
    /// The latest end - or, for a row still open, start - among the event's viewer sessions, or null
    /// when there are none.
    /// </param>
    /// <param name="eventEndDate">The event's end date as stored.</param>
    /// <param name="nowUtc">The current time.</param>
    public static DateTime LastSessionFallbackEndUtc(DateTime? lastActivityUtc, DateTime eventEndDate, DateTime nowUtc)
    {
        var fallback = lastActivityUtc is { } activity
            ? UtcTimestamp.Normalize(activity)
            : EventDates.LastDayEndUtc(eventEndDate);
        return fallback > nowUtc ? nowUtc : fallback;
    }
}
