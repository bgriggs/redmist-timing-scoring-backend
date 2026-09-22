using RedMist.Backend.Shared.Utilities;
using RedMist.Database.Models;
using RedMist.EventManagement.Models;
using RedMist.EventManagement.Viewership;
using RedMist.PostEventReports;
using RedMist.PostEventReports.Sections.Viewership;
using RedMist.TimingCommon.Models;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RedMist.TimingAndScoringService.Tests.EventManagement;

/// <summary>
/// Covers computing a running event's viewership live, for the organizer dashboard.
/// </summary>
/// <remarks>
/// Pure arithmetic with no fixture, like the report aggregator's tests and for the same reason: every
/// mistake here is a plausible number rather than a failure, and an organizer watches it as fact.
/// </remarks>
[TestClass]
public class LiveViewershipCalculatorTests
{
    private const int EventId = 1;
    private static readonly DateTime Origin = new(2026, 9, 19, 14, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The event's dates as the organizer entered them: days, landing at midnight. Different days on
    /// purpose, so that a start and end passed the wrong way round cannot go unnoticed.
    /// </summary>
    private static readonly DateTime EventStart = new(2026, 9, 19);
    private static readonly DateTime EventEnd = new(2026, 9, 20);

    /// <summary>The lower plausibility bound for <see cref="EventStart"/>: noon UTC the day before.</summary>
    private static readonly DateTime Earliest = Origin.AddHours(-26);

    /// <summary>The live upper bound for <see cref="EventEnd"/>: the end of that day plus twelve hours.</summary>
    private static readonly DateTime DayBound = Origin.AddHours(46);

    private static EventViewerSession Viewer(double startMinute, double? endMinute, string clientType = "Web")
        => new()
        {
            EventId = EventId,
            ConnectionId = Guid.NewGuid().ToString(),
            ClientType = clientType,
            StartUtc = Origin.AddMinutes(startMinute),
            EndUtc = endMinute is { } e ? Origin.AddMinutes(e) : null,
        };

    private static Session Racing(int id, string name, double startMinute, double? endMinute, bool isLive = false,
        bool isPracticeQualifying = false)
        => new()
        {
            Id = id,
            EventId = EventId,
            Name = name,
            StartTime = Origin.AddMinutes(startMinute),
            EndTime = endMinute is { } e ? Origin.AddMinutes(e) : null,
            IsLive = isLive,
            IsPracticeQualifying = isPracticeQualifying,
            LocalTimeZoneOffset = -4,
        };

    private static LiveViewershipDto Compute(IEnumerable<EventViewerSession> viewers, IEnumerable<Session> sessions,
        double asOfMinute, bool eventIsLive = true, DateTime? eventStart = null, DateTime? eventEnd = null)
        => LiveViewershipCalculator.Compute(EventId, eventStart ?? EventStart, eventEnd ?? EventEnd, eventIsLive,
            [.. sessions], [.. viewers], Origin.AddMinutes(asOfMinute));

    private static DateTime At(double minute) => Origin.AddMinutes(minute);

    #region Buckets

    /// <summary>
    /// Four connections laid out by hand so that each minute has a different shape: a dip, a
    /// three-way overlap, and a short overlap. A sweep at the report's fifteen minutes, or an average
    /// taken over the wrong length, gets at least one of them wrong.
    /// </summary>
    [TestMethod]
    public void OneMinuteBuckets_CarryTheMinMaxAndAverageOfTheirOwnMinute()
    {
        var result = Compute(
            [
                Viewer(0, 3),         // A: the whole session
                Viewer(0.5, 1.5),     // B: the second half of minute 0 and the first half of minute 1
                Viewer(1, 2),         // C: all of minute 1
                Viewer(2.25, 2.5833), // D: 14:02:15 to 14:02:35
            ],
            [Racing(1, "Race", 0, 3)],
            asOfMinute: 10);

        var buckets = result.Sessions.Single().Buckets;

        Assert.HasCount(3, buckets);
        CollectionAssert.AreEqual(new[] { At(0), At(1), At(2) }, buckets.Select(b => b.StartUtc).ToArray());

        Assert.AreEqual(1, buckets[0].Min, "Only A for the first thirty seconds.");
        Assert.AreEqual(2, buckets[0].Max);
        Assert.AreEqual(1.5, buckets[0].Avg, 0.001);

        Assert.AreEqual(2, buckets[1].Min, "A and C never left during minute 1.");
        Assert.AreEqual(3, buckets[1].Max);
        Assert.AreEqual(2.5, buckets[1].Avg, 0.001);

        Assert.AreEqual(1, buckets[2].Min);
        Assert.AreEqual(2, buckets[2].Max);
        Assert.AreEqual(80 / 60d, buckets[2].Avg, 0.01);
    }

    [TestMethod]
    public void SessionFigures_ComeFromItsOwnMinutes()
    {
        var result = Compute(
            [Viewer(0, 3), Viewer(0.5, 1.5), Viewer(1, 2), Viewer(2.25, 2.5833)],
            [Racing(1, "Race", 0, 3)],
            asOfMinute: 10);

        var session = result.Sessions.Single();

        Assert.AreEqual(320, session.TotalViewerSeconds, 1);
        Assert.AreEqual(3, session.MaxConcurrent);
        Assert.AreEqual(At(1), session.PeakUtc, "The peak was reached in the second minute, not the first.");
        Assert.AreEqual(320 / 180d, session.AvgConcurrent, 0.01);
    }

    /// <summary>
    /// The minute a running session is still in has only been going for the seconds since it began.
    /// One viewer there the whole time is one viewer, not half of one - dividing by the full minute
    /// would draw every live line sagging toward zero at "now".
    /// </summary>
    [TestMethod]
    public void PartialLastBucket_OfARunningSession_AveragesOverTheElapsedPartOnly()
    {
        var result = Compute([Viewer(0, null)], [Racing(1, "Race", 0, null, isLive: true)], asOfMinute: 2.5);

        var buckets = result.Sessions.Single().Buckets;
        var last = buckets[^1];

        Assert.HasCount(3, buckets);
        Assert.AreEqual(At(2), last.StartUtc);
        Assert.AreEqual(1d, last.Avg, 0.0001, "The elapsed thirty seconds were averaged over the full minute.");
        Assert.AreEqual(1, last.Min);
        Assert.AreEqual(1, last.Max);
        Assert.AreEqual(1d, result.Sessions.Single().AvgConcurrent, 0.0001);
    }

    /// <summary>
    /// The same rule when the short bucket is not flat: its min, max and mean all describe the thirty
    /// seconds that have happened, not the thirty still to come.
    /// </summary>
    [TestMethod]
    public void PartialLastBucket_DescribesOnlyWhatHasElapsed()
    {
        var result = Compute(
            [Viewer(0, null), Viewer(2, 2.25)],
            [Racing(1, "Race", 0, null, isLive: true)],
            asOfMinute: 2.5);

        var last = result.Sessions.Single().Buckets[^1];

        Assert.AreEqual(1, last.Min);
        Assert.AreEqual(2, last.Max);
        Assert.AreEqual(45 / 30d, last.Avg, 0.0001, "45 connected seconds over the 30 elapsed.");
    }

    /// <summary>An ended session's last minute is usually short too, and takes the same rule.</summary>
    [TestMethod]
    public void PartialLastBucket_OfAnEndedSession_AlsoAveragesOverWhatItCovers()
    {
        var result = Compute([Viewer(0, 10)], [Racing(1, "Race", 0, 2.5)], asOfMinute: 10);

        Assert.AreEqual(1d, result.Sessions.Single().Buckets[^1].Avg, 0.0001);
    }

    #endregion

    #region Event figures

    /// <summary>
    /// The average across sessions leaves the gap between them out, so an audience drifting away over
    /// lunch does not drag down the figure for the racing - but that time was still watched, and the
    /// total keeps it.
    /// </summary>
    [TestMethod]
    public void AvgConcurrentDuringSessions_LeavesTheGapOut_WhileTheTotalKeepsIt()
    {
        var result = Compute(
            [
                Viewer(0, 10),    // throughout the first session
                Viewer(12, 18),   // only in the gap
                Viewer(20, null), // throughout the second, still connected
                Viewer(20, 25),   // the first half of the second
            ],
            [Racing(1, "Practice", 0, 10), Racing(2, "Race", 20, null, isLive: true)],
            asOfMinute: 30);

        Assert.AreEqual(600 + 360 + 600 + 300, result.Event.TotalViewerSeconds, "The gap's viewer was not in the total.");
        // (600 + 900) / (600 + 600). Including the gap would give 1860 / 1800; putting its viewer in
        // the numerator alone would give 1860 / 1200.
        Assert.AreEqual(1.25, result.Event.AvgConcurrentDuringSessions, 0.0001);
        Assert.AreEqual(1d, result.Sessions[0].AvgConcurrent, 0.0001);
        Assert.AreEqual(1.5, result.Sessions[1].AvgConcurrent, 0.0001);
    }

    [TestMethod]
    public void EventPeak_IsTheFirstMinuteThatReachedTheMaximum()
    {
        var result = Compute(
            [Viewer(3, 5), Viewer(3.5, 5), Viewer(8, 9), Viewer(8, 9)],
            [Racing(1, "Race", 0, null, isLive: true)],
            asOfMinute: 10);

        Assert.AreEqual(2, result.Event.MaxConcurrent);
        Assert.AreEqual(At(3), result.Event.PeakUtc, "A later minute equalling the peak moved it.");
    }

    /// <summary>
    /// The window starts where the post-event report's will: the first connection, rounded down to
    /// the quarter-hour. A minute-aligned start would sit seven minutes later for the same rows.
    /// </summary>
    [TestMethod]
    public void WindowStart_IsTheReportsQuarterHour_NotTheMinute()
    {
        var result = Compute([Viewer(7.5, 20)], [], asOfMinute: 30);

        Assert.AreEqual(At(0), result.Event.WindowStartUtc);
    }

    #endregion

    #region Open and anomalous rows

    /// <summary>
    /// A row still open is somebody watching now, so it counts up to the as-of time - and not a moment
    /// past it. A row that ends before it starts is discarded, as the report discards it: read with
    /// its ends swapped it would add a second viewer and seven minutes that never happened.
    /// </summary>
    [TestMethod]
    public void OpenRow_IsClampedToAsOf_AndAnAnomalousRowIsExcluded()
    {
        var anomalous = Viewer(8, 1);

        var result = Compute([Viewer(5, null), anomalous], [Racing(1, "Race", 0, null, isLive: true)], asOfMinute: 10);

        Assert.AreEqual(300, result.Event.TotalViewerSeconds, "The open row was not counted to exactly the as-of time.");
        Assert.AreEqual(300, result.Sessions.Single().TotalViewerSeconds);
        Assert.AreEqual(1, result.Event.MaxConcurrent, "The anomalous row was counted.");
        Assert.HasCount(10, result.Sessions.Single().Buckets, "A bucket was charted beyond the as-of time.");
    }

    /// <summary>
    /// Nobody has watched the future. A closed row whose end is beyond now - a clock running ahead -
    /// is cut at now, as an open one is.
    /// </summary>
    [TestMethod]
    public void ARowEndingAfterAsOf_IsCutAtAsOf()
    {
        var result = Compute([Viewer(0, 60)], [Racing(1, "Race", 0, null, isLive: true)], asOfMinute: 10);

        Assert.AreEqual(600, result.Event.TotalViewerSeconds);
    }

    #endregion

    #region Plausible window

    /// <summary>
    /// A row left open after the event would otherwise count until whoever asks next - a month later,
    /// a month of watching. It stops at the end of the event's last day plus twelve hours.
    /// </summary>
    [TestMethod]
    public void AFinishedEventsOpenRow_IsCountedToTheEndOfItsLastDay_NotToNow()
    {
        var result = Compute([Viewer(0, null)], [], asOfMinute: TimeSpan.FromDays(30).TotalMinutes, eventIsLive: false);

        Assert.AreEqual((long)(DayBound - Origin).TotalSeconds, result.Event.TotalViewerSeconds);
        Assert.AreEqual(Origin.AddDays(30), result.AsOfUtc);
    }

    /// <summary>
    /// A finished event whose last session and whose own live flag were both left set would chart
    /// that session to now: a month of one-minute buckets on every request. The day bound stops it,
    /// and a session cut short there is not reported as running, since it no longer reaches now.
    /// </summary>
    [TestMethod]
    public void AFinishedEventsStuckSession_IsChartedOnlyToTheDayBound()
    {
        var result = Compute([Viewer(0, null)], [Racing(1, "Race", 0, null, isLive: true)],
            asOfMinute: TimeSpan.FromDays(30).TotalMinutes, eventIsLive: true);

        var session = result.Sessions.Single();
        Assert.HasCount((int)(DayBound - Origin).TotalMinutes, session.Buckets);
        Assert.AreEqual(DayBound, session.EndUtc);
        Assert.AreEqual(1d, session.AvgConcurrent, 0.0001);
    }

    /// <summary>
    /// A row that began before the lower bound is counted from the bound. Built so that the bound can
    /// only be the event's start date: its end date would put it a day later, and no bound at all
    /// would count four more hours.
    /// </summary>
    [TestMethod]
    public void ARowStartingBeforeTheLowerBound_IsCountedFromIt()
    {
        var result = Compute([Viewer(-30 * 60, 10)], [], asOfMinute: 10);

        Assert.AreEqual(Earliest, result.Event.WindowStartUtc);
        Assert.AreEqual((long)(Origin.AddMinutes(10) - Earliest).TotalSeconds, result.Event.TotalViewerSeconds);
    }

    /// <summary>
    /// Relay tests the day before become sessions too, and one left unretired runs on until the first
    /// real session begins. Its viewers were already cut by the lower bound; its hours must be too, or
    /// they sit in the average across sessions as time nobody watched and in the payload as a long run
    /// of empty buckets. One wholly before the bound is left out altogether.
    /// </summary>
    [TestMethod]
    public void SessionsBeforeTheEvent_AreCutToThePlausibleWindow()
    {
        var eventDay = new DateTime(2026, 9, 20);
        var windowOpens = Origin.AddHours(-2);

        var result = Compute(
            [Viewer(-240, -180), Viewer(0, 60)],
            [
                Racing(1, "Thursday test", -40 * 60, -39 * 60),
                Racing(2, "Relay test", -5 * 60, null),
                Racing(3, "Race", 0, null, isLive: true),
            ],
            asOfMinute: 60, eventStart: eventDay, eventEnd: eventDay);

        CollectionAssert.AreEqual(new[] { 2, 3 }, result.Sessions.Select(s => s.SessionId).ToArray());
        Assert.AreEqual(windowOpens, result.Sessions[0].StartUtc);
        Assert.AreEqual(Origin, result.Sessions[0].EndUtc);
        Assert.HasCount(120, result.Sessions[0].Buckets, "The relay test was charted from before the window.");
        Assert.HasCount(60, result.Sessions[1].Buckets);
        // 3600 watched seconds over two hours of relay test inside the window and one hour of race.
        // Uncut, the relay test's five hours - and the Thursday test's one - would halve it.
        Assert.AreEqual(3600 / (7200d + 3600), result.Event.AvgConcurrentDuringSessions, 0.0001);
    }

    /// <summary>
    /// A recorded end in the future - a clock between two hosts running ahead - is cut at now, and a
    /// session that has not begun yet as far as this clock is concerned is not shown at all.
    /// </summary>
    [TestMethod]
    public void SessionsReachingPastAsOf_AreCutAtIt()
    {
        var result = Compute([Viewer(0, null)], [Racing(1, "Practice", 0, 45), Racing(2, "Race", 40, 50)],
            asOfMinute: 30);

        var session = result.Sessions.Single();
        Assert.AreEqual(1, session.SessionId);
        Assert.AreEqual(At(30), session.EndUtc);
        Assert.HasCount(30, session.Buckets);
    }

    #endregion

    #region Sessions

    /// <summary>
    /// Every session, oldest first however they arrive. Only the latest can be running, and an
    /// earlier one with no end of its own ends when the next began, as it does in the report.
    /// </summary>
    [TestMethod]
    public void Sessions_AreOldestFirst_AndOnlyTheLatestUnendedOneIsRunning()
    {
        var result = Compute(
            [Viewer(0, null)],
            [
                Racing(3, "Race", 40, null, isLive: true),
                Racing(1, "Practice", 0, 15, isPracticeQualifying: true),
                Racing(2, "Qualifying", 20, null, isPracticeQualifying: true),
            ],
            asOfMinute: 50);

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, result.Sessions.Select(s => s.SessionId).ToArray());

        Assert.AreEqual(At(15), result.Sessions[0].EndUtc);
        Assert.AreEqual(At(40), result.Sessions[1].EndUtc, "An earlier session with no end should end at the next start.");
        Assert.IsNull(result.Sessions[2].EndUtc, "The running session was reported as ended.");

        var running = result.Sessions[2];
        Assert.AreEqual("Race", running.SessionName);
        Assert.IsFalse(running.IsPracticeQualifying);
        Assert.IsTrue(result.Sessions[0].IsPracticeQualifying);
        Assert.HasCount(10, running.Buckets, "The running session was not charted up to the as-of time.");
        Assert.AreEqual(At(49), running.Buckets[^1].StartUtc);
    }

    /// <summary>
    /// A processor that dies mid-session retires the row without writing an end, but clears the live
    /// flag. That session is over; reading it as running would chart it growing for as long as anyone
    /// looked. It ends where the report will end it: the last viewer activity seen.
    /// </summary>
    [TestMethod]
    public void ALatestSessionNoLongerFlaggedLive_IsNotRunning_AndEndsWhereTheReportEndsIt()
    {
        var result = Compute(
            [Viewer(0, 20), Viewer(5, null)],
            [Racing(1, "Race", 0, null, isLive: false)],
            asOfMinute: 60);

        var session = result.Sessions.Single();
        Assert.AreEqual(At(20), session.EndUtc, "The last activity was the close at 14:20.");
        Assert.HasCount(20, session.Buckets);
    }

    /// <summary>
    /// A session flag can stick when the processor that owns it dies. The event's own flag is kept by
    /// the orchestrator from relay heartbeats, so once the event has been torn down its latest session
    /// is not running, whatever that session's row still says.
    /// </summary>
    [TestMethod]
    public void TheLatestSession_IsNotRunning_OnceTheEventIsNotLive()
    {
        var result = Compute(
            [Viewer(0, 20), Viewer(5, null)],
            [Racing(1, "Race", 0, null, isLive: true)],
            asOfMinute: 60, eventIsLive: false);

        Assert.AreEqual(At(20), result.Sessions.Single().EndUtc);
    }

    /// <summary>
    /// A final session that nobody watched and that never recorded an end falls back to the end of
    /// the event's last day. Falling back to the end date itself - that day's first moment - gave it
    /// an end before its start, and it vanished from the list.
    /// </summary>
    [TestMethod]
    public void AnUnwatchedFinalSessionWithNoEnd_EndsWithTheLastDay()
    {
        var raceDay = new DateTime(2026, 9, 19);

        var result = Compute([], [Racing(1, "Race", 0, null)], asOfMinute: 2 * 24 * 60, eventIsLive: false,
            eventStart: raceDay, eventEnd: raceDay);

        var session = result.Sessions.Single();
        Assert.AreEqual(raceDay.AddDays(1), session.EndUtc);
        Assert.HasCount(10 * 60, session.Buckets);
    }

    /// <summary>The report drops a session whose end is not after its start; so does this.</summary>
    [TestMethod]
    public void ASessionEndingBeforeItStarted_IsDroppedAsTheReportDropsIt()
    {
        var result = Compute(
            [Viewer(0, 30)],
            [Racing(1, "Broken", 10, 5), Racing(2, "Race", 20, null, isLive: true)],
            asOfMinute: 30);

        CollectionAssert.AreEqual(new[] { 2 }, result.Sessions.Select(s => s.SessionId).ToArray());
    }

    [TestMethod]
    public void AnEventWithNoSessionsYet_StillHasItsEventFigures()
    {
        var result = Compute([Viewer(7, 20), Viewer(10, 15)], [], asOfMinute: 30);

        Assert.IsEmpty(result.Sessions);
        Assert.AreEqual(At(0), result.Event.WindowStartUtc);
        Assert.AreEqual(13 * 60 + 5 * 60, result.Event.TotalViewerSeconds);
        Assert.AreEqual(2, result.Event.MaxConcurrent);
        Assert.AreEqual(At(10), result.Event.PeakUtc);
        Assert.AreEqual(0d, result.Event.AvgConcurrentDuringSessions);
        Assert.IsNull(result.TrackOffsetMinutes, "No session means no reported offset.");
    }

    /// <summary>
    /// Nobody watching yet is zeros - and still a full set of buckets, so the chart draws a flat line
    /// across the session rather than nothing at all.
    /// </summary>
    [TestMethod]
    public void AnEventWithNoViewers_IsZeros_WithBucketsStillPresent()
    {
        var result = Compute([], [Racing(1, "Race", 0, null, isLive: true)], asOfMinute: 5);

        Assert.AreEqual(0, result.Event.TotalViewerSeconds);
        Assert.AreEqual(0, result.Event.MaxConcurrent);
        Assert.IsNull(result.Event.PeakUtc);
        Assert.AreEqual(0d, result.Event.AvgConcurrentDuringSessions);
        Assert.AreEqual(At(5), result.Event.WindowStartUtc, "An empty window should sit at the as-of time.");

        var session = result.Sessions.Single();
        Assert.HasCount(5, session.Buckets);
        Assert.IsTrue(session.Buckets.All(b => b.Min == 0 && b.Max == 0 && b.Avg == 0));
        Assert.IsNull(session.PeakUtc);
        Assert.AreEqual(-240, result.TrackOffsetMinutes);
    }

    #endregion

    #region Agreement with the report

    /// <summary>
    /// The point of sharing the rules: for the same rows and sessions, the live view and the report
    /// produce the same window start, the same total, the same peaks and the same open and anomalous
    /// handling. A difference here is a number the organizer watched on race day that the report then
    /// contradicts.
    /// </summary>
    /// <remarks>
    /// Each side takes its bounds, track offset, session windows and maximum window exactly where its
    /// production caller does - the live view inside the calculator, the report from the same helpers
    /// <c>ViewershipSection</c> and the job call - with nothing chosen by the test. Checked twice: at
    /// one instant during the event, and for the finished event read at two different times after
    /// it, where both stop at the end of the last day plus the margin and so count the open row alike.
    /// </remarks>
    [TestMethod]
    [DataRow(120d, 120d, true, DisplayName = "During the event, at the same instant")]
    [DataRow((46 + 24) * 60d, (46 + 5 * 24) * 60d, false, DisplayName = "After the event, read at different times")]
    public void ForTheSameRows_TheLiveFiguresAgreeWithTheReport(double liveAsOfMinute, double reportRunMinute,
        bool eventIsLive)
    {
        List<EventViewerSession> viewers =
        [
            Viewer(7.5, 47, "iOS"), Viewer(12, 90, "Android"), Viewer(30, 31), Viewer(33, null), Viewer(50, 45),
            Viewer(61, 62), Viewer(61, 75, "API"), Viewer(64, 64),
        ];
        List<Session> sessions = [Racing(1, "Practice", 5, 40), Racing(2, "Race", 55, 100)];
        var reportRun = At(reportRunMinute);

        var live = Compute(viewers, sessions, liveAsOfMinute, eventIsLive);
        var report = ViewershipAggregator.Aggregate(
            viewers,
            SessionWindowResolver.Resolve(sessions, ViewershipWindow.LastSessionFallbackEndUtc(
                viewers.Max(v => v.EndUtc ?? v.StartUtc), EventEnd, reportRun)),
            TrackTime.ForEvent(sessions.OrderBy(s => s.StartTime).Select(s => s.LocalTimeZoneOffset)),
            ViewershipWindow.EarliestPlausibleUtc(EventStart),
            ViewershipWindow.LatestPlausibleUtc(EventEnd, reportRun),
            new PostEventReportSettings().MaxWindow);

        Assert.AreEqual(1, report.OpenSessions, "The fixture is meant to exercise an open row.");
        Assert.AreEqual(1, report.AnomalousSessions, "The fixture is meant to exercise an anomalous row.");
        Assert.AreEqual(report.WindowStartUtc, live.Event.WindowStartUtc);
        Assert.AreEqual(report.MaxConcurrent, live.Event.MaxConcurrent);
        Assert.AreEqual(report.TotalViewerMinutes, live.Event.TotalViewerSeconds / 60d, 0.1);
        Assert.AreEqual(report.TrackOffsetMinutes, live.TrackOffsetMinutes);

        foreach (var session in live.Sessions)
        {
            var reported = report.Sessions.Single(s => s.SessionId == session.SessionId);
            Assert.AreEqual(reported.StartUtc, session.StartUtc);
            Assert.AreEqual(reported.EndUtc, session.EndUtc);
            Assert.AreEqual(reported.MaxConcurrent, session.MaxConcurrent);
            Assert.AreEqual(reported.TotalViewerMinutes, session.TotalViewerSeconds / 60d, 0.1);
            // The live peak is a minute inside the report's quarter-hour: the same moment, finer.
            Assert.IsTrue(session.PeakUtc >= reported.PeakUtc && session.PeakUtc < reported.PeakUtc + ViewershipAggregator.BucketLength,
                $"Session {session.SessionId} peaked at {session.PeakUtc:HH:mm:ss} live but {reported.PeakUtc:HH:mm:ss} in the report.");
        }
    }

    #endregion

    #region Timestamps

    /// <summary>
    /// PostgreSQL hands these columns back with no Kind. Written out as-is they carry no "Z", and a
    /// browser parses them as its own local time - every point on the chart moved by the viewer's
    /// offset from UTC, with nothing failing.
    /// </summary>
    [TestMethod]
    public void EveryTimestamp_IsUtc_EvenWhenReadWithoutAKind()
    {
        static DateTime Unspecified(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

        List<EventViewerSession> viewers = [Viewer(0, 12), Viewer(3, null)];
        foreach (var viewer in viewers)
        {
            viewer.StartUtc = Unspecified(viewer.StartUtc);
            viewer.EndUtc = viewer.EndUtc is { } end ? Unspecified(end) : null;
        }
        List<Session> sessions = [Racing(1, "Practice", 0, 5), Racing(2, "Race", 6, null, isLive: false)];
        foreach (var session in sessions)
        {
            session.StartTime = Unspecified(session.StartTime);
            session.EndTime = session.EndTime is { } end ? Unspecified(end) : null;
        }

        var result = LiveViewershipCalculator.Compute(EventId, EventStart, EventEnd, eventIsLive: true, sessions,
            viewers, Unspecified(At(20)));

        var timestamps = LiveViewershipWireFormat.AllDateTimes(result).ToList();
        Assert.IsTrue(timestamps.Count > 10, "The walk found too few timestamps to prove anything.");
        Assert.IsTrue(timestamps.All(t => t.Kind == DateTimeKind.Utc),
            "Not UTC: " + string.Join(", ", timestamps.Where(t => t.Kind != DateTimeKind.Utc).Select(t => $"{t:O} ({t.Kind})")));

        LiveViewershipWireFormat.AssertEveryTimestampHasZ(JsonSerializer.Serialize(result, LiveViewershipWireFormat.WebOptions));
    }

    #endregion
}

/// <summary>
/// Checks shared by the calculator and controller tests for how the live DTO goes on the wire.
/// </summary>
internal static class LiveViewershipWireFormat
{
    /// <summary>What ASP.NET Core serializes responses with in this service, which configures no JSON options.</summary>
    public static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly Regex Timestamp = new("\"(\\d{4}-\\d{2}-\\d{2}T[^\"]*)\"", RegexOptions.Compiled);

    /// <summary>Every DateTime reachable from <paramref name="value"/>, through nested objects and lists.</summary>
    public static IEnumerable<DateTime> AllDateTimes(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case DateTime dateTime:
                yield return dateTime;
                yield break;
            case string:
                yield break;
            case IEnumerable items:
                foreach (var item in items)
                {
                    foreach (var found in AllDateTimes(item))
                    {
                        yield return found;
                    }
                }
                yield break;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal)
        {
            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (var found in AllDateTimes(property.GetValue(value)))
            {
                yield return found;
            }
        }
    }

    /// <summary>Asserts that every ISO timestamp in <paramref name="json"/> carries a UTC designator.</summary>
    public static void AssertEveryTimestampHasZ(string json)
    {
        var stamps = Timestamp.Matches(json).Select(m => m.Groups[1].Value).ToList();
        Assert.IsTrue(stamps.Count > 10, "Too few timestamps in the JSON to prove anything.");

        var local = stamps.Where(s => !s.EndsWith('Z')).ToList();
        Assert.IsEmpty(local, "Written without a Z, so a browser reads them as local time: " + string.Join(", ", local));
    }
}
