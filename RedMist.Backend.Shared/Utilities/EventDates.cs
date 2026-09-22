namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// Reading an event's end date as the calendar day it is, rather than as a moment.
/// </summary>
/// <remarks>
/// <para>
/// The organizer enters an event's dates as days, and each is stored as midnight at the start of its
/// day. So the end date is when the last day <em>begins</em>, not when the event ended - for a track
/// behind UTC the whole final day's racing comes after it. Read as a moment, it made the post-event
/// report cut off the final day and become due while that day was still being raced.
/// </para>
/// <para>
/// One definition, used by the report job's candidate query, the dashboard's report status, and the
/// viewership bounds, so that "the event is over" means the same thing to all of them. A dashboard
/// that said "pending" for an event the job would not touch for another day, or "no report yet" for
/// one still being raced, would be telling the organizer something untrue.
/// </para>
/// </remarks>
public static class EventDates
{
    /// <summary>The moment the event's last day ends: midnight, UTC, at the end of its end date.</summary>
    /// <remarks>
    /// Only the time of day is discarded, so an end date that does carry one is still read as that
    /// whole day rather than as the moment it names.
    /// </remarks>
    /// <param name="eventEndDate">The event's end date as stored.</param>
    public static DateTime LastDayEndUtc(DateTime eventEndDate)
        => DateTime.SpecifyKind(eventEndDate.Date, DateTimeKind.Utc).AddDays(1);

    /// <summary>
    /// The bound on the stored end date that means "the event's last day had ended by
    /// <paramref name="momentUtc"/>".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>endDate &lt; LastDayEndedByCutoff(t)</c> holds exactly when
    /// <c>LastDayEndUtc(endDate) &lt;= t</c>, and its negation exactly when the last day ends after
    /// <c>t</c>. Both are the same fact, so one cutoff serves "has finished", "has settled" and "is
    /// still inside the lookback".
    /// </para>
    /// <para>
    /// A bound on the column rather than arithmetic on it, so a query is a plain comparison of the
    /// column with a parameter - which translates to SQL unchanged and leaves the column indexable -
    /// rather than truncating every row's value to its date before comparing.
    /// </para>
    /// <para>
    /// The column is timestamp without time zone, read back under the legacy timestamp switch with no
    /// Kind and compared as the wall-clock value it holds - which for an end date is midnight of the
    /// day the organizer entered. The cutoff is that same kind of value, a midnight, so the comparison
    /// is like for like.
    /// </para>
    /// </remarks>
    /// <param name="momentUtc">The moment by which the last day must have ended.</param>
    public static DateTime LastDayEndedByCutoff(DateTime momentUtc)
        => DateTime.SpecifyKind(momentUtc.Date, DateTimeKind.Utc);
}
