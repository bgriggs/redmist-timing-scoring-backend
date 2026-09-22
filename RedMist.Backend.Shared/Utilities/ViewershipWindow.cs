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
    /// Guards against one corrupt timestamp producing a window of weeks. The event's dates are used
    /// only as this bound, never as the window, because they land at midnight.
    ///
    /// Hours rather than days, and deliberately: the bound has to be tighter than the longest window
    /// the report will cover, or a single stray early connection pins the window start and the
    /// truncation at the far end cuts real racing sessions off the report entirely.
    /// </remarks>
    public static readonly TimeSpan PlausibilityMargin = TimeSpan.FromHours(12);

    /// <summary>The earliest believable viewer timestamp for an event.</summary>
    /// <param name="eventStartDate">The event's start date as stored.</param>
    public static DateTime EarliestPlausibleUtc(DateTime eventStartDate)
        => DateTime.SpecifyKind(eventStartDate, DateTimeKind.Utc) - PlausibilityMargin;

    /// <summary>
    /// The latest believable viewer timestamp for an event, and the end given to a row left open.
    /// </summary>
    /// <remarks>Never later than now: nobody has watched the future.</remarks>
    /// <param name="eventEndDate">The event's end date as stored.</param>
    /// <param name="nowUtc">The current time.</param>
    public static DateTime LatestPlausibleUtc(DateTime eventEndDate, DateTime nowUtc)
    {
        var latest = DateTime.SpecifyKind(eventEndDate, DateTimeKind.Utc) + PlausibilityMargin;
        return latest > nowUtc ? nowUtc : latest;
    }

    /// <summary>
    /// The latest believable viewer timestamp, reading the event's end date as the calendar day it
    /// is: the end of that day plus the margin, and never later than now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The organizer enters the end date as a day, and it is stored as midnight at the start of that
    /// day. Adding the margin to that midnight, as <see cref="LatestPlausibleUtc"/> does, lands on the
    /// last day's morning in UTC - before a track behind UTC has started that day's racing, so
    /// everything watched on the final day falls outside the bound. Reading the date as the whole day
    /// and adding the margin after it covers the last day in full at any track within twelve hours of
    /// UTC, and still bounds a stray row left open after the event.
    /// </para>
    /// <para>
    /// Only the time of day is discarded, so an end date that does carry one is still treated as that
    /// whole day rather than as the moment it names.
    /// </para>
    /// <para>
    /// Used by the live viewership view. The post-event report still uses
    /// <see cref="LatestPlausibleUtc"/>; moving it onto this rule changes stored report output and is
    /// a separate decision.
    /// </para>
    /// </remarks>
    /// <param name="eventEndDate">The event's end date as stored.</param>
    /// <param name="nowUtc">The current time.</param>
    public static DateTime LatestPlausibleUtcAfterLastDay(DateTime eventEndDate, DateTime nowUtc)
    {
        var latest = DateTime.SpecifyKind(eventEndDate.Date, DateTimeKind.Utc).AddDays(1) + PlausibilityMargin;
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
    /// The last viewer activity seen for the event, or failing that the event's own end date, and
    /// never later than now. Handed to <see cref="SessionWindowResolver"/> as its fallback.
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
            : DateTime.SpecifyKind(eventEndDate, DateTimeKind.Utc);
        return fallback > nowUtc ? nowUtc : fallback;
    }
}
