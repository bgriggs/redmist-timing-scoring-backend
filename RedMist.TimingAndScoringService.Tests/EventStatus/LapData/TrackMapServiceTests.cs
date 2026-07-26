using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.LapData;

[TestClass]
public class TrackMapServiceTests
{
    private const int EventId = 1;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private SessionContext _sessionContext = null!;
    private Mock<IConnectionMultiplexer> _redis = null!;
    private TrackMapService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", EventId.ToString() } })
            .Build();

        _dbContextFactory = CreateDbContextFactory();
        var timeProvider = new FakeTimeProvider();
        var lapHistory = new InMemoryCarLapHistoryService(null!);
        _sessionContext = new SessionContext(configuration, _dbContextFactory, loggerFactory.Object, lapHistory, timeProvider);
        _sessionContext.SessionState.SessionId = 7;

        _redis = new Mock<IConnectionMultiplexer>();
        _redis.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(new Mock<IDatabase>().Object);

        _service = new TrackMapService(_sessionContext, _dbContextFactory, _redis.Object, loggerFactory.Object, timeProvider);
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"TrackMapTests_{Guid.NewGuid()}")
            .Options;
        return new TestDbContextFactory(options);
    }

    [TestMethod]
    public async Task AddSample_FullLap_BuildsAndExposesMap()
    {
        Assert.IsNull(_service.CurrentMap);

        await CircleTrack.FeedFullLapAsync(_service);

        Assert.IsNotNull(_service.CurrentMap);
        Assert.AreEqual(EventId, _service.CurrentMap.EventId);
        Assert.AreEqual(CircleTrack.Circumference, _service.CurrentMap.TotalLengthMeters, CircleTrack.Circumference * 0.02);
    }

    [TestMethod]
    public async Task AddSample_FullLap_PersistsMapToDatabase()
    {
        await CircleTrack.FeedFullLapAsync(_service);

        await using var db = _dbContextFactory.CreateDbContext();
        var record = db.TrackMaps.FirstOrDefault(t => t.EventId == EventId);
        Assert.IsNotNull(record);
        Assert.IsTrue(record.Map.Points.Count > 1);
        Assert.AreEqual(CircleTrack.Circumference, record.Map.TotalLengthMeters, CircleTrack.Circumference * 0.02);
    }

    [TestMethod]
    public async Task EnsureLoaded_LoadsPersistedMap()
    {
        // Seed a persisted map directly, then load it.
        await using (var db = _dbContextFactory.CreateDbContext())
        {
            db.TrackMaps.Add(new TrackMapRecord
            {
                EventId = EventId,
                UpdatedUtc = DateTime.UnixEpoch,
                Map = new TrackMap
                {
                    EventId = EventId,
                    TotalLengthMeters = 1234.5,
                    Points =
                    [
                        new TrackMapPoint { Latitude = 45.0, Longitude = -75.0, CumulativeDistanceMeters = 0 },
                        new TrackMapPoint { Latitude = 45.001, Longitude = -75.0, CumulativeDistanceMeters = 111 },
                    ],
                },
            });
            await db.SaveChangesAsync();
        }

        Assert.IsNull(_service.CurrentMap);
        await _service.EnsureLoadedAsync();

        Assert.IsNotNull(_service.CurrentMap);
        Assert.AreEqual(1234.5, _service.CurrentMap.TotalLengthMeters, 1e-6);
        Assert.AreEqual(2, _service.CurrentMap.Points.Count);
    }

    [TestMethod]
    public async Task EnsureLoaded_NoPersistedMap_LeavesCurrentMapNull()
    {
        await _service.EnsureLoadedAsync();
        Assert.IsNull(_service.CurrentMap);
    }

    #region Declared track length

    [TestMethod]
    public async Task AddSample_MapMatchingTheDeclaredLength_IsAccepted()
    {
        await _service.SetDeclaredLapLengthAsync(CircleTrack.Circumference);

        await CircleTrack.FeedFullLapAsync(_service);

        Assert.IsNotNull(_service.CurrentMap);
    }

    [TestMethod]
    public async Task AddSample_MapAtOddsWithTheDeclaredLength_IsDiscarded()
    {
        // Laps can agree with each other and still be wrong together - a feed reporting lap counts
        // consistently late gives every buffer two laps. The declared length is the only thing that
        // can tell, and a map the event would otherwise be stuck with is thrown away.
        await _service.SetDeclaredLapLengthAsync(CircleTrack.Circumference / 2);

        await CircleTrack.FeedFullLapAsync(_service);

        Assert.IsNull(_service.CurrentMap);
    }

    [TestMethod]
    public async Task AddSample_DiscardedMap_DoesNotPersist()
    {
        await _service.SetDeclaredLapLengthAsync(CircleTrack.Circumference / 2);

        await CircleTrack.FeedFullLapAsync(_service);

        await using var db = _dbContextFactory.CreateDbContext();
        Assert.IsNull(db.TrackMaps.FirstOrDefault(t => t.EventId == EventId));
    }

    [TestMethod]
    public async Task DeclaredLength_ArrivingAfterAWrongMapWasLoaded_DiscardsIt()
    {
        // A map learned before this check existed is otherwise reloaded and trusted every session
        // for the rest of the event.
        await CircleTrack.FeedFullLapAsync(_service);
        Assert.IsNotNull(_service.CurrentMap);

        await _service.SetDeclaredLapLengthAsync(CircleTrack.Circumference / 2);

        Assert.IsNull(_service.CurrentMap, "A map that cannot be this track must not survive");
    }

    [TestMethod]
    public async Task DeclaredLength_KnownBeforeAWrongStoredMapLoads_DiscardsItOnLoad()
    {
        // The restart case: a map learned before this check existed is in the database, and the
        // declared length is already known by the time it is read back.
        await using (var db = _dbContextFactory.CreateDbContext())
        {
            db.TrackMaps.Add(new TrackMapRecord
            {
                EventId = EventId,
                UpdatedUtc = DateTime.UnixEpoch,
                Map = new TrackMap
                {
                    EventId = EventId,
                    TotalLengthMeters = 10_000,
                    StartFinishOffsetMeters = 1_393,
                    Points =
                    [
                        new TrackMapPoint { Latitude = 45.0, Longitude = -75.0, CumulativeDistanceMeters = 0 },
                        new TrackMapPoint { Latitude = 45.001, Longitude = -75.0, CumulativeDistanceMeters = 111 },
                    ],
                },
            });
            await db.SaveChangesAsync();
        }
        await _service.SetDeclaredLapLengthAsync(4_088);

        await _service.EnsureLoadedAsync();

        Assert.IsNull(_service.CurrentMap);
        Assert.IsFalse(_service.IsStartFinishCalibrated);
    }

    [TestMethod]
    public async Task DeclaredLength_DiscardedMap_IsRemovedFromTheDatabase()
    {
        // Left in place it would be reloaded and re-discarded on every restart, and consumers would
        // keep drawing an outline the server no longer uses.
        await CircleTrack.FeedFullLapAsync(_service);
        await _service.SetDeclaredLapLengthAsync(CircleTrack.Circumference / 2);

        await using var db = _dbContextFactory.CreateDbContext();
        Assert.IsNull(db.TrackMaps.FirstOrDefault(t => t.EventId == EventId));
    }

    [TestMethod]
    public async Task DeclaredLength_MapShorterThanDeclared_IsKept()
    {
        // A track length entered in the wrong units would otherwise destroy a good map and then
        // reject every replacement for the rest of the event. A map reading short is the ordinary
        // result of a polyline cutting corners; only an over-long one indicates a swallowed lap.
        await CircleTrack.FeedFullLapAsync(_service);

        await _service.SetDeclaredLapLengthAsync(CircleTrack.Circumference * 1.61);

        Assert.IsNotNull(_service.CurrentMap);
    }

    [TestMethod]
    public async Task DeclaredLength_SameValueAgain_IsANoOp()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        await _service.SetDeclaredLapLengthAsync(CircleTrack.Circumference);
        var map = _service.CurrentMap;

        await _service.SetDeclaredLapLengthAsync(CircleTrack.Circumference);

        Assert.AreSame(map, _service.CurrentMap);
    }

    [TestMethod]
    public async Task DeclaredLength_NonFiniteOrNegative_IsIgnored()
    {
        await CircleTrack.FeedFullLapAsync(_service);

        await _service.SetDeclaredLapLengthAsync(double.NaN);
        await _service.SetDeclaredLapLengthAsync(double.PositiveInfinity);
        await _service.SetDeclaredLapLengthAsync(-1);

        Assert.IsNotNull(_service.CurrentMap);
        Assert.IsNull(_service.DeclaredLapLengthMeters);
    }

    [TestMethod]
    public async Task DeclaredLength_MatchingTheMapInHand_LeavesItAlone()
    {
        await CircleTrack.FeedFullLapAsync(_service);

        await _service.SetDeclaredLapLengthAsync(CircleTrack.Circumference);

        Assert.IsNotNull(_service.CurrentMap);
    }

    [TestMethod]
    public async Task DeclaredLength_AfterDiscardingAWrongMap_RelearnsFromLaterLaps()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        await _service.SetDeclaredLapLengthAsync(CircleTrack.Circumference / 2);
        Assert.IsNull(_service.CurrentMap);

        // The declared length now matches what the cars are actually driving.
        await _service.SetDeclaredLapLengthAsync(CircleTrack.Circumference);
        await CircleTrack.FeedFullLapAsync(_service);

        Assert.IsNotNull(_service.CurrentMap);
        Assert.AreEqual(CircleTrack.Circumference, _service.CurrentMap.TotalLengthMeters, CircleTrack.Circumference * 0.05);
    }

    [TestMethod]
    public async Task AddSample_NoDeclaredLength_LearnsAsBefore()
    {
        // External-source events have no declared length; the GPS has to stand on its own there.
        Assert.IsNull(_service.DeclaredLapLengthMeters);

        await CircleTrack.FeedFullLapAsync(_service);

        Assert.IsNotNull(_service.CurrentMap);
    }

    [TestMethod]
    public async Task AddSample_ChordCuttingUndershoot_IsStillAccepted()
    {
        // A polyline through sampled fixes cuts every corner, so a learned map reads short against
        // the declared figure. That is expected, not a disagreement.
        await _service.SetDeclaredLapLengthAsync(CircleTrack.Circumference * 1.10);

        await CircleTrack.FeedFullLapAsync(_service);

        Assert.IsNotNull(_service.CurrentMap);
    }

    #endregion

    #region Start/finish calibration

    /// <summary>Feeds crossings clustered around a point a tenth of the way along the path.</summary>
    private async Task<double> AddClusteredObservationsAsync(int count)
    {
        var target = _service.CurrentMap!.TotalLengthMeters * 0.10;
        await AddCrossingsAsync(target, count);
        return target;
    }

    /// <summary>
    /// Feeds crossings clustered around a point, spread over enough cars to satisfy the rule that a
    /// line is only moved on evidence from more than one of them.
    /// </summary>
    private async Task AddCrossingsAsync(double around, int count, int cars = 5)
    {
        for (int i = 0; i < count; i++)
            await _service.AddStartFinishObservationAsync($"car{i % cars}", around + (i % 5) * 3.0);
    }

    [TestMethod]
    public async Task Calibration_NewMap_StartsUncalibrated()
    {
        await CircleTrack.FeedFullLapAsync(_service);

        Assert.IsFalse(_service.IsStartFinishCalibrated);
        Assert.IsNull(_service.CurrentMap!.StartFinishOffsetMeters);
    }

    [TestMethod]
    public async Task Calibration_EnoughAgreeingCrossings_SetsTheOffset()
    {
        await CircleTrack.FeedFullLapAsync(_service);

        var target = await AddClusteredObservationsAsync(5);

        Assert.IsTrue(_service.IsStartFinishCalibrated);
        Assert.AreEqual(target, _service.CurrentMap!.StartFinishOffsetMeters!.Value, 20);
    }

    [TestMethod]
    public async Task Calibration_ScatteredCrossings_LeavesMapUncalibrated()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        var length = _service.CurrentMap!.TotalLengthMeters;

        // Crossings spread right around the track: no consensus about where the line is.
        foreach (var fraction in new[] { 0.0, 0.2, 0.4, 0.6, 0.8 })
            await _service.AddStartFinishObservationAsync("9", length * fraction);

        Assert.IsFalse(_service.IsStartFinishCalibrated);
    }

    [TestMethod]
    public async Task Calibration_NoMapYet_IgnoresObservations()
    {
        await _service.AddStartFinishObservationAsync("9", 100);

        Assert.IsNull(_service.CurrentMap);
        Assert.IsFalse(_service.IsStartFinishCalibrated);
    }

    [TestMethod]
    public async Task Calibration_CrossingsAgreeingWithTheLine_LeaveItWhereItIs()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        var target = await AddClusteredObservationsAsync(5);
        var settled = _service.CurrentMap!.StartFinishOffsetMeters!.Value;

        // Normal running: crossings keep arriving where the line already is.
        await AddCrossingsAsync(target, 15);

        Assert.AreEqual(settled, _service.CurrentMap.StartFinishOffsetMeters!.Value, 1e-6);
    }

    [TestMethod]
    public async Task Calibration_AFewCrossingsElsewhere_DoNotMoveIt()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        await AddClusteredObservationsAsync(5);
        var settled = _service.CurrentMap!.StartFinishOffsetMeters!.Value;

        // Moving a located line takes more evidence than settling one did.
        await AddCrossingsAsync(_service.CurrentMap.TotalLengthMeters * 0.60, 9);

        Assert.AreEqual(settled, _service.CurrentMap.StartFinishOffsetMeters!.Value, 1e-6);
    }

    [TestMethod]
    public async Task Calibration_ScatteredLaterCrossings_DoNotMoveIt()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        await AddClusteredObservationsAsync(5);
        var settled = _service.CurrentMap!.StartFinishOffsetMeters!.Value;

        // Plenty of crossings, but they do not agree with each other either.
        var length = _service.CurrentMap.TotalLengthMeters;
        for (int i = 0; i < 15; i++)
            await _service.AddStartFinishObservationAsync($"car{i % 5}", length * ((i % 5) * 0.2));

        Assert.AreEqual(settled, _service.CurrentMap.StartFinishOffsetMeters!.Value, 1e-6);
    }

    [TestMethod]
    public async Task Calibration_OneCarCirculatingAlone_CannotMoveTheLine()
    {
        // Ten crossings by one car are not ten measurements, they are one systematic bias repeated
        // - and that car would otherwise capture the line for the whole field.
        await CircleTrack.FeedFullLapAsync(_service);
        await AddClusteredObservationsAsync(5);
        var settled = _service.CurrentMap!.StartFinishOffsetMeters!.Value;
        var elsewhere = _service.CurrentMap.TotalLengthMeters * 0.60;

        for (int window = 0; window < 4; window++)
            await AddCrossingsAsync(elsewhere, 10, cars: 1);

        Assert.AreEqual(settled, _service.CurrentMap.StartFinishOffsetMeters!.Value, 1e-6);
    }

    [TestMethod]
    public async Task Calibration_OneWindowElsewhere_WaitsForASecondOpinion()
    {
        // A field whose crossings fall into two clusters would otherwise move the line back and
        // forth between them for the rest of the event.
        await CircleTrack.FeedFullLapAsync(_service);
        await AddClusteredObservationsAsync(5);
        var settled = _service.CurrentMap!.StartFinishOffsetMeters!.Value;

        await AddCrossingsAsync(_service.CurrentMap.TotalLengthMeters * 0.60, 10);

        Assert.AreEqual(settled, _service.CurrentMap.StartFinishOffsetMeters!.Value, 1e-6);
    }

    [TestMethod]
    public async Task Calibration_ASecondWindowDisagreeing_RetiresTheFirst()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        var target = await AddClusteredObservationsAsync(5);
        var settled = _service.CurrentMap!.StartFinishOffsetMeters!.Value;
        var length = _service.CurrentMap.TotalLengthMeters;

        // Two windows pointing at different places, then one confirming where the line already is.
        await AddCrossingsAsync(length * 0.60, 10);
        await AddCrossingsAsync(length * 0.30, 10);
        await AddCrossingsAsync(target, 10);

        Assert.AreEqual(settled, _service.CurrentMap.StartFinishOffsetMeters!.Value, 1e-6);
    }

    [TestMethod]
    public async Task Calibration_TwoWindowsAgreeingElsewhere_MoveTheLine()
    {
        // A line settled from bad evidence would otherwise be wrong for the rest of the event, and
        // the whole event is what the map is kept for.
        await CircleTrack.FeedFullLapAsync(_service);
        await AddClusteredObservationsAsync(5);
        var settled = _service.CurrentMap!.StartFinishOffsetMeters!.Value;
        var elsewhere = _service.CurrentMap.TotalLengthMeters * 0.60;

        await AddCrossingsAsync(elsewhere, 10);
        await AddCrossingsAsync(elsewhere, 10);

        Assert.AreNotEqual(settled, _service.CurrentMap.StartFinishOffsetMeters!.Value);
        Assert.AreEqual(elsewhere, _service.CurrentMap.StartFinishOffsetMeters!.Value, 20);
    }

    [TestMethod]
    public async Task Calibration_CrossingsJustInsideTheThreshold_DoNotMoveTheLine()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        var target = await AddClusteredObservationsAsync(5);
        var settled = _service.CurrentMap!.StartFinishOffsetMeters!.Value;

        // The floor is 150 m, so 120 m away is ordinary scatter rather than a different line.
        await AddCrossingsAsync(target + 120, 10);
        await AddCrossingsAsync(target + 120, 10);

        Assert.AreEqual(settled, _service.CurrentMap.StartFinishOffsetMeters!.Value, 1e-6);
    }

    [TestMethod]
    public async Task Calibration_CrossingsWellOutsideTheThreshold_MoveTheLine()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        var target = await AddClusteredObservationsAsync(5);

        await AddCrossingsAsync(target + 400, 10);
        await AddCrossingsAsync(target + 400, 10);

        Assert.AreEqual(target + 400, _service.CurrentMap!.StartFinishOffsetMeters!.Value, 20);
    }

    [TestMethod]
    public async Task Calibration_CrossingsThatSettledTheLine_DoNotCountTowardsMovingIt()
    {
        // The window starts fresh after calibration, so the line cannot be moved by evidence that
        // is partly the evidence which placed it.
        await CircleTrack.FeedFullLapAsync(_service);
        await AddClusteredObservationsAsync(5);
        var elsewhere = _service.CurrentMap!.TotalLengthMeters * 0.60;

        // Nine is one short of a window, even though fourteen crossings have now been seen.
        await AddCrossingsAsync(elsewhere, 9);
        Assert.AreEqual(0.10 * _service.CurrentMap.TotalLengthMeters,
            _service.CurrentMap.StartFinishOffsetMeters!.Value, 20);
    }

    [TestMethod]
    public async Task Calibration_MovedLine_IsPersisted()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        await AddClusteredObservationsAsync(5);
        var elsewhere = _service.CurrentMap!.TotalLengthMeters * 0.60;

        await AddCrossingsAsync(elsewhere, 10);
        await AddCrossingsAsync(elsewhere, 10);

        await using var db = _dbContextFactory.CreateDbContext();
        var record = db.TrackMaps.FirstOrDefault(t => t.EventId == EventId);
        Assert.IsNotNull(record);
        Assert.AreEqual(elsewhere, record.Map.StartFinishOffsetMeters!.Value, 20);
    }

    [TestMethod]
    public async Task Calibration_PersistsWithTheMap()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        var target = await AddClusteredObservationsAsync(5);

        await using var db = _dbContextFactory.CreateDbContext();
        var record = db.TrackMaps.FirstOrDefault(t => t.EventId == EventId);
        Assert.IsNotNull(record);
        Assert.IsNotNull(record.Map.StartFinishOffsetMeters);
        Assert.AreEqual(target, record.Map.StartFinishOffsetMeters!.Value, 20);
    }

    [TestMethod]
    public async Task Calibration_SurvivesReload()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        var target = await AddClusteredObservationsAsync(5);

        // A restart mid-event reloads the map, and with it the line it already found.
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
        var reloaded = new TrackMapService(_sessionContext, _dbContextFactory, _redis.Object,
            loggerFactory.Object, new FakeTimeProvider());
        await reloaded.EnsureLoadedAsync();

        Assert.IsTrue(reloaded.IsStartFinishCalibrated);
        Assert.AreEqual(target, reloaded.CurrentMap!.StartFinishOffsetMeters!.Value, 20);
    }

    [TestMethod]
    public async Task Calibration_OnlyTheRecentCrossingsCount()
    {
        // Observations are capped, so a long event cannot accumulate them without bound and the
        // estimate follows the current field rather than crossings from hours ago.
        await CircleTrack.FeedFullLapAsync(_service);
        var length = _service.CurrentMap!.TotalLengthMeters;

        // A run of scattered early crossings, none of which agree.
        for (int i = 0; i < 20; i++)
            await _service.AddStartFinishObservationAsync("9", length * (i % 5) * 0.2);
        Assert.IsFalse(_service.IsStartFinishCalibrated);

        // Then a full window of agreeing ones, which should push the old ones out.
        for (int i = 0; i < 20; i++)
            await _service.AddStartFinishObservationAsync("9", length * 0.10 + (i % 3));

        Assert.IsTrue(_service.IsStartFinishCalibrated);
        Assert.AreEqual(length * 0.10, _service.CurrentMap.StartFinishOffsetMeters!.Value, 20);
    }

    #endregion
}