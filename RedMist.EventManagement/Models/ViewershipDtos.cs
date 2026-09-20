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

/// <summary>Whether an organization wants the post-event report email.</summary>
public class ReportSettingsDto
{
    public bool SendPostEventReport { get; set; } = true;
}
