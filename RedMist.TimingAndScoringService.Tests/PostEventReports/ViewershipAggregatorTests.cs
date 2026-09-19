using RedMist.Backend.Shared.Utilities;
using RedMist.Database.Models;
using RedMist.PostEventReports.Sections.Viewership;

namespace RedMist.TimingAndScoringService.Tests.PostEventReports;

/// <summary>
/// Covers turning an event's raw viewer sessions into the figures the report presents.
/// </summary>
[TestClass]
public class ViewershipAggregatorTests
{
    private static readonly DateTime Origin = new(2026, 9, 19, 14, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MaxWindow = TimeSpan.FromHours(96);

    private static EventViewerSession Session(int startMinute, int? endMinute, string clientType = "Web")
        => new()
        {
            EventId = 1,
            ConnectionId = Guid.NewGuid().ToString(),
            ClientType = clientType,
            StartUtc = Origin.AddMinutes(startMinute),
            EndUtc = endMinute is { } e ? Origin.AddMinutes(e) : null,
        };

    private static EventViewershipSummary Aggregate(IEnumerable<EventViewerSession> sessions,
        IEnumerable<SessionWindow>? racing = null, TimeSpan? offset = null, DateTime? latest = null)
        => ViewershipAggregator.Aggregate(
            [.. sessions],
            [.. racing ?? []],
            offset,
            Origin.AddDays(-2),
            latest ?? Origin.AddHours(12),
            MaxWindow);

    private static List<EventViewershipBucket> EventSeries(EventViewershipSummary summary, string clientType)
        => [.. summary.Buckets.Where(b => b.SessionId == null && b.ClientType == clientType).OrderBy(b => b.BucketStartUtc)];

    [TestMethod]
    public void NoSessions_ProducesAnEmptySummary()
    {
        var summary = Aggregate([]);

        Assert.AreEqual(0, summary.SessionCount);
        Assert.AreEqual(0d, summary.TotalViewerMinutes);
        Assert.IsEmpty(summary.Buckets);
    }

    [TestMethod]
    public void TotalViewerMinutes_IsTheSumOfTheSessionDurations()
    {
        var summary = Aggregate([Session(0, 30), Session(10, 25)]);

        Assert.AreEqual(45d, summary.TotalViewerMinutes, 0.1);
    }

    /// <summary>
    /// The identity that ties the chart to the headline figure: the all-types averages, integrated
    /// over the buckets, are the total viewing time.
    /// </summary>
    [TestMethod]
    public void TotalViewerMinutes_EqualsTheAllTypesAveragesIntegratedOverTheBuckets()
    {
        var summary = Aggregate([Session(0, 47), Session(12, 90, "iOS"), Session(30, 31, "Android")]);

        var fromBuckets = EventSeries(summary, ViewerClientTypes.All)
            .Sum(b => b.AvgConcurrent * ViewershipAggregator.BucketLength.TotalMinutes);

        Assert.AreEqual(summary.TotalViewerMinutes, fromBuckets, 0.2);
    }

    [TestMethod]
    public void PerTypeAverages_SumToTheAllTypesAverageInEveryBucket()
    {
        var summary = Aggregate(
            [Session(0, 40, "iOS"), Session(5, 50, "Android"), Session(0, 60, "Web"), Session(20, 22, "API")]);

        var all = EventSeries(summary, ViewerClientTypes.All);
        foreach (var bucket in all)
        {
            var perType = ViewerClientTypes.Reported
                .Sum(t => EventSeries(summary, t).Single(b => b.BucketStartUtc == bucket.BucketStartUtc).AvgConcurrent);
            Assert.AreEqual(bucket.AvgConcurrent, perType, 0.001,
                $"Bucket at {bucket.BucketStartUtc:HH:mm} does not add up.");
        }
    }

    /// <summary>
    /// A session left open is real viewership - a tab open all weekend - so it is counted to the end
    /// of the window rather than discarded, and flagged so the report can say the total rests on an
    /// assumption.
    /// </summary>
    [TestMethod]
    public void SessionWithNoEnd_IsClampedToTheWindowAndCounted()
    {
        var summary = Aggregate([Session(0, null)], latest: Origin.AddMinutes(30));

        Assert.AreEqual(1, summary.OpenSessions);
        Assert.AreEqual(30d, summary.TotalViewerMinutes, 0.1);
    }

    [TestMethod]
    public void SessionEndingBeforeItStarted_IsDiscardedAndCounted()
    {
        var summary = Aggregate([Session(30, 10), Session(0, 20)]);

        Assert.AreEqual(1, summary.AnomalousSessions);
        Assert.AreEqual(20d, summary.TotalViewerMinutes, 0.1);
    }

    /// <summary>
    /// The footnote about open sessions tells the organizer they were counted to the end of the
    /// event. A session that was thrown away must not be described that way as well.
    /// </summary>
    [TestMethod]
    public void SessionWithNoEndThatIsAlsoAnomalous_IsCountedOnlyOnce()
    {
        var summary = ViewershipAggregator.Aggregate(
            [new EventViewerSession
            {
                EventId = 1,
                ConnectionId = "c",
                ClientType = "Web",
                StartUtc = Origin.AddHours(5),
                EndUtc = null,
            }],
            [],
            null,
            Origin.AddDays(-2),
            Origin.AddHours(1),
            MaxWindow);

        Assert.AreEqual(1, summary.AnomalousSessions);
        Assert.AreEqual(0, summary.OpenSessions);
    }

    [TestMethod]
    public void ZeroLengthSession_ContributesNothing()
    {
        var summary = Aggregate([Session(0, 20), Session(5, 5)]);

        Assert.AreEqual(20d, summary.TotalViewerMinutes, 0.1);
        Assert.AreEqual(1, summary.MaxConcurrent);
    }

    /// <summary>
    /// By viewing time, not session count. A mobile client that reconnects every half minute would
    /// otherwise take the title from the platform people actually watched on.
    /// </summary>
    [TestMethod]
    public void TopClientType_IsDecidedByViewingTimeNotSessionCount()
    {
        // One web viewer for an hour against a phone that reconnected a hundred times, thirty seconds
        // apiece: fifty minutes of iOS across a hundred sessions, sixty minutes of web across one.
        var sessions = new List<EventViewerSession> { Session(0, 60, ViewerClientTypes.Web) };
        for (var i = 0; i < 100; i++)
        {
            sessions.Add(new EventViewerSession
            {
                EventId = 1,
                ConnectionId = Guid.NewGuid().ToString(),
                ClientType = ViewerClientTypes.IOS,
                StartUtc = Origin.AddSeconds(i * 30),
                EndUtc = Origin.AddSeconds((i * 30) + 30),
            });
        }

        var summary = Aggregate(sessions);

        Assert.AreEqual(ViewerClientTypes.Web, summary.TopClientType,
            "iOS has a hundred times the sessions but less of the watching.");
    }

    [TestMethod]
    public void MaxConcurrent_IsNotTheSumOfThePerTypePeaks()
    {
        var summary = Aggregate(
        [
            Session(0, 10, "iOS"), Session(0, 10, "iOS"),
            Session(30, 40, "Web"), Session(30, 40, "Web"),
        ]);

        Assert.AreEqual(2, summary.MaxConcurrent);
    }

    [TestMethod]
    public void PeakUtc_IsTheStartOfTheBucketTheMaximumOccurredIn()
    {
        var summary = Aggregate([Session(0, 90), Session(35, 40)]);

        Assert.AreEqual(Origin.AddMinutes(30), summary.PeakUtc);
    }

    /// <summary>
    /// The window comes from the sessions, not from the event's dates, which land at midnight and
    /// would give a chart forty empty buckets before anyone arrived.
    /// </summary>
    [TestMethod]
    public void Window_IsDerivedFromTheSessionsAndAlignedToBucketBoundaries()
    {
        var summary = Aggregate([Session(7, 23)]);

        Assert.AreEqual(Origin, summary.WindowStartUtc);
        Assert.AreEqual(Origin.AddMinutes(30), summary.WindowEndUtc);
    }

    [TestMethod]
    public void Window_IsTruncatedAtTheMaximum()
    {
        var summary = ViewershipAggregator.Aggregate(
            [Session(0, 60 * 24 * 10)],
            [],
            null,
            Origin.AddDays(-2),
            Origin.AddDays(30),
            TimeSpan.FromHours(6));

        Assert.AreEqual(Origin.AddHours(6), summary.WindowEndUtc);
    }

    /// <summary>
    /// An offset of -5 puts a session at 14:07 UTC in the bucket an organizer reads as 09:00.
    /// </summary>
    [TestMethod]
    public void WithATrackOffset_BucketsAlignToLocalQuarterHours()
    {
        var summary = Aggregate([Session(7, 23)], offset: TimeSpan.FromHours(-5));

        Assert.AreEqual(-300, summary.TrackOffsetMinutes);
        Assert.AreEqual(Origin, summary.WindowStartUtc);
        Assert.AreEqual(9, summary.WindowStartUtc.AddMinutes(summary.TrackOffsetMinutes!.Value).Hour);
        Assert.AreEqual(0, summary.WindowStartUtc.AddMinutes(summary.TrackOffsetMinutes.Value).Minute);
    }

    [TestMethod]
    public void WithNoUsableOffset_TheOffsetIsNullSoTimesCanBeLabelledUtc()
    {
        Assert.IsNull(Aggregate([Session(0, 20)], offset: TrackTime.Offset(0)).TrackOffsetMinutes);
        Assert.IsNull(Aggregate([Session(0, 20)], offset: TrackTime.Offset(double.NaN)).TrackOffsetMinutes);
        Assert.IsNull(Aggregate([Session(0, 20)], offset: TrackTime.Offset(15)).TrackOffsetMinutes);
    }

    /// <summary>
    /// Viewers subscribe to an event, never to a racing session, so a viewer watching across a
    /// boundary belongs to both sessions. Time in neither belongs only to the event totals.
    /// </summary>
    [TestMethod]
    public void SessionSpanningTwoRacingSessions_CountsInBoth()
    {
        SessionWindow[] racing =
        [
            new(1, "Practice", true, Origin, Origin.AddMinutes(30)),
            new(2, "Race", false, Origin.AddMinutes(60), Origin.AddMinutes(120)),
        ];

        var summary = Aggregate([Session(15, 75)], racing);

        var practice = summary.Sessions.Single(s => s.SessionId == 1);
        var race = summary.Sessions.Single(s => s.SessionId == 2);
        Assert.AreEqual(15d, practice.TotalViewerMinutes, 0.1);
        Assert.AreEqual(15d, race.TotalViewerMinutes, 0.1);

        // The gap between the two sessions is in neither, but is in the event total.
        Assert.AreEqual(60d, summary.TotalViewerMinutes, 0.1);
    }

    /// <summary>
    /// Back-to-back sessions, neither of which starts on a bucket boundary. Rounding their windows
    /// out to boundaries would overlap them and count the viewer in that overlap twice, so the
    /// sessions would add up to more than the event they are part of.
    /// </summary>
    [TestMethod]
    public void AdjacentRacingSessions_DoNotOverlapAndDoNotDoubleCount()
    {
        SessionWindow[] racing =
        [
            new(1, "Practice", true, Origin, Origin.AddMinutes(67)),
            new(2, "Race", false, Origin.AddMinutes(67), Origin.AddMinutes(120)),
        ];

        var summary = Aggregate([Session(0, 120)], racing);

        var sessionTotal = summary.Sessions.Sum(s => s.TotalViewerMinutes);
        Assert.AreEqual(120d, summary.TotalViewerMinutes, 0.1);
        Assert.AreEqual(120d, sessionTotal, 0.2,
            "The sessions add up to more than the event, so the boundary minutes were counted twice.");
    }

    /// <summary>
    /// The "started" time an organizer reads has to be when the session actually started, not the
    /// quarter hour it was rounded back to.
    /// </summary>
    [TestMethod]
    public void RacingSession_ReportsItsRealStartTimeNotARoundedOne()
    {
        SessionWindow[] racing = [new(1, "Race", false, Origin.AddMinutes(67), Origin.AddMinutes(120))];

        var session = Aggregate([Session(0, 120)], racing).Sessions.Single();

        Assert.AreEqual(Origin.AddMinutes(67), session.StartUtc);
    }

    /// <summary>
    /// A session outside the charted window must still appear in the summary: the summary is the only
    /// thing the "by session" table is built from, so a session dropped here is gone from the report
    /// altogether rather than merely going without a chart.
    /// </summary>
    [TestMethod]
    public void RacingSessionOutsideTheTruncatedWindow_IsStillReported()
    {
        SessionWindow[] racing =
        [
            new(1, "Practice", true, Origin, Origin.AddHours(1)),
            new(2, "Race", false, Origin.AddHours(20), Origin.AddHours(22)),
        ];

        var summary = ViewershipAggregator.Aggregate(
            [Session(0, 60), Session(60 * 20, 60 * 22)],
            racing,
            null,
            Origin.AddDays(-2),
            Origin.AddDays(2),
            TimeSpan.FromHours(6));

        CollectionAssert.AreEquivalent(new[] { "Practice", "Race" },
            summary.Sessions.Select(s => s.SessionName).ToArray());
    }

    [TestMethod]
    public void RacingSessions_CarryTheirNameAndPracticeFlag()
    {
        SessionWindow[] racing = [new(7, "Qualifying", true, Origin, Origin.AddMinutes(30))];

        var session = Aggregate([Session(0, 30)], racing).Sessions.Single();

        Assert.AreEqual(7, session.SessionId);
        Assert.AreEqual("Qualifying", session.SessionName);
        Assert.IsTrue(session.IsPracticeQualifying);
        Assert.AreEqual(1, session.MaxConcurrent);
    }

    [TestMethod]
    public void EveryBucketInTheWindowIsEmitted_SoAChartHasNoGaps()
    {
        var summary = Aggregate([Session(0, 5), Session(55, 60)]);

        var all = EventSeries(summary, ViewerClientTypes.All);
        Assert.HasCount(4, all);
        Assert.AreEqual(0, all[1].MaxConcurrent, "The quiet stretch is still a bucket.");
    }

    [TestMethod]
    public void UnknownClientType_IsFoldedIntoWebSoTheSeriesStillAddUp()
    {
        var summary = Aggregate([Session(0, 30, "SomethingNew")]);

        Assert.AreEqual(ViewerClientTypes.Web, summary.TopClientType);
        Assert.AreEqual(summary.TotalViewerMinutes,
            EventSeries(summary, ViewerClientTypes.Web).Sum(b => b.ViewerSeconds) / 60d, 0.1);
    }
}
