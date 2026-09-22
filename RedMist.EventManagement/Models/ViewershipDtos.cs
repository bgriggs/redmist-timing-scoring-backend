namespace RedMist.EventManagement.Models;

/// <summary>
/// One event's viewership, as the reports list shows it.
/// </summary>
/// <remarks>
/// Deliberately says "connections" nowhere in its field names but means them everywhere: a viewer
/// who loses signal and reconnects opens a second session, so the session count is the least
/// meaningful number here and must never be presented as a number of people. Concurrency is the
/// robust measure - a reconnect closes one session and opens another, so it does not inflate a peak.
/// </remarks>
public class ViewershipReportSummaryDto
{
    public int EventId { get; set; }

    public string EventName { get; set; } = string.Empty;

    /// <summary>
    /// The event's start date as the organizer entered it.
    /// </summary>
    /// <remarks>
    /// Not UTC and not an instant. It is a date with no reliable time base - it lands at midnight,
    /// which is why the report job widens it by twelve hours rather than trusting it as a bound. Use
    /// it to label and order events; use <see cref="WindowStartUtc"/> and
    /// <see cref="WindowEndUtc"/>, which are genuine UTC instants, for anything measured.
    /// </remarks>
    public DateTime EventStartDate { get; set; }

    public DateTime GeneratedUtc { get; set; }

    public string State { get; set; } = string.Empty;

    public DateTime WindowStartUtc { get; set; }

    public DateTime WindowEndUtc { get; set; }

    public double TotalViewerMinutes { get; set; }

    public int MaxConcurrent { get; set; }

    public DateTime? PeakUtc { get; set; }

    public string? TopClientType { get; set; }

    public int SessionCount { get; set; }

    public int OpenSessions { get; set; }

    public int AnomalousSessions { get; set; }

    /// <summary>
    /// Minutes from UTC for the track, or null when no session reported a usable one.
    /// </summary>
    /// <remarks>
    /// Null means label every timestamp UTC. It is not zero: the underlying column defaults to zero
    /// and every track this serves is hours from UTC, so zero is read as "absent" rather than as
    /// Greenwich. Presenting UTC as local would have an organizer read "peak at 09:00" for a race
    /// that started at 14:00 and conclude the numbers are wrong.
    /// </remarks>
    public int? TrackOffsetMinutes { get; set; }
}

/// <summary>One racing session's viewership within an event.</summary>
public class ViewershipSessionDto
{
    public int SessionId { get; set; }

    public string SessionName { get; set; } = string.Empty;

    public bool IsPracticeQualifying { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public double TotalViewerMinutes { get; set; }

    public int MaxConcurrent { get; set; }

    public DateTime? PeakUtc { get; set; }

    public string? TopClientType { get; set; }
}

/// <summary>
/// Concurrency over one fifteen-minute window, for one client type or for all of them together.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AvgConcurrent"/> is time-weighted and the per-type values sum exactly to the "All"
/// series in the same bucket, so they stack honestly. <see cref="MaxConcurrent"/> does not sum and
/// must not be stacked: iOS peaking at 10:00 and Web peaking at 10:10 would produce a bar
/// describing a moment that never happened.
/// </para>
/// <para>
/// <see cref="MinConcurrent"/> is the lowest count held for non-zero time across the whole bucket,
/// including any stretch before the first connection, so it is 0 for any bucket containing a moment
/// with nobody watching.
/// </para>
/// </remarks>
public class ViewershipBucketDto
{
    /// <summary>The racing session this bucket belongs to, or null for the event-level series.</summary>
    /// <remarks>
    /// The event-level series covers the whole window including time between racing sessions, so it
    /// is not the sum of the per-session ones.
    /// </remarks>
    public int? SessionId { get; set; }

    public DateTime BucketStartUtc { get; set; }

    /// <summary>iOS, Android, Web, API, or All.</summary>
    /// <remarks>All is swept independently over the same sessions, never summed from the others.</remarks>
    public string ClientType { get; set; } = string.Empty;

    public int MinConcurrent { get; set; }

    public int MaxConcurrent { get; set; }

    public double AvgConcurrent { get; set; }

    public long ViewerSeconds { get; set; }
}

/// <summary>One event's viewership in full.</summary>
public class ViewershipReportDto
{
    public ViewershipReportSummaryDto Summary { get; set; } = new();

    public List<ViewershipSessionDto> Sessions { get; set; } = [];

    public List<ViewershipBucketDto> Buckets { get; set; } = [];
}

/// <summary>
/// Where one finished event stands with the post-event report job.
/// </summary>
/// <remarks>
/// Three states a page needs to tell apart, and only the first two are visible here - the third is
/// the report itself, which is in the reports list:
/// <list type="bullet">
/// <item>State null: not processed yet. The job has not reached this event.</item>
/// <item>State "NoContent": processed, and nobody watched. Not a pending report; there will never
/// be one, so a page saying "no report yet" would leave an organizer waiting for nothing.</item>
/// <item>Any other state: the report exists and is in the reports list.</item>
/// </list>
/// No separate "processed" flag: it would be a second field encoding the same fact as a null state,
/// and two fields for one fact eventually disagree.
/// </remarks>
public class EventReportStatusDto
{
    public int EventId { get; set; }

    public string EventName { get; set; } = string.Empty;

    /// <summary>
    /// The event's end date as the organizer entered it.
    /// </summary>
    /// <remarks>
    /// A date, not an instant, for the same reason as
    /// <see cref="ViewershipReportSummaryDto.EventStartDate"/> - it lands at midnight and carries no
    /// reliable time base. It is the event's last day, so the event counts as finished - and appears
    /// here - only once that whole day has ended in UTC, and the job's settle period runs from then.
    /// </remarks>
    public DateTime EventEndDate { get; set; }

    /// <summary>The report's state, or null when the job has not processed this event.</summary>
    public string? State { get; set; }

    /// <summary>
    /// Whether the report job can still produce a report for this event.
    /// </summary>
    /// <remarks>
    /// A separate fact from <see cref="State"/>, not a restatement of it: State says what has
    /// happened, this says what still can. Without it a null State would read as "any moment now"
    /// for an event the job will never look at again - every event older than the job's lookback
    /// window, every simulation, and any event still flagged live - so a page listing last season
    /// would show a wall of rows pending forever.
    /// </remarks>
    public bool Eligible { get; set; }
}

/// <summary>Whether an organization wants the post-event report email.</summary>
public class ReportSettingsDto
{
    public bool SendPostEventReport { get; set; } = true;
}

/// <summary>
/// A running event's viewership as it stands right now: the post-event report's numbers, computed
/// live.
/// </summary>
/// <remarks>
/// <para>
/// Computed from the same viewer sessions by the same rules the report uses - the same treatment of
/// rows still open and rows that end before they start, the same window start, the same attribution
/// of viewers to racing sessions by intersecting each connection with each session's window, and the
/// same concurrency sweep - so the peak an organizer watches climb on race day is the peak the report
/// emails them afterwards. See <c>LiveViewershipCalculator</c> for where the two deliberately differ.
/// </para>
/// <para>
/// Every count is a count of CONNECTIONS, not of people, for the reason given on
/// <see cref="ViewershipReportSummaryDto"/>. Concurrency is the robust measure; nothing here is a
/// headcount.
/// </para>
/// <para>
/// Every timestamp is a UTC instant and is written with its "Z". PostgreSQL returns these columns with
/// no Kind, and without the designator a browser parses them as its own local time, which moves every
/// point on the chart by the viewer's offset from UTC.
/// </para>
/// </remarks>
public class LiveViewershipDto
{
    public int EventId { get; set; }

    /// <summary>
    /// When these numbers were computed, and where the window and a running session's buckets end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server caches an answer for about thirty seconds, so this can be that much older than the
    /// response. It is the honest time to label the numbers with.
    /// </para>
    /// <para>
    /// Once an event is over, nothing is counted past the end of its last day plus twelve hours, so
    /// the numbers for a finished event stop there however much later this is.
    /// </para>
    /// </remarks>
    public DateTime AsOfUtc { get; set; }

    /// <summary>The width of every bucket, in seconds.</summary>
    public int BucketSeconds { get; set; }

    /// <summary>
    /// Minutes from UTC for the track, or null when no session reported a usable one.
    /// </summary>
    /// <remarks>
    /// The same rule as <see cref="ViewershipReportSummaryDto.TrackOffsetMinutes"/>: null means label
    /// every time UTC, and zero is never sent to mean Greenwich.
    /// </remarks>
    public int? TrackOffsetMinutes { get; set; }

    public LiveViewershipEventDto Event { get; set; } = new();

    /// <summary>
    /// Every racing session of the event, oldest first.
    /// </summary>
    /// <remarks>
    /// A session the report would drop - one whose end is not after its start - is dropped here too,
    /// and so is one lying wholly outside the event's plausible window: from twelve hours before its
    /// start date to twelve hours after the end of its last day. A session running into that window
    /// from outside it - a relay test the day before, never retired - is shown from where the window
    /// begins.
    /// </remarks>
    public List<LiveViewershipSessionDto> Sessions { get; set; } = [];
}

/// <summary>
/// The event as a whole, from the start of its window to <see cref="LiveViewershipDto.AsOfUtc"/>.
/// </summary>
public class LiveViewershipEventDto
{
    /// <summary>
    /// Where the window begins: the first connection, rounded down to the quarter-hour in track-local
    /// time - where the post-event report's window will begin. The window ends at the as-of time.
    /// </summary>
    /// <remarks>
    /// Equal to the as-of time when nobody has connected yet, making an empty window. The window ends
    /// at the as-of time, or for a finished event at the end of its last day plus twelve hours.
    /// </remarks>
    public DateTime WindowStartUtc { get; set; }

    /// <summary>
    /// All connected time across the whole window, including the gaps between racing sessions.
    /// </summary>
    /// <remarks>
    /// Not the sum of the sessions' totals: time between sessions belongs to no session, and a viewer
    /// who stays on through a gap was still watching.
    /// </remarks>
    public long TotalViewerSeconds { get; set; }

    /// <summary>The most connections held at once for a non-zero length of time, across the whole window.</summary>
    public int MaxConcurrent { get; set; }

    /// <summary>
    /// Start of the first bucket that reached <see cref="MaxConcurrent"/>, or null when nobody has
    /// watched.
    /// </summary>
    public DateTime? PeakUtc { get; set; }

    /// <summary>
    /// Mean concurrency across the racing sessions only: their connected time over their combined
    /// length.
    /// </summary>
    /// <remarks>
    /// The gaps between sessions are left out of both halves, so an audience that drifts away over
    /// lunch does not drag down the figure for the racing. It is the per-session averages weighted by
    /// session length, so it can never fall outside the range they span. Zero when no session has
    /// run for any time yet.
    /// </remarks>
    public double AvgConcurrentDuringSessions { get; set; }
}

/// <summary>One racing session's viewership, live.</summary>
/// <remarks>
/// A viewer watching across a session boundary counts in both sessions, which is the right answer
/// for each of them - and why the sessions' totals are not meant to sum to the event's.
/// </remarks>
public class LiveViewershipSessionDto
{
    public int SessionId { get; set; }

    public string SessionName { get; set; } = string.Empty;

    public bool IsPracticeQualifying { get; set; }

    /// <summary>
    /// When the session started, or where the event's plausible window begins if it started earlier.
    /// </summary>
    public DateTime StartUtc { get; set; }

    /// <summary>When the session ended, or null while it is still running.</summary>
    /// <remarks>
    /// A running session's buckets run to <see cref="LiveViewershipDto.AsOfUtc"/>. At most one
    /// session is running: the latest, and only while the event is live and the session has neither
    /// recorded an end nor been retired by the timing processor.
    /// </remarks>
    public DateTime? EndUtc { get; set; }

    public long TotalViewerSeconds { get; set; }

    public int MaxConcurrent { get; set; }

    /// <summary>Start of the first bucket that reached <see cref="MaxConcurrent"/>, or null when nobody watched.</summary>
    public DateTime? PeakUtc { get; set; }

    /// <summary>Mean concurrency over the session so far: its connected time over its length.</summary>
    public double AvgConcurrent { get; set; }

    /// <summary>
    /// The session in buckets of <see cref="LiveViewershipDto.BucketSeconds"/>, from its start, across
    /// every client type together.
    /// </summary>
    public List<LiveViewershipBucketDto> Buckets { get; set; } = [];
}

/// <summary>Concurrency over one bucket of a session.</summary>
/// <remarks>
/// <para>
/// The first bucket starts exactly when the session did, not on a round minute. Rounding it would put
/// the time just before the start into this session and the one before it both, counting a viewer
/// watching across the boundary twice - the reason the report does not round either.
/// </para>
/// <para>
/// The last bucket is usually short: a running session's ends at the as-of time, an ended session's
/// at its end. Its <see cref="Min"/>, <see cref="Max"/> and <see cref="Avg"/> all describe only the
/// part it covers, so the line does not sag at "now" merely because the minute is not over yet.
/// </para>
/// </remarks>
public class LiveViewershipBucketDto
{
    public DateTime StartUtc { get; set; }

    /// <summary>
    /// The fewest connections held for a non-zero length of time in the bucket. Zero for any bucket
    /// containing a moment with nobody watching.
    /// </summary>
    public int Min { get; set; }

    /// <summary>The most connections held for a non-zero length of time in the bucket.</summary>
    public int Max { get; set; }

    /// <summary>Time-weighted mean concurrency over the part of the bucket that has elapsed.</summary>
    public double Avg { get; set; }
}
