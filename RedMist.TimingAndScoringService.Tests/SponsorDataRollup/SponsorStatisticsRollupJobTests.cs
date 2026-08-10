using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.SponsorDataRollup;

namespace RedMist.TimingAndScoringService.Tests.SponsorDataRollup;

/// <summary>
/// Tests for the monthly sponsor telemetry rollup. The job aggregates the previous calendar
/// month's <see cref="SponsorTelemetryLog"/> rows into total / per-event / per-source
/// <see cref="SponsorStatistics"/>.
/// </summary>
[TestClass]
public class SponsorStatisticsRollupJobTests
{
    private const string IMPRESSION = "Impression";
    private const string VIEWABLE_IMPRESSION = "ViewableImpression";
    private const string CLICK_THROUGH = "ClickThrough";
    private const string ENGAGEMENT_DURATION = "EngagementDuration";

    /// <summary>Any instant inside March 2026, so the job's window is February 2026.</summary>
    private static readonly DateTimeOffset MarchRunTime = new(2026, 3, 10, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly February2026 = new(2026, 2, 1);

    private readonly DebugLoggerFactory lf = new();

    private sealed class Harness
    {
        public required DbContextOptions<TsContext> Options { get; init; }
        public required IDbContextFactory<TsContext> Factory { get; init; }
        public required Mock<IHostApplicationLifetime> Lifetime { get; init; }
        public required CancellationTokenSource AppStarted { get; init; }
        public required FakeTimeProvider Clock { get; init; }

        public TsContext NewContext() => new(Options);
    }

    private static Harness CreateHarness(DateTimeOffset? now = null, bool applicationAlreadyStarted = true)
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var appStarted = new CancellationTokenSource();
        if (applicationAlreadyStarted)
        {
            appStarted.Cancel();
        }

        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(l => l.ApplicationStarted).Returns(appStarted.Token);

        return new Harness
        {
            Options = options,
            Factory = new TestDbContextFactory(options),
            Lifetime = lifetime,
            AppStarted = appStarted,
            Clock = new FakeTimeProvider(now ?? MarchRunTime),
        };
    }

    private SponsorStatisticsRollupJob CreateJob(Harness h, TimeProvider? clock = null)
        => new(lf, h.Factory, h.Lifetime.Object, clock ?? h.Clock);

    private static Sponsor NewSponsor(int id, string name = "Acme") => new()
    {
        Id = id,
        Name = name,
        ImageUrl = $"https://cdn.test/{id}.png",
        TargetUrl = "https://acme.test",
    };

    private static SponsorTelemetryLog Log(int sponsorId, string eventType, DateTime timestamp,
        string eventId = "1", string source = "web", int? durationMs = null) => new()
        {
            ImageId = sponsorId.ToString(),
            EventType = eventType,
            Timestamp = timestamp,
            EventId = eventId,
            Source = source,
            DurationMs = durationMs,
        };

    private static DateTime InFeb(int day = 15, int hour = 12) => new(2026, 2, day, hour, 0, 0, DateTimeKind.Utc);

    #region Aggregation arithmetic

    /// <summary>
    /// Each event type maps to its own counter and EngagementDuration accumulates DurationMs,
    /// treating a null duration as zero.
    /// </summary>
    [TestMethod]
    public async Task Rollup_SumsEachEventTypeIntoItsOwnCounter()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorTelemetryLogs.AddRange(
                Log(7, IMPRESSION, InFeb()),
                Log(7, IMPRESSION, InFeb()),
                Log(7, IMPRESSION, InFeb()),
                Log(7, VIEWABLE_IMPRESSION, InFeb()),
                Log(7, VIEWABLE_IMPRESSION, InFeb()),
                Log(7, CLICK_THROUGH, InFeb()),
                Log(7, ENGAGEMENT_DURATION, InFeb(), durationMs: 1500),
                Log(7, ENGAGEMENT_DURATION, InFeb(), durationMs: 2500),
                // A duration event with no duration recorded must contribute zero, not blow up.
                Log(7, ENGAGEMENT_DURATION, InFeb(), durationMs: null));
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        var stats = await db.SponsorStatistics.SingleAsync();
        Assert.AreEqual(February2026, stats.Month);
        Assert.AreEqual(7, stats.SponsorId);
        Assert.AreEqual(3, stats.Impressions);
        Assert.AreEqual(2, stats.ViewableImpressions);
        Assert.AreEqual(1, stats.ClickThroughs);
        Assert.AreEqual(4000L, stats.EngagementDurationMs);
        Assert.IsFalse(stats.ReportProcessed, "New statistics must be left for the report job to pick up.");
    }

    /// <summary>
    /// An unrecognized event type is counted in neither bucket, but the sponsor still gets a
    /// (zeroed) statistics row because the row matched a known sponsor.
    /// </summary>
    [TestMethod]
    public async Task Rollup_UnknownEventType_ContributesNothingButStillCreatesRow()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorTelemetryLogs.AddRange(
                Log(7, "SomethingElse", InFeb(), durationMs: 9999),
                Log(7, "impression", InFeb()));  // wrong case - the match is ordinal/case sensitive
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        var stats = await db.SponsorStatistics.SingleAsync();
        Assert.AreEqual(0, stats.Impressions);
        Assert.AreEqual(0, stats.ViewableImpressions);
        Assert.AreEqual(0, stats.ClickThroughs);
        Assert.AreEqual(0L, stats.EngagementDurationMs);
    }

    /// <summary>Telemetry for different sponsors must not bleed into one another.</summary>
    [TestMethod]
    public async Task Rollup_KeepsSponsorsSeparate()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.AddRange(NewSponsor(1, "One"), NewSponsor(2, "Two"));
            seed.SponsorTelemetryLogs.AddRange(
                Log(1, IMPRESSION, InFeb()),
                Log(1, IMPRESSION, InFeb()),
                Log(2, IMPRESSION, InFeb()),
                Log(2, CLICK_THROUGH, InFeb()));
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        var stats = await db.SponsorStatistics.OrderBy(s => s.SponsorId).ToListAsync();
        Assert.AreEqual(2, stats.Count);
        Assert.AreEqual(2, stats[0].Impressions);
        Assert.AreEqual(0, stats[0].ClickThroughs);
        Assert.AreEqual(1, stats[1].Impressions);
        Assert.AreEqual(1, stats[1].ClickThroughs);
    }

    #endregion

    #region Grouping

    /// <summary>
    /// Per-event rows are keyed by the parsed EventId. Blank, non-numeric, zero and negative
    /// EventId values all collapse into the single "no event" bucket (0).
    /// </summary>
    [TestMethod]
    public async Task Rollup_GroupsByEventId_CollapsingUnparsableValuesIntoZero()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorTelemetryLogs.AddRange(
                Log(7, IMPRESSION, InFeb(), eventId: "42"),
                Log(7, CLICK_THROUGH, InFeb(), eventId: "42"),
                Log(7, IMPRESSION, InFeb(), eventId: "99"),
                Log(7, IMPRESSION, InFeb(), eventId: ""),
                Log(7, IMPRESSION, InFeb(), eventId: "   "),
                Log(7, IMPRESSION, InFeb(), eventId: "abc"),
                Log(7, IMPRESSION, InFeb(), eventId: "0"),
                Log(7, IMPRESSION, InFeb(), eventId: "-3"));
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        var stats = await db.SponsorStatistics.Include(s => s.EventStatistics).SingleAsync();
        var byEvent = stats.EventStatistics.ToDictionary(e => e.EventId);
        CollectionAssert.AreEquivalent(new[] { 0, 42, 99 }, byEvent.Keys.ToArray());
        Assert.AreEqual(1, byEvent[42].Impressions);
        Assert.AreEqual(1, byEvent[42].ClickThroughs);
        Assert.AreEqual(1, byEvent[99].Impressions);
        Assert.AreEqual(5, byEvent[0].Impressions, "Blank, whitespace, non-numeric, zero and negative event ids share one bucket.");
        // Per-event totals must add up to the sponsor total.
        Assert.AreEqual(stats.Impressions, stats.EventStatistics.Sum(e => e.Impressions));
    }

    /// <summary>
    /// Per-source rows are keyed by the raw source string using an ordinal comparison, so
    /// sources that differ only by case are reported separately.
    /// </summary>
    [TestMethod]
    public async Task Rollup_GroupsBySource_OrdinalCaseSensitive()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorTelemetryLogs.AddRange(
                Log(7, IMPRESSION, InFeb(), source: "web"),
                Log(7, IMPRESSION, InFeb(), source: "web"),
                Log(7, ENGAGEMENT_DURATION, InFeb(), source: "web", durationMs: 100),
                Log(7, IMPRESSION, InFeb(), source: "mobile"),
                Log(7, IMPRESSION, InFeb(), source: "Web"));
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        var stats = await db.SponsorStatistics.Include(s => s.SourceStatistics).SingleAsync();
        var bySource = stats.SourceStatistics.ToDictionary(s => s.Source);
        CollectionAssert.AreEquivalent(new[] { "web", "mobile", "Web" }, bySource.Keys.ToArray());
        Assert.AreEqual(2, bySource["web"].Impressions);
        Assert.AreEqual(100L, bySource["web"].EngagementDurationMs);
        Assert.AreEqual(1, bySource["mobile"].Impressions);
        Assert.AreEqual(1, bySource["Web"].Impressions);
        Assert.AreEqual(stats.Impressions, stats.SourceStatistics.Sum(s => s.Impressions));
    }

    #endregion

    #region Date window

    /// <summary>
    /// The window is [first instant of the previous month, first instant of this month).
    /// Only the two rows inside February may be counted.
    /// </summary>
    [TestMethod]
    public async Task Rollup_WindowIsPreviousMonth_InclusiveStartExclusiveEnd()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorTelemetryLogs.AddRange(
                // 1 ms before the window - excluded
                Log(7, IMPRESSION, new DateTime(2026, 1, 31, 23, 59, 59, 999, DateTimeKind.Utc)),
                // exactly the first instant of February - included
                Log(7, IMPRESSION, new DateTime(2026, 2, 1, 0, 0, 0, 0, DateTimeKind.Utc)),
                // last instant of February - included
                Log(7, IMPRESSION, new DateTime(2026, 2, 28, 23, 59, 59, 999, DateTimeKind.Utc)),
                // exactly the first instant of March - excluded
                Log(7, IMPRESSION, new DateTime(2026, 3, 1, 0, 0, 0, 0, DateTimeKind.Utc)));
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        var stats = await db.SponsorStatistics.SingleAsync();
        Assert.AreEqual(2, stats.Impressions);
        Assert.AreEqual(February2026, stats.Month);
    }

    /// <summary>A January run must roll up the previous December, rolling the year back.</summary>
    [TestMethod]
    public async Task Rollup_JanuaryRun_RollsUpPreviousDecember()
    {
        var h = CreateHarness(new DateTimeOffset(2026, 1, 5, 3, 0, 0, TimeSpan.Zero));
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorTelemetryLogs.AddRange(
                Log(7, IMPRESSION, new DateTime(2025, 12, 20, 8, 0, 0, DateTimeKind.Utc)),
                Log(7, IMPRESSION, new DateTime(2025, 11, 30, 23, 0, 0, DateTimeKind.Utc)),
                Log(7, IMPRESSION, new DateTime(2026, 1, 2, 1, 0, 0, DateTimeKind.Utc)));
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        var stats = await db.SponsorStatistics.SingleAsync();
        Assert.AreEqual(new DateOnly(2025, 12, 1), stats.Month);
        Assert.AreEqual(1, stats.Impressions);
    }

    /// <summary>
    /// Running on the 31st must still select the previous calendar month, not skip back two
    /// months when the previous month is shorter (DateTime.AddMonths clamps the day).
    /// </summary>
    [TestMethod]
    public async Task Rollup_RunOnMonthEnd_SelectsPreviousCalendarMonth()
    {
        var h = CreateHarness(new DateTimeOffset(2026, 3, 31, 23, 0, 0, TimeSpan.Zero));
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorTelemetryLogs.Add(Log(7, IMPRESSION, InFeb(28)));
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        var stats = await db.SponsorStatistics.SingleAsync();
        Assert.AreEqual(February2026, stats.Month);
        Assert.AreEqual(1, stats.Impressions);
    }

    #endregion

    #region Empty / unmatched input

    /// <summary>
    /// With no rows in the window the job writes nothing and stops the host, even though
    /// telemetry exists for other months.
    /// </summary>
    [TestMethod]
    public async Task Rollup_NoTelemetryInWindow_WritesNothingAndStopsHost()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorTelemetryLogs.Add(Log(7, IMPRESSION, new DateTime(2026, 3, 5, 1, 0, 0, DateTimeKind.Utc)));
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        Assert.AreEqual(0, await db.SponsorStatistics.CountAsync());
        // Early-exit paths stop the host explicitly and again from the finally block; the real
        // lifetime treats that as idempotent, so only "at least once" is contractual.
        h.Lifetime.Verify(l => l.StopApplication(), Times.AtLeastOnce);
    }

    /// <summary>
    /// Telemetry whose ImageId is not a known sponsor id is dropped. When nothing at all
    /// matches, no statistics are written.
    /// </summary>
    [TestMethod]
    public async Task Rollup_AllRowsUnmatched_WritesNothingAndStopsHost()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorTelemetryLogs.AddRange(
                // Non-numeric image id
                new SponsorTelemetryLog { ImageId = "banner-a", EventType = IMPRESSION, Timestamp = InFeb(), EventId = "1", Source = "web" },
                // Numeric but no such sponsor
                Log(999, IMPRESSION, InFeb()));
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        Assert.AreEqual(0, await db.SponsorStatistics.CountAsync());
        h.Lifetime.Verify(l => l.StopApplication(), Times.AtLeastOnce);
    }

    /// <summary>Unmatched rows are skipped without affecting the sponsors that do match.</summary>
    [TestMethod]
    public async Task Rollup_UnmatchedRowsMixedWithMatched_SkipsOnlyUnmatched()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorTelemetryLogs.AddRange(
                Log(7, IMPRESSION, InFeb()),
                Log(999, IMPRESSION, InFeb()),
                new SponsorTelemetryLog { ImageId = "", EventType = IMPRESSION, Timestamp = InFeb(), EventId = "1", Source = "web" },
                Log(7, IMPRESSION, InFeb()));
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        var stats = await db.SponsorStatistics.SingleAsync();
        Assert.AreEqual(7, stats.SponsorId);
        Assert.AreEqual(2, stats.Impressions);
    }

    #endregion

    #region Re-run safety

    /// <summary>
    /// Re-running the job for the same month replaces the previous result instead of adding
    /// to it: one statistics row per sponsor/month, child rows are not duplicated, totals are
    /// not doubled, and the report flags are reset so the refreshed numbers get re-sent.
    /// </summary>
    [TestMethod]
    public async Task Rollup_SecondRunForSameMonth_OverwritesInsteadOfDoubleCounting()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorTelemetryLogs.AddRange(
                Log(7, IMPRESSION, InFeb(), eventId: "42", source: "web"),
                Log(7, IMPRESSION, InFeb(), eventId: "42", source: "web"),
                Log(7, ENGAGEMENT_DURATION, InFeb(), eventId: "42", source: "web", durationMs: 750));
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        // Simulate the report job having already processed the first result.
        await using (var mark = h.NewContext())
        {
            var s = await mark.SponsorStatistics.SingleAsync();
            s.ReportProcessed = true;
            s.ReportProcessingSuccessful = true;
            await mark.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        var stats = await db.SponsorStatistics
            .Include(s => s.EventStatistics)
            .Include(s => s.SourceStatistics)
            .SingleAsync();
        Assert.AreEqual(2, stats.Impressions, "Totals must be replaced, not accumulated.");
        Assert.AreEqual(750L, stats.EngagementDurationMs);
        Assert.AreEqual(1, stats.EventStatistics.Count, "Old per-event rows must be removed before the new ones are added.");
        Assert.AreEqual(2, stats.EventStatistics[0].Impressions);
        Assert.AreEqual(1, stats.SourceStatistics.Count);
        Assert.AreEqual(2, stats.SourceStatistics[0].Impressions);
        Assert.IsFalse(stats.ReportProcessed, "Refreshed statistics must be queued for a new report.");
        Assert.IsFalse(stats.ReportProcessingSuccessful);
        Assert.AreEqual(1, await db.EventSponsorStatistics.CountAsync(), "Orphaned per-event rows must not be left behind.");
        Assert.AreEqual(1, await db.SourceSponsorStatistics.CountAsync());
    }

    /// <summary>
    /// The overwrite is scoped to the month being processed - statistics already stored for a
    /// different month must be left completely alone.
    /// </summary>
    [TestMethod]
    public async Task Rollup_LeavesOtherMonthsUntouched()
    {
        var h = CreateHarness();
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorStatistics.Add(new SponsorStatistics
            {
                Month = new DateOnly(2026, 1, 1),
                SponsorId = 7,
                Impressions = 500,
                ReportProcessed = true,
                ReportProcessingSuccessful = true,
                EventStatistics = [new EventSponsorStatistics { SponsorId = 7, EventId = 3, Impressions = 500 }],
            });
            seed.SponsorTelemetryLogs.Add(Log(7, IMPRESSION, InFeb()));
            await seed.SaveChangesAsync();
        }

        await CreateJob(h).RunRollupAsync(CancellationToken.None);

        await using var db = h.NewContext();
        var january = await db.SponsorStatistics.Include(s => s.EventStatistics)
            .SingleAsync(s => s.Month == new DateOnly(2026, 1, 1));
        Assert.AreEqual(500, january.Impressions);
        Assert.IsTrue(january.ReportProcessed);
        Assert.AreEqual(1, january.EventStatistics.Count);

        var february = await db.SponsorStatistics.SingleAsync(s => s.Month == February2026);
        Assert.AreEqual(1, february.Impressions);
    }

    #endregion

    #region Host lifecycle and failure

    /// <summary>
    /// The full hosted-service path: the job does no work until the host signals that it has
    /// started, then rolls up and stops the host.
    /// </summary>
    [TestMethod]
    // The only thing that completes ExecuteTask is the ApplicationStarted token; if that setup ever
    // stops matching, the loose mock hands back a token that never cancels and the await never returns.
    [Timeout(30_000)]
    public async Task ExecuteAsync_WaitsForApplicationStartedThenRollsUpAndStopsHost()
    {
        var h = CreateHarness(applicationAlreadyStarted: false);
        await using (var seed = h.NewContext())
        {
            seed.Sponsors.Add(NewSponsor(7));
            seed.SponsorTelemetryLogs.Add(Log(7, IMPRESSION, InFeb()));
            await seed.SaveChangesAsync();
        }

        var job = CreateJob(h, new ImmediateTimerTimeProvider(MarchRunTime));
        await job.StartAsync(CancellationToken.None);
        Assert.IsNotNull(job.ExecuteTask);

        // Startup has not been signaled, so the job cannot have written anything yet.
        await using (var parked = h.NewContext())
        {
            Assert.AreEqual(0, await parked.SponsorStatistics.CountAsync());
        }

        await h.AppStarted.CancelAsync();
        await job.ExecuteTask;

        await using var db = h.NewContext();
        Assert.AreEqual(1, await db.SponsorStatistics.CountAsync());
        h.Lifetime.Verify(l => l.StopApplication(), Times.AtLeastOnce);
    }

    /// <summary>
    /// When the rollup fails the exception is propagated (so the pod exits non-zero) but the
    /// host is still stopped so the run does not hang.
    /// </summary>
    [TestMethod]
    public async Task RunRollup_WhenCanceled_PropagatesAndStillStopsHost()
    {
        var h = CreateHarness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateJob(h).RunRollupAsync(cts.Token));

        h.Lifetime.Verify(l => l.StopApplication(), Times.Once);
        await using var db = h.NewContext();
        Assert.AreEqual(0, await db.SponsorStatistics.CountAsync());
    }

    #endregion
}
