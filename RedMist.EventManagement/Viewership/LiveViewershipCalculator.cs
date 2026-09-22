using RedMist.Backend.Shared.Utilities;
using RedMist.Database.Models;
using RedMist.EventManagement.Models;
using RedMist.TimingCommon.Models;

namespace RedMist.EventManagement.Viewership;

/// <summary>
/// Computes a running event's viewership as it stands right now: the post-event report's numbers,
/// live, for the organizer dashboard.
/// </summary>
/// <remarks>
/// <para>
/// Pure: everything it needs is passed in, and it touches neither the database nor the clock, for the
/// same reason the report's aggregator is - these are the rules most easily got subtly wrong, and they
/// are worth testing exhaustively without a fixture.
/// </para>
/// <para>
/// <b>What is shared with the report, and therefore cannot disagree with it.</b> Rows are turned into
/// intervals by <see cref="ViewerIntervals"/> - open rows clamped, rows ending before they start
/// discarded, starts before the plausible bound clamped up to it - between the same two bounds:
/// <see cref="ViewershipWindow.EarliestPlausibleUtc"/> and
/// <see cref="ViewershipWindow.LatestPlausibleUtc"/>. The window starts where
/// <see cref="ViewershipWindow.Start"/> says. Racing sessions are resolved by
/// <see cref="SessionWindowResolver"/>, viewers are attributed to them by intersecting each interval
/// with each session's own unrounded window, and every figure comes out of
/// <see cref="ConcurrencySweep"/>, with the peak time chosen by <see cref="ConcurrencySweep.Peak"/>.
/// </para>
/// <para>
/// <b>Where it deliberately differs, and why.</b>
/// </para>
/// <list type="bullet">
/// <item>
/// Buckets are a minute wide rather than fifteen, because this is a line an organizer watches move.
/// The window still starts on the report's quarter-hour grid, so its start is the report's start.
/// </item>
/// <item>
/// The upper bound is the report's - the end of the event's last day plus the plausibility margin -
/// capped at the as-of time rather than at whenever the report runs. While the event runs that cap
/// is what applies: nothing can have been watched after it, and an open row is clamped to it. Once
/// the event is over the day bound takes over, so a stray row left open after the event is not
/// counted until whenever somebody next asks, and the answer for a finished event stops growing -
/// at exactly the point the report's will.
/// </item>
/// <item>
/// Racing sessions are charted only inside the plausible window. A session that began before it - a
/// relay test the day before, left unretired so that it ran on until the first real session - keeps
/// only the part inside the window, and one wholly outside it is left out. Its viewers were already
/// excluded by the same bound; without this its hours would stay in the average across sessions as
/// time nobody watched, and in the payload as a long run of empty buckets.
/// </item>
/// <item>
/// No truncation at the report's longest window. The live window ends at the as-of time, and a
/// dashboard that stopped counting four days in would be showing a number that is plainly false.
/// </item>
/// <item>
/// The latest session can be running, which the report - run after the event - never sees. See
/// <see cref="ResolveSessions"/>.
/// </item>
/// <item>
/// Every bucket's mean is taken over the stretch it actually covers rather than the full minute, so
/// the short bucket at "now" and the short last bucket of an ended session do not sag. Min and max
/// already came from only that stretch.
/// </item>
/// </list>
/// <para>
/// <b>Open rows are not checked against the live connection hash.</b> It was considered and rejected,
/// because the hash cannot improve on clamping to the as-of time in any case that matters:
/// </para>
/// <list type="bullet">
/// <item>
/// A row whose connection died with its status pod - the case that leaves rows open longest - still
/// has its hash entry, because the entry is removed by the very disconnect handler that never ran.
/// Presence proves nothing, so the check cannot catch it. The logger's reconciler caps such a row at
/// six hours (<c>ViewerSessionReconciler.MaxSessionDuration</c>), and until then it is counted here
/// exactly as the report will count it - so this is an overcount the two agree on, not one between
/// them.
/// </item>
/// <item>
/// A row open with no hash entry is a connection whose disconnect handler ran but whose close has not
/// reached the database. Normally that is stream lag of well under a second. If the close was lost,
/// the reconciler closes the row on its second consecutive miss - within two reconcile intervals, two
/// minutes, of the disconnect - backdated to its first miss. The check could only exclude such a row,
/// not end it at the right time, and excluding it would throw away its whole real history, then
/// restore it once the reconciler closed it: a chart that rewrites its own past, which is worse than a
/// two-minute overhang at "now".
/// </item>
/// <item>
/// The hub writes a new connection's hash entry fire-and-forget, so a row can exist before its entry
/// does. Excluding rows without one would drop viewers who had just arrived.
/// </item>
/// </list>
/// <para>
/// So the worst case: a connection whose close was lost is counted as still watching for up to two
/// minutes after it left, plus up to the thirty seconds an answer is cached for, after which the
/// numbers settle to what the report will say. A connection orphaned by a pod killed outright is
/// counted for up to six hours, in this view and in the report alike. And if the event's logger pod is
/// not running at all, open rows count until the orchestrator closes them at teardown - again in both
/// - and never past the end of the event's last day plus the margin. Checking the hash would also
/// make Redis a dependency of a figure that otherwise, like the report, rests on the database alone.
/// </para>
/// </remarks>
public static class LiveViewershipCalculator
{
    /// <summary>The width of every live bucket.</summary>
    public static readonly TimeSpan BucketLength = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Computes the viewership of one event as of <paramref name="asOfUtc"/>.
    /// </summary>
    /// <param name="eventId">The event.</param>
    /// <param name="eventStartDate">The event's start date as stored, for the lower plausibility bound.</param>
    /// <param name="eventEndDate">
    /// The event's end date as stored. Read as a calendar day for the upper plausibility bound, and
    /// used as the report uses it to end a latest session that never recorded an end and is no longer
    /// running.
    /// </param>
    /// <param name="eventIsLive">
    /// Whether the event is live - a relay has sent a heartbeat within the orchestrator's timeout. A
    /// session can only be running while it is.
    /// </param>
    /// <param name="racingSessions">Every racing session of the event, in any order.</param>
    /// <param name="viewerSessions">Every viewer session recorded for the event, in any order.</param>
    /// <param name="asOfUtc">The moment to compute as of.</param>
    public static LiveViewershipDto Compute(int eventId, DateTime eventStartDate, DateTime eventEndDate,
        bool eventIsLive, IReadOnlyCollection<Session> racingSessions,
        IReadOnlyCollection<EventViewerSession> viewerSessions, DateTime asOfUtc)
    {
        var asOf = UtcTimestamp.Normalize(asOfUtc);
        var earliest = ViewershipWindow.EarliestPlausibleUtc(eventStartDate);
        var latest = ViewershipWindow.LatestPlausibleUtc(eventEndDate, asOf);

        // In the order the sessions ran, because the first usable offset wins: the sessions of one
        // event are at one track, so they either agree or the later ones are corrupt.
        var trackOffset = TrackTime.ForEvent(racingSessions
            .OrderBy(s => s.StartTime)
            .ThenBy(s => s.Id)
            .Select(s => s.LocalTimeZoneOffset));

        var intervals = ViewerIntervals.Build(viewerSessions, earliest, latest).Intervals;

        var result = new LiveViewershipDto
        {
            EventId = eventId,
            AsOfUtc = asOf,
            BucketSeconds = (int)BucketLength.TotalSeconds,
            TrackOffsetMinutes = trackOffset is { } o ? (int)o.TotalMinutes : null,
        };

        var sessionSeconds = 0d;
        var sessionLength = 0d;
        var sessions = ResolveSessions(racingSessions, viewerSessions, eventEndDate, eventIsLive, earliest, latest, asOf);
        foreach (var (window, running) in sessions)
        {
            var buckets = ConcurrencySweep.Run(intervals, window.StartUtc, window.EndUtc, BucketLength);
            var seconds = buckets.Sum(b => b.ConnectedSeconds);
            var length = (window.EndUtc - window.StartUtc).TotalSeconds;
            var peak = ConcurrencySweep.Peak(buckets.Select(b => (b.StartUtc, b.Max)));

            sessionSeconds += seconds;
            sessionLength += length;

            result.Sessions.Add(new LiveViewershipSessionDto
            {
                SessionId = window.SessionId,
                SessionName = window.Name,
                IsPracticeQualifying = window.IsPracticeQualifying,
                StartUtc = window.StartUtc,
                EndUtc = running ? null : window.EndUtc,
                TotalViewerSeconds = (long)Math.Round(seconds),
                MaxConcurrent = peak.Max,
                PeakUtc = peak.AtUtc,
                AvgConcurrent = Math.Round(seconds / length, 4),
                Buckets = buckets.ConvertAll(b => new LiveViewershipBucketDto
                {
                    StartUtc = b.StartUtc,
                    Min = b.Min,
                    Max = b.Max,
                    Avg = Math.Round(b.AverageOverSpan(), 4),
                }),
            });
        }

        result.Event = EventFigures(intervals, trackOffset ?? TimeSpan.Zero, asOf,
            sessionLength > 0 ? sessionSeconds / sessionLength : 0);
        return result;
    }

    /// <summary>The figures for the whole window, gaps between sessions included.</summary>
    private static LiveViewershipEventDto EventFigures(List<ViewerInterval> intervals, TimeSpan trackOffset,
        DateTime asOf, double avgDuringSessions)
    {
        if (intervals.Count == 0)
        {
            // Nobody has connected, so there is no first connection for the window to start at. An
            // empty window at "now" says so without inventing a start.
            return new LiveViewershipEventDto { WindowStartUtc = asOf };
        }

        var windowStart = ViewershipWindow.Start(intervals, trackOffset);

        // Swept only as far as the last connection rather than all the way to the as-of time. Every
        // bucket past it is empty, so the total, the peak and the peak's time come out the same - and
        // the gap between an event's last viewer and "now" would otherwise be empty buckets computed
        // only to be thrown away.
        var buckets = ConcurrencySweep.Run(intervals, windowStart, intervals.Max(i => i.EndUtc), BucketLength);
        var peak = ConcurrencySweep.Peak(buckets.Select(b => (b.StartUtc, b.Max)));

        return new LiveViewershipEventDto
        {
            WindowStartUtc = windowStart,
            TotalViewerSeconds = (long)Math.Round(buckets.Sum(b => b.ConnectedSeconds)),
            MaxConcurrent = peak.Max,
            PeakUtc = peak.AtUtc,
            AvgConcurrentDuringSessions = Math.Round(avgDuringSessions, 4),
        };
    }

    /// <summary>
    /// The event's racing sessions with the windows they are charted over, oldest first, and which one
    /// - if any - is still running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved by <see cref="SessionWindowResolver"/>, as the report resolves them: a session runs
    /// until its own recorded end, the next session's start, or the fallback, whichever comes first,
    /// and a session left with no length is dropped.
    /// </para>
    /// <para>
    /// The latest session is running when it has no recorded end, is still flagged live, <em>and</em>
    /// the event is live. The session's own two fields for the reason <c>ExportsController.HasEnded</c>
    /// gives: a processor that dies mid-session retires the row without writing an end time but still
    /// clears the flag, and a session in that state is over - reading it as running would chart it
    /// growing forever. The event's flag for the case where the session's got stuck: it only clears
    /// once a relay has been silent for the orchestrator's full timeout, so it does not flicker through
    /// an ordinary dropout at the track, and it is cleared at the same teardown that retires the
    /// session. A running session is swept to the as-of time. One that is not gets the report's
    /// fallback - the last viewer activity seen for the event, or failing that the end of its last
    /// day - which is exactly where the report will end it.
    /// </para>
    /// <para>
    /// The running session is only ever the latest. An earlier session with no end of its own ends
    /// when the next one began, as it does in the report.
    /// </para>
    /// <para>
    /// Every window is then cut to the plausible window, from <paramref name="earliestUtc"/> to
    /// <paramref name="latestUtc"/>, and a session with nothing left inside it is dropped. The upper
    /// end is never past the as-of time, so this also clamps a recorded end in the future - only
    /// possible with a clock between two hosts running ahead. A session cut short by the day bound is
    /// reported as ended there rather than as running, because its buckets no longer reach "now".
    /// </para>
    /// </remarks>
    internal static List<(SessionWindow Window, bool Running)> ResolveSessions(
        IReadOnlyCollection<Session> racingSessions, IReadOnlyCollection<EventViewerSession> viewerSessions,
        DateTime eventEndDate, bool eventIsLive, DateTime earliestUtc, DateTime latestUtc, DateTime asOf)
    {
        // The resolver's own order, so "newest" names the same session here as there.
        var newest = racingSessions.OrderBy(s => s.StartTime).ThenBy(s => s.Id).LastOrDefault();
        var running = eventIsLive && newest is { EndTime: null, IsLive: true };

        var fallback = running
            ? asOf
            : ViewershipWindow.LastSessionFallbackEndUtc(
                viewerSessions.Count == 0 ? null : viewerSessions.Max(s => s.EndUtc ?? s.StartUtc),
                eventEndDate, asOf);

        var windows = new List<(SessionWindow Window, bool Running)>();
        foreach (var window in SessionWindowResolver.Resolve(racingSessions, fallback))
        {
            var start = window.StartUtc < earliestUtc ? earliestUtc : window.StartUtc;
            var end = window.EndUtc > latestUtc ? latestUtc : window.EndUtc;
            if (end <= start)
            {
                continue;
            }

            var isRunning = running && window.SessionId == newest!.Id && end == asOf;
            windows.Add((window with { StartUtc = start, EndUtc = end }, isRunning));
        }

        return windows;
    }
}
