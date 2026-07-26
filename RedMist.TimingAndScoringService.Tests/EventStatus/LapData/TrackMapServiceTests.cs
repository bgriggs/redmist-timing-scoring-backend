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
        _service.DeclaredLapLengthMeters = CircleTrack.Circumference;

        await CircleTrack.FeedFullLapAsync(_service);

        Assert.IsNotNull(_service.CurrentMap);
    }

    [TestMethod]
    public async Task AddSample_MapAtOddsWithTheDeclaredLength_IsDiscarded()
    {
        // Laps can agree with each other and still be wrong together - a feed reporting lap counts
        // consistently late gives every buffer two laps. The declared length is the only thing that
        // can tell, and a map the event would otherwise be stuck with is thrown away.
        _service.DeclaredLapLengthMeters = CircleTrack.Circumference * 2;

        await CircleTrack.FeedFullLapAsync(_service);

        Assert.IsNull(_service.CurrentMap);
    }

    [TestMethod]
    public async Task AddSample_DiscardedMap_DoesNotPersist()
    {
        _service.DeclaredLapLengthMeters = CircleTrack.Circumference * 2;

        await CircleTrack.FeedFullLapAsync(_service);

        await using var db = _dbContextFactory.CreateDbContext();
        Assert.IsNull(db.TrackMaps.FirstOrDefault(t => t.EventId == EventId));
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
        _service.DeclaredLapLengthMeters = CircleTrack.Circumference * 1.10;

        await CircleTrack.FeedFullLapAsync(_service);

        Assert.IsNotNull(_service.CurrentMap);
    }

    #endregion

    #region Start/finish calibration

    /// <summary>Feeds crossings clustered around a point a tenth of the way along the path.</summary>
    private async Task<double> AddClusteredObservationsAsync(int count)
    {
        var target = _service.CurrentMap!.TotalLengthMeters * 0.10;
        for (int i = 0; i < count; i++)
            await _service.AddStartFinishObservationAsync(target + (i % 5) * 3.0);
        return target;
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
            await _service.AddStartFinishObservationAsync(length * fraction);

        Assert.IsFalse(_service.IsStartFinishCalibrated);
    }

    [TestMethod]
    public async Task Calibration_NoMapYet_IgnoresObservations()
    {
        await _service.AddStartFinishObservationAsync(100);

        Assert.IsNull(_service.CurrentMap);
        Assert.IsFalse(_service.IsStartFinishCalibrated);
    }

    [TestMethod]
    public async Task Calibration_OnceSet_LaterCrossingsDoNotMoveIt()
    {
        await CircleTrack.FeedFullLapAsync(_service);
        await AddClusteredObservationsAsync(5);
        var settled = _service.CurrentMap!.StartFinishOffsetMeters!.Value;

        // A run of crossings from somewhere else entirely must not drag the line around mid-event.
        for (int i = 0; i < 10; i++)
            await _service.AddStartFinishObservationAsync(_service.CurrentMap.TotalLengthMeters * 0.60);

        Assert.AreEqual(settled, _service.CurrentMap.StartFinishOffsetMeters!.Value, 1e-6);
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
            await _service.AddStartFinishObservationAsync(length * (i % 5) * 0.2);
        Assert.IsFalse(_service.IsStartFinishCalibrated);

        // Then a full window of agreeing ones, which should push the old ones out.
        for (int i = 0; i < 20; i++)
            await _service.AddStartFinishObservationAsync(length * 0.10 + (i % 3));

        Assert.IsTrue(_service.IsStartFinishCalibrated);
        Assert.AreEqual(length * 0.10, _service.CurrentMap.StartFinishOffsetMeters!.Value, 20);
    }

    #endregion
}