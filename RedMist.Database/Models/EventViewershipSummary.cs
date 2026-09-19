using System.ComponentModel.DataAnnotations;

namespace RedMist.Database.Models;

/// <summary>
/// The viewership section's numbers for one event, computed once when its report was produced.
/// </summary>
/// <remarks>
/// Derived from <see cref="EventViewerSession"/> rather than kept live, because a session's end time
/// is frequently settled minutes or hours after the fact by the logger's reconciler: anything written
/// while the event was running would be invalidated by a later close. Persisted rather than
/// recomputed on demand so the email and any later view of the same event cannot disagree, and so
/// the raw sessions stay prunable.
/// </remarks>
public class EventViewershipSummary
{
    public long Id { get; set; }

    public long PostEventReportId { get; set; }

    public DateTime WindowStartUtc { get; set; }

    public DateTime WindowEndUtc { get; set; }

    /// <summary>
    /// The track's offset from UTC in minutes, or null when no session reported a usable one.
    /// </summary>
    /// <remarks>
    /// Null means every time in the report is labelled UTC. Presenting UTC as though it were local is
    /// worse than admitting the offset is unknown: an organizer reading "peak at 09:00" for a race
    /// that started at 14:00 local concludes the whole feature is broken.
    /// </remarks>
    public int? TrackOffsetMinutes { get; set; }

    public double TotalViewerMinutes { get; set; }

    public int MaxConcurrent { get; set; }

    /// <summary>Start of the bucket in which <see cref="MaxConcurrent"/> occurred.</summary>
    public DateTime? PeakUtc { get; set; }

    /// <summary>
    /// The client type that accounted for the most viewer minutes, not the most sessions.
    /// </summary>
    /// <remarks>
    /// By minutes because a mobile client that reconnects every thirty seconds would otherwise win on
    /// row count while accounting for two minutes of actual watching.
    /// </remarks>
    [MaxLength(20)]
    public string? TopClientType { get; set; }

    /// <summary>
    /// How many viewer sessions fed the numbers. Not a count of people: one phone that changes
    /// network or is backgrounded produces a new session each time.
    /// </summary>
    public int SessionCount { get; set; }

    /// <summary>Sessions discarded as unusable, for example an end time before the start.</summary>
    public int AnomalousSessions { get; set; }

    /// <summary>
    /// Sessions that still had no end time and were clamped to the end of the window. A high count
    /// means the logger's reconciler did not get to them, and the totals are an over-estimate.
    /// </summary>
    public int OpenSessions { get; set; }

    public List<EventViewershipSessionSummary> Sessions { get; set; } = [];

    public List<EventViewershipBucket> Buckets { get; set; } = [];
}

/// <summary>
/// Viewership for one racing session within the event.
/// </summary>
/// <remarks>
/// Clients subscribe to an event, never to a racing session, so these are derived by intersecting
/// each viewer session's interval with the racing session's window. A viewer watching across a
/// boundary counts in both, which is the right answer. Time between racing sessions belongs to none
/// of them and appears only in the event totals.
/// </remarks>
public class EventViewershipSessionSummary
{
    public long Id { get; set; }

    public long EventViewershipSummaryId { get; set; }

    public int SessionId { get; set; }

    [MaxLength(512)]
    public string SessionName { get; set; } = string.Empty;

    public bool IsPracticeQualifying { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public double TotalViewerMinutes { get; set; }

    public int MaxConcurrent { get; set; }

    public DateTime? PeakUtc { get; set; }

    [MaxLength(20)]
    public string? TopClientType { get; set; }
}

/// <summary>
/// Concurrency over one fifteen-minute window, for one client type or for all of them together.
/// </summary>
public class EventViewershipBucket
{
    public long Id { get; set; }

    public long EventViewershipSummaryId { get; set; }

    /// <summary>
    /// The racing session this bucket belongs to, or null for the event-wide series.
    /// </summary>
    /// <remarks>
    /// The event-wide series covers the whole window including the gaps between racing sessions, so
    /// it is not the sum of the per-session series and cannot be derived from them.
    /// </remarks>
    public int? SessionId { get; set; }

    public DateTime BucketStartUtc { get; set; }

    /// <summary>iOS, Android, Web, API, or All.</summary>
    [MaxLength(20)]
    public string ClientType { get; set; } = string.Empty;

    /// <summary>
    /// The lowest number of connections held for a non-zero length of time anywhere in the bucket,
    /// including the stretch before the first connect. Legitimately zero for any bucket containing a
    /// moment with nobody watching.
    /// </summary>
    public int MinConcurrent { get; set; }

    /// <summary>The highest number of connections held for a non-zero length of time.</summary>
    public int MaxConcurrent { get; set; }

    /// <summary>
    /// Time-weighted mean concurrency over the full bucket length, so the per-type values sum exactly
    /// to the All value. That is what makes a stacked bar of averages honest, where a stacked bar of
    /// maxima would be a picture of a moment that never happened.
    /// </summary>
    public double AvgConcurrent { get; set; }

    /// <summary>Total connected seconds within the bucket.</summary>
    public long ViewerSeconds { get; set; }
}
