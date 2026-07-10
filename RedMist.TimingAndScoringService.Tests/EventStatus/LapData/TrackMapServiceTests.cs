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
}