using RedMist.Backend.Shared.Utilities;
using RedMist.Database.Models;

namespace RedMist.PostEventReports.Sections.Viewership;

/// <summary>
/// Turns an event's raw viewer sessions into the numbers the report presents.
/// </summary>
/// <remarks>
/// Pure: everything it needs is passed in, and it touches neither the database nor the clock. That is
/// deliberate - the concurrency rules here are the part of the feature most easily got subtly wrong,
/// and they are worth being able to test exhaustively without a fixture.
/// </remarks>
public static class ViewershipAggregator
{
    /// <summary>The bucket width every stored series uses.</summary>
    /// <remarks>
    /// The shared alignment grid rather than a width of its own, so the organizer's live view - which
    /// starts its window on the same grid - begins exactly where this report does.
    /// </remarks>
    public static readonly TimeSpan BucketLength = ViewershipWindow.AlignmentBucket;

    /// <summary>
    /// Builds the viewership summary for an event.
    /// </summary>
    /// <param name="sessions">Every viewer session recorded for the event.</param>
    /// <param name="racingSessions">The event's racing sessions and their windows.</param>
    /// <param name="trackOffset">The track's offset from UTC, or null when it is unknown.</param>
    /// <param name="earliestPlausibleUtc">Lower bound for a usable session start.</param>
    /// <param name="latestPlausibleUtc">Upper bound for a usable session end, and the clamp for sessions left open.</param>
    /// <param name="maxWindow">The longest window that will be reported on.</param>
    public static EventViewershipSummary Aggregate(
        IReadOnlyCollection<EventViewerSession> sessions,
        IReadOnlyCollection<SessionWindow> racingSessions,
        TimeSpan? trackOffset,
        DateTime earliestPlausibleUtc,
        DateTime latestPlausibleUtc,
        TimeSpan maxWindow)
    {
        var offset = trackOffset ?? TimeSpan.Zero;
        var summary = new EventViewershipSummary
        {
            TrackOffsetMinutes = trackOffset is { } o ? (int)o.TotalMinutes : null,
            SessionCount = sessions.Count,
        };

        // Open rows clamped and counted, rows that end before they start discarded and counted: the
        // rules the live viewership view applies too, so the two cannot disagree about either.
        var built = ViewerIntervals.Build(sessions, earliestPlausibleUtc, latestPlausibleUtc);
        summary.OpenSessions = built.OpenSessions;
        summary.AnomalousSessions = built.AnomalousSessions;
        var intervals = built.Intervals;

        if (intervals.Count == 0)
        {
            return summary;
        }

        var windowStart = ViewershipWindow.Start(intervals, offset);
        var windowEnd = TrackTime.CeilingToBucket(intervals.Max(i => i.EndUtc), offset, BucketLength);
        if (windowEnd - windowStart > maxWindow)
        {
            windowEnd = TrackTime.CeilingToBucket(windowStart + maxWindow, offset, BucketLength);
        }

        summary.WindowStartUtc = windowStart;
        summary.WindowEndUtc = windowEnd;

        foreach (var bucket in BuildSeries(intervals, windowStart, windowEnd, sessionId: null))
        {
            summary.Buckets.Add(bucket);
        }

        var allSeries = summary.Buckets.Where(b => b.ClientType == ViewerClientTypes.All).ToList();
        ApplyTotals(summary, allSeries, intervals, windowStart, windowEnd);

        foreach (var racing in racingSessions)
        {
            AddRacingSession(summary, intervals, racing);
        }

        return summary;
    }

    /// <summary>
    /// Adds one racing session's numbers and its chart series.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Swept over the session's own window, not a version of it rounded out to bucket boundaries.
    /// <see cref="SessionWindowResolver"/> deliberately truncates each session at the next one's
    /// start so the windows cannot overlap, and rounding the start down and the end up would put
    /// that overlap straight back - a viewer watching across the boundary would be counted in full
    /// by both sessions, and the sum of the sessions would exceed the event. It would also report a
    /// race that started at 15:07 as having started at 15:00.
    /// </para>
    /// <para>
    /// Added whether or not the session overlaps the charted window, because the summary is the only
    /// thing the "by session" table is built from: a session dropped here vanishes from the report
    /// altogether rather than merely going without a chart.
    /// </para>
    /// </remarks>
    private static void AddRacingSession(EventViewershipSummary summary, List<ViewerInterval> intervals,
        SessionWindow racing)
    {
        if (racing.EndUtc <= racing.StartUtc)
        {
            return;
        }

        var buckets = BuildSeries(intervals, racing.StartUtc, racing.EndUtc, racing.SessionId);
        foreach (var bucket in buckets)
        {
            summary.Buckets.Add(bucket);
        }

        var all = buckets.Where(b => b.ClientType == ViewerClientTypes.All).ToList();
        var peak = Peak(all);
        summary.Sessions.Add(new EventViewershipSessionSummary
        {
            SessionId = racing.SessionId,
            SessionName = racing.Name,
            IsPracticeQualifying = racing.IsPracticeQualifying,
            StartUtc = racing.StartUtc,
            EndUtc = racing.EndUtc,
            TotalViewerMinutes = Math.Round(all.Sum(b => b.ViewerSeconds) / 60d, 1),
            MaxConcurrent = peak.Max,
            PeakUtc = peak.At,
            TopClientType = TopClientType(intervals, racing.StartUtc, racing.EndUtc),
        });
    }

    /// <summary>
    /// The per-type series plus the all-types series for one window.
    /// </summary>
    /// <remarks>
    /// The all-types series is swept over every interval rather than summed from the per-type ones.
    /// Averages would sum to within rounding; maxima would not, and the result would be a peak that
    /// never happened.
    /// </remarks>
    private static List<EventViewershipBucket> BuildSeries(List<ViewerInterval> intervals,
        DateTime windowStart, DateTime windowEnd, int? sessionId)
    {
        var buckets = new List<EventViewershipBucket>();

        foreach (var clientType in ViewerClientTypes.Stored)
        {
            var forType = clientType == ViewerClientTypes.All
                ? intervals
                : intervals.Where(i => i.ClientType == clientType).ToList();

            foreach (var bucket in ConcurrencySweep.Run(forType, windowStart, windowEnd, BucketLength))
            {
                buckets.Add(new EventViewershipBucket
                {
                    SessionId = sessionId,
                    BucketStartUtc = bucket.StartUtc,
                    ClientType = clientType,
                    MinConcurrent = bucket.Min,
                    MaxConcurrent = bucket.Max,
                    AvgConcurrent = Math.Round(bucket.Average(BucketLength), 4),
                    ViewerSeconds = bucket.ViewerSeconds,
                });
            }
        }

        return buckets;
    }

    private static void ApplyTotals(EventViewershipSummary summary, List<EventViewershipBucket> allSeries,
        List<ViewerInterval> intervals, DateTime windowStart, DateTime windowEnd)
    {
        var peak = Peak(allSeries);
        summary.TotalViewerMinutes = Math.Round(allSeries.Sum(b => b.ViewerSeconds) / 60d, 1);
        summary.MaxConcurrent = peak.Max;
        summary.PeakUtc = peak.At;
        summary.TopClientType = TopClientType(intervals, windowStart, windowEnd);
    }

    /// <summary>
    /// The first bucket that reached the maximum, by the rule the live viewership view uses as well.
    /// </summary>
    private static (int Max, DateTime? At) Peak(List<EventViewershipBucket> series)
        => ConcurrencySweep.Peak(series.Select(b => (b.BucketStartUtc, b.MaxConcurrent)));

    /// <summary>
    /// The client type accounting for the most watching time in a window.
    /// </summary>
    /// <remarks>
    /// By time rather than by session count. Mobile clients reconnect constantly - a phone changing
    /// network or being backgrounded opens a new session each time - so counting sessions would hand
    /// the title to whichever platform reconnects most rather than to the one people actually watched
    /// on.
    /// </remarks>
    private static string? TopClientType(List<ViewerInterval> intervals, DateTime windowStart, DateTime windowEnd)
    {
        var byType = ViewerClientTypes.Reported
            .Select(type => (Type: type, Seconds: intervals
                .Where(i => i.ClientType == type)
                .Sum(i => Overlap(i, windowStart, windowEnd))))
            .Where(x => x.Seconds > 0)
            .ToList();

        if (byType.Count == 0)
        {
            return null;
        }

        // Ties break on the fixed presentation order so the answer is stable run to run.
        var most = byType.Max(x => x.Seconds);
        return ViewerClientTypes.Reported.First(t => byType.Any(x => x.Type == t && x.Seconds == most));
    }

    private static double Overlap(ViewerInterval interval, DateTime windowStart, DateTime windowEnd)
    {
        var start = interval.StartUtc < windowStart ? windowStart : interval.StartUtc;
        var end = interval.EndUtc > windowEnd ? windowEnd : interval.EndUtc;
        return end <= start ? 0 : (end - start).TotalSeconds;
    }
}
