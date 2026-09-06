using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.SessionMonitoring;
using RedMist.EventProcessor.EventStatus.SessionMonitoring.Metrics;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventProcessor.Tests.EventStatus.SessionMonitoring;

/// <summary>
/// The derived race metrics as they reach the results row: which of them are written when a session
/// is finalized, what happens when the lap log has not caught up yet, and that a result being
/// rewritten does not lose what an earlier write established.
/// </summary>
[TestClass]
public class SessionMonitorMetricsTests
{
    private const int EventId = 1;
    private const int SessionId = 36;

    private static readonly DateTime Start = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    public TestContext TestContext { get; set; } = null!;

    #region What gets written

    /// <summary>
    /// The metrics that come out of the state alone go on as the session is written out. Reading the
    /// lap log does not: the write can happen on the pipeline thread with the session write lock
    /// held, so the lap metrics wait for the finish check's own loop.
    /// </summary>
    [TestMethod]
    public async Task PersistFinishedSession_WritesTheMetricsThatNeedNoLapLog()
    {
        var harness = await CreateHarnessAsync(distance: "2.50");
        await harness.LogLapsAsync(LeaderLaps("54", 20, "1"));

        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState()));

        await using var db = harness.CreateDb();
        var state = db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;

        Assert.AreEqual(1, state.NumberOfYellows);
        Assert.AreEqual(1_200_000, state.GreenTimeMs);
        Assert.AreEqual(300_000, state.YellowTimeMs);
        Assert.AreEqual(0, state.RedTimeMs);
        Assert.AreEqual("100.00", state.AverageRaceSpeed, "20 laps of 2.5 miles in 30 minutes.");
        Assert.IsNull(state.LeadChanges, "The lap log is not read on this path.");
        Assert.IsNull(state.CarPositions.Single(c => c.Number == "54").LapsLedOverall);
    }

    /// <summary>
    /// The finish check runs every five seconds and completes what the write left out, so a session
    /// whose lap log is already there gets its full set on the very next pass.
    /// </summary>
    [TestMethod]
    public async Task RunCheckForFinished_WritesTheMetricsNoMultiloopFeedSupplied()
    {
        var harness = await CreateHarnessAsync(distance: "2.50");
        await harness.LogLapsAsync(LeaderLaps("54", 20, "1"));

        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState()));
        await harness.Monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        await using var db = harness.CreateDb();
        var state = db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;

        Assert.AreEqual(1, state.NumberOfYellows);
        Assert.AreEqual(0, state.LeadChanges, "One car led throughout.");
        Assert.AreEqual(20, state.GreenLaps);
        Assert.AreEqual("100.00", state.AverageRaceSpeed);
        Assert.AreEqual(20, state.CarPositions.Single(c => c.Number == "54").LapsLedOverall);
        Assert.AreEqual(20, state.CarPositions.Single(c => c.Number == "54").LapsLedInClass);
        Assert.AreEqual(0, state.CarPositions.Single(c => c.Number == "1").LapsLedOverall);
    }

    /// <summary>
    /// The lap distance is the organizer's, entered on the event, and it is the only source the
    /// average speed will accept.
    /// </summary>
    [TestMethod]
    public async Task PersistFinishedSession_WithNoDistanceOnTheEvent_WritesEverythingButTheSpeed()
    {
        var harness = await CreateHarnessAsync(distance: string.Empty);
        await harness.LogLapsAsync(LeaderLaps("54", 20, "1"));

        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState()));
        await harness.Monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        await using var db = harness.CreateDb();
        var state = db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;

        Assert.IsNull(state.AverageRaceSpeed);
        Assert.AreEqual(1, state.NumberOfYellows);
        Assert.AreEqual(20, state.GreenLaps);
    }

    /// <summary>
    /// A session with cars but no laps is common - a practice run nobody completed a lap in - and
    /// has to be written out like any other.
    /// </summary>
    [TestMethod]
    public async Task PersistFinishedSession_ForASessionWithNoLaps_StillWritesTheResults()
    {
        var harness = await CreateHarnessAsync(distance: "2.50");
        var state = RaceState();
        foreach (var car in state.CarPositions)
            car.LastLapCompleted = 0;

        Assert.IsTrue(harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, state)));

        await using var db = harness.CreateDb();
        var saved = db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;
        Assert.AreEqual(1, saved.NumberOfYellows);
        Assert.IsNull(saved.LeadChanges, "There were no laps to find a leader in.");
        Assert.IsNull(saved.AverageRaceSpeed);
    }

    #endregion

    #region Waiting for the lap log

    /// <summary>
    /// The lap log is written by a different service reading a Redis stream, so its tail is
    /// routinely still in flight when the session is finalized. The results still have to be saved -
    /// they are the session's only permanent record - but the numbers that need the whole race are
    /// left for later rather than being written short.
    /// </summary>
    [TestMethod]
    public async Task PersistFinishedSession_WhenTheLapLogIsStillShort_SavesTheResultsWithoutTheLapMetrics()
    {
        var harness = await CreateHarnessAsync(distance: "2.50");
        await harness.LogLapsAsync(LeaderLaps("54", 18, "1"));

        Assert.IsTrue(harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState())));

        await using var db = harness.CreateDb();
        var state = db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;

        Assert.IsNull(state.LeadChanges, "The log stopped two laps short of the finish.");
        Assert.IsNull(state.GreenLaps);
        Assert.IsNull(state.CarPositions.Single(c => c.Number == "54").LapsLedOverall);
        Assert.AreEqual(1, state.NumberOfYellows, "The metrics that do not need the lap log are written anyway.");
        Assert.AreEqual("100.00", state.AverageRaceSpeed);
    }

    /// <summary>
    /// Once the rest of the laps land, a later pass of the finish check fills in what the first
    /// write had to leave out.
    /// </summary>
    [TestMethod]
    public async Task RunCheckForFinished_FillsInTheLapMetricsOnceTheLogCatchesUp()
    {
        var harness = await CreateHarnessAsync(distance: "2.50");
        await harness.LogLapsAsync(LeaderLaps("54", 18, "1"));
        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState()));

        await harness.LogLapsAsync(LeaderLaps("54", 20, "1").Where(l => l.LapNumber > 18));
        await harness.Monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        await using var db = harness.CreateDb();
        var state = db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;

        Assert.AreEqual(0, state.LeadChanges);
        Assert.AreEqual(20, state.GreenLaps);
        Assert.AreEqual(20, state.CarPositions.Single(c => c.Number == "54").LapsLedOverall);
        Assert.AreEqual(1, state.NumberOfYellows, "What the first write established is still there.");
    }

    /// <summary>
    /// A session whose log never catches up must not keep being queried for the life of the event,
    /// and must never end up with a number derived from a partial log.
    /// </summary>
    [TestMethod]
    public async Task RunCheckForFinished_WhileTheLogIsStillShort_LeavesTheLapMetricsNull()
    {
        var harness = await CreateHarnessAsync(distance: "2.50");
        await harness.LogLapsAsync(LeaderLaps("54", 18, "1"));
        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState()));

        await harness.Monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        await using var db = harness.CreateDb();
        var state = db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;

        Assert.IsNull(state.LeadChanges);
        Assert.IsNull(state.CarPositions.Single(c => c.Number == "54").LapsLedOverall);
    }

    #endregion

    #region Results being rewritten

    /// <summary>
    /// A session can be finalized more than once, and the second write replaces the state when it
    /// carries more data. The replacement is a fresh snapshot with no metrics on it, so anything the
    /// first write established has to survive the swap rather than being reset to null.
    /// </summary>
    [TestMethod]
    public async Task PersistFinishedSession_RewritingAResult_KeepsTheMetricsTheFirstWriteEstablished()
    {
        var harness = await CreateHarnessAsync(distance: "2.50");
        await harness.LogLapsAsync(LeaderLaps("54", 20, "1"));
        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState()));
        await harness.Monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        // A later snapshot with an extra car and a leader the lap log has not reached, which is what
        // stops the lap metrics being worked out a second time.
        var later = RaceState();
        later.CarPositions.Single(c => c.Number == "54").LastLapCompleted = 25;
        later.CarPositions.Add(new CarPosition { Number = "99", Class = "GTU", OverallPosition = 3, LastLapCompleted = 15 });

        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, later));
        await harness.Monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        await using var db = harness.CreateDb();
        var state = db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;

        Assert.HasCount(3, state.CarPositions, "The newer snapshot is the one that was kept.");
        Assert.AreEqual(0, state.LeadChanges, "The lead changes the first pass found are still there.");
        Assert.AreEqual(20, state.GreenLaps);
        Assert.AreEqual(20, state.CarPositions.Single(c => c.Number == "54").LapsLedOverall);
        Assert.AreEqual(0, state.CarPositions.Single(c => c.Number == "99").LapsLedOverall,
            "Laps led stay a complete set: a car the earlier pass never counted as leading led none.");
    }

    /// <summary>
    /// Laps led have to be all present or all absent. A field where one car is null and the rest
    /// carry numbers reads as partly supplied by a timing feed, which is exactly what would stop the
    /// missing one ever being worked out.
    /// </summary>
    [TestMethod]
    public async Task PersistFinishedSession_RewritingAResult_LeavesLapsLedAsACompleteSet()
    {
        var harness = await CreateHarnessAsync(distance: "2.50");
        await harness.LogLapsAsync(LeaderLaps("54", 20, "1"));
        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState()));
        await harness.Monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        var later = RaceState();
        later.CarPositions.Single(c => c.Number == "54").LastLapCompleted = 25;
        later.CarPositions.Add(new CarPosition { Number = "99", Class = "GTU", OverallPosition = 3, LastLapCompleted = 15 });
        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, later));

        await using var db = harness.CreateDb();
        var state = db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;

        Assert.IsEmpty(state.CarPositions.Where(c => c.LapsLedOverall is null), "Every car carries a value.");
        Assert.IsEmpty(state.CarPositions.Where(c => c.LapsLedInClass is null));
    }

    /// <summary>
    /// A session whose lap log never covers it has to stop being queried, and has to be left with
    /// nulls rather than numbers read off a partial log.
    /// </summary>
    [TestMethod]
    public async Task RunCheckForFinished_AfterTheRetryWindowPasses_StopsTryingAndLeavesTheMetricsNull()
    {
        var harness = await CreateHarnessAsync(distance: "2.50");
        await harness.LogLapsAsync(LeaderLaps("54", 18, "1"));
        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState()));

        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        // The laps arrive after the session was given up on, so a further pass must not take it up.
        await harness.LogLapsAsync(LeaderLaps("54", 20, "1").Where(l => l.LapNumber > 18));
        await harness.Monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        await using var db = harness.CreateDb();
        var state = db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;

        Assert.IsNull(state.LeadChanges);
        Assert.IsNull(state.CarPositions.Single(c => c.Number == "54").LapsLedOverall);
    }

    /// <summary>
    /// A pass that fails has to leave the session queued rather than spending its one chance.
    /// </summary>
    [TestMethod]
    public async Task RunCheckForFinished_WhenAPassThrows_TriesAgainOnTheNext()
    {
        var deriver = new FlakyDeriver();
        var harness = await CreateHarnessAsync(distance: "2.50", deriver: deriver);
        await harness.LogLapsAsync(LeaderLaps("54", 20, "1"));
        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState()));

        deriver.ThrowOnLapMetrics = true;
        await harness.Monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        deriver.ThrowOnLapMetrics = false;
        await harness.Monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        await using var db = harness.CreateDb();
        var state = db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;

        Assert.AreEqual(0, state.LeadChanges, "The session was still queued after the pass that threw.");
        Assert.AreEqual(20, state.CarPositions.Single(c => c.Number == "54").LapsLedOverall);
    }

    /// <summary>
    /// Running twice must add nothing the first run did not, or a session finalized repeatedly would
    /// drift.
    /// </summary>
    [TestMethod]
    public async Task PersistFinishedSession_RunTwiceOverTheSameSession_ProducesTheSameNumbers()
    {
        var harness = await CreateHarnessAsync(distance: "2.50");
        await harness.LogLapsAsync(LeaderLaps("54", 20, "1"));

        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState()));
        SessionState first;
        await using (var db = harness.CreateDb())
            first = db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;

        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState()));

        await using var after = harness.CreateDb();
        var second = after.SessionResults.Single(r => r.SessionId == SessionId).SessionState!;

        Assert.AreEqual(first.LeadChanges, second.LeadChanges);
        Assert.AreEqual(first.NumberOfYellows, second.NumberOfYellows);
        Assert.AreEqual(first.GreenLaps, second.GreenLaps);
        Assert.AreEqual(first.AverageRaceSpeed, second.AverageRaceSpeed);
        Assert.AreEqual(first.CarPositions.Single(c => c.Number == "54").LapsLedOverall,
            second.CarPositions.Single(c => c.Number == "54").LapsLedOverall);
    }

    /// <summary>
    /// The derivation is a decoration on the results. A session that ended is written out whether or
    /// not its metrics could be worked out.
    /// </summary>
    [TestMethod]
    public async Task PersistFinishedSession_WhenTheDerivationThrows_StillSavesTheResults()
    {
        var harness = await CreateHarnessAsync(distance: "2.50", deriver: new ThrowingDeriver());

        Assert.IsTrue(harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, RaceState())));

        await using var db = harness.CreateDb();
        var result = db.SessionResults.Single(r => r.SessionId == SessionId);
        Assert.HasCount(2, result.SessionState!.CarPositions);
        Assert.IsNull(result.SessionState.NumberOfYellows);
    }

    #endregion

    #region Helpers

    /// <summary>A deriver that can be made to throw for one pass.</summary>
    private sealed class FlakyDeriver : ISessionMetricsDeriver
    {
        private readonly SessionMetricsDeriver inner = new();

        public bool ThrowOnLapMetrics { get; set; }

        public void ApplyFlagMetrics(SessionState state, string? eventLapDistance)
            => inner.ApplyFlagMetrics(state, eventLapDistance);

        public bool TryApplyLapMetrics(SessionState state, ISessionLapLog lapLog)
            => ThrowOnLapMetrics ? throw new InvalidOperationException("boom") : inner.TryApplyLapMetrics(state, lapLog);
    }

    private sealed class ThrowingDeriver : ISessionMetricsDeriver
    {
        public void ApplyFlagMetrics(SessionState state, string? eventLapDistance) => throw new InvalidOperationException("boom");

        public bool TryApplyLapMetrics(SessionState state, ISessionLapLog lapLog) => throw new InvalidOperationException("boom");
    }

    /// <summary>
    /// A twenty minute green, five minutes of caution and ten minutes of green, with the checkered
    /// flag still open the way a finished session always leaves it.
    /// </summary>
    private static SessionState RaceState() => new()
    {
        EventId = EventId,
        SessionId = SessionId,
        RunningRaceTime = "00:30:00",
        CarPositions =
        [
            new CarPosition { Number = "54", Class = "GTO", OverallPosition = 1, ClassPosition = 1, LastLapCompleted = 20 },
            new CarPosition { Number = "1", Class = "GTO", OverallPosition = 2, ClassPosition = 2, LastLapCompleted = 19 },
        ],
        FlagDurations =
        [
            new FlagDuration { Flag = Flags.Green, StartTime = Start, EndTime = Start.AddSeconds(1200) },
            new FlagDuration { Flag = Flags.Yellow, StartTime = Start.AddSeconds(1200), EndTime = Start.AddSeconds(1500) },
            new FlagDuration { Flag = Flags.Checkered, StartTime = Start.AddSeconds(1800), EndTime = null },
        ],
    };

    /// <summary>One car leading every lap of a two car race, as the lap log would have recorded it.</summary>
    private static IEnumerable<CarLapLog> LeaderLaps(string leader, int laps, string follower)
    {
        for (var lap = 1; lap <= laps; lap++)
        {
            yield return LapRow(leader, lap, lap * 60, overall: 1, inClass: 1);
            yield return LapRow(follower, lap, lap * 60 + 2, overall: 2, inClass: 2);
        }
    }

    private static CarLapLog LapRow(string car, int lap, int atSeconds, int overall, int inClass) => new()
    {
        EventId = EventId,
        SessionId = SessionId,
        CarNumber = car,
        LapNumber = lap,
        Timestamp = Start.AddSeconds(atSeconds),
        Flag = (int)Flags.Green,
        LapData = JsonSerializer.Serialize(new CarPosition
        {
            Number = car,
            Class = "GTO",
            OverallPosition = overall,
            ClassPosition = inClass,
            LastLapCompleted = lap,
        }),
    };

    private sealed class MetricsHarness
    {
        public required MetricsSessionMonitor Monitor { get; init; }
        public required Func<TsContext> CreateDb { get; init; }
        public required FakeTimeProvider Clock { get; init; }

        public async Task LogLapsAsync(IEnumerable<CarLapLog> laps)
        {
            await using var db = CreateDb();
            db.CarLapLogs.AddRange(laps);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>A monitor whose result writing is the real one, over an in-memory database.</summary>
    private sealed class MetricsSessionMonitor(IConfiguration configuration, IDbContextFactory<TsContext> tsContext,
        SessionContext sessionContext, IConnectionMultiplexer cacheMux, ISessionMetricsDeriver metricsDeriver,
        TimeProvider timeProvider)
        : SessionMonitor(configuration, tsContext, new DebugLoggerFactory(), sessionContext, cacheMux, metricsDeriver,
            timeProvider)
    {
        public bool CallPersist(FinishedSession finished) => PersistFinishedSession(finished);

        protected override Task SetSessionAsLiveAsync(int eventId, int sessionId) => Task.CompletedTask;

        protected override Task SaveLastUpdatedTimestampAsync(int eventId, int sessionId, CancellationToken stoppingToken = default)
            => Task.CompletedTask;
    }

    private async Task<MetricsHarness> CreateHarnessAsync(string distance, ISessionMetricsDeriver? deriver = null)
    {
        var clock = new FakeTimeProvider(Start);
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        var factory = new TestDbContextFactory(optionsBuilder.Options);

        await using (var seed = factory.CreateDbContext())
        {
            seed.Events.Add(new TimingCommon.Models.Configuration.Event { Id = EventId, Name = "Test", Distance = distance });
            seed.Sessions.Add(new Session
            {
                Id = SessionId,
                EventId = EventId,
                Name = "Race",
                StartTime = Start,
                IsLive = true,
            });
            await seed.SaveChangesAsync(TestContext.CancellationToken);
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", EventId.ToString() } })
            .Build();
        var sessionContext = new SessionContext(configuration, factory, new DebugLoggerFactory(),
            new Mock<ICarLapHistoryService>().Object);

        var database = new Mock<IDatabase>();
        database.Setup(x => x.StringGet(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).Returns(RedisValue.Null);
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);

        return new MetricsHarness
        {
            Monitor = new MetricsSessionMonitor(configuration, factory, sessionContext, mux.Object,
                deriver ?? new SessionMetricsDeriver(), clock),
            CreateDb = factory.CreateDbContext,
            Clock = clock,
        };
    }

    #endregion
}
