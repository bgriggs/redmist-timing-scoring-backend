using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.LapData;

[TestClass]
public class GpsProjectedLapTimeEnricherTests
{
    private const int EventId = 1;
    private SessionContext _sessionContext = null!;
    private FakeTimeProvider _timeProvider = null!;
    private TrackMapService _trackMapService = null!;
    private Mock<ICarLapHistoryService> _historyService = null!;
    private GpsProjectedLapTimeEnricher _enricher = null!;

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", EventId.ToString() } })
            .Build();

        var dbFactory = CreateDbContextFactory();
        _timeProvider = new FakeTimeProvider();
        _sessionContext = new SessionContext(configuration, dbFactory, loggerFactory.Object,
            new InMemoryCarLapHistoryService(null!), _timeProvider);
        _sessionContext.SessionState.SessionId = 7;

        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(new Mock<IDatabase>().Object);
        _trackMapService = new TrackMapService(_sessionContext, dbFactory, redis.Object, loggerFactory.Object, _timeProvider);

        _historyService = new Mock<ICarLapHistoryService>();
        _historyService.Setup(x => x.GetLapsAsync(It.IsAny<string>())).ReturnsAsync(new List<CarPosition>());
        var historyFallback = new ProjectedLapTimeEnricher(loggerFactory.Object, _historyService.Object, _sessionContext);

        _enricher = new GpsProjectedLapTimeEnricher(loggerFactory.Object, _trackMapService, _sessionContext, historyFallback, _timeProvider);
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"GpsProjTests_{Guid.NewGuid()}")
            .Options;
        return new TestDbContextFactory(options);
    }

    private static CarPosition CarAt(double fraction, int lap = 3, string best = "00:01:30.000")
    {
        var (lat, lon) = CircleTrack.Point(fraction);
        return new CarPosition
        {
            Number = "5",
            LastLapCompleted = lap,
            BestTime = best,
            Latitude = lat,
            Longitude = lon,
        };
    }

    [TestMethod]
    public async Task ProcessCar_WithMapAndElapsed_ProjectsLapTimeFromPosition()
    {
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // First call at the start line stamps lap start; elapsed is 0 so no GPS projection yet.
        var car = CarAt(0.0);
        await _enricher.ProcessCarAsync(car);

        // 45 s later, half way around → projects a ~90 s lap.
        _timeProvider.Advance(TimeSpan.FromSeconds(45));
        var (lat, lon) = CircleTrack.Point(0.5);
        car.Latitude = lat;
        car.Longitude = lon;

        var patch = await _enricher.ProcessCarAsync(car);

        Assert.IsNotNull(patch);
        Assert.AreEqual("5", patch.Number);
        Assert.IsNotNull(patch.ProjectedLapTimeMs);
        Assert.AreEqual(90_000, patch.ProjectedLapTimeMs!.Value, 2_500);
    }

    [TestMethod]
    public async Task ProcessCar_NoMap_FallsBackToHistoryEstimate()
    {
        // No map learned. History has consistent ~90 s green laps, so the fallback should produce ~90 s.
        _historyService.Setup(x => x.GetLapsAsync("5")).ReturnsAsync(GreenLaps(90.0, 5));
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        var car = CarAt(0.5);
        var patch = await _enricher.ProcessCarAsync(car);

        Assert.IsNull(_trackMapService.CurrentMap);
        Assert.IsNotNull(patch);
        Assert.AreEqual(90_000, patch!.ProjectedLapTimeMs!.Value, 2_000);
    }

    [TestMethod]
    public async Task ProcessCar_WithinThrottleWindow_ReturnsNull()
    {
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        var car = CarAt(0.0);
        await _enricher.ProcessCarAsync(car);                 // stamps lap start, sets last-emit

        _timeProvider.Advance(TimeSpan.FromSeconds(45));
        var (lat, lon) = CircleTrack.Point(0.5);
        car.Latitude = lat; car.Longitude = lon;
        var first = await _enricher.ProcessCarAsync(car);     // emits

        _timeProvider.Advance(TimeSpan.FromMilliseconds(100)); // inside the 500 ms throttle
        var (lat2, lon2) = CircleTrack.Point(0.75);
        car.Latitude = lat2; car.Longitude = lon2;
        var second = await _enricher.ProcessCarAsync(car);

        Assert.IsNotNull(first);
        Assert.IsNull(second, "Second update within the throttle window should be suppressed");
    }

    [TestMethod]
    public async Task ProcessCar_NonGreenFlag_DoesNotProjectFromGps()
    {
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        _sessionContext.SessionState.CurrentFlag = Flags.Red;

        var car = CarAt(0.0);
        await _enricher.ProcessCarAsync(car);
        _timeProvider.Advance(TimeSpan.FromSeconds(45));
        var (lat, lon) = CircleTrack.Point(0.5);
        car.Latitude = lat; car.Longitude = lon;

        var patch = await _enricher.ProcessCarAsync(car);

        // No GPS projection under red, and history is empty → nothing to emit.
        Assert.IsNull(patch);
        Assert.AreEqual(0, car.ProjectedLapTimeMs);
    }

    private static List<CarPosition> GreenLaps(double seconds, int count)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        var time = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
        var laps = new List<CarPosition>();
        for (int i = 0; i < count; i++)
            laps.Add(new CarPosition { Number = "5", LastLapTime = time, BestTime = time, TrackFlag = Flags.Green, LapIncludedPit = false });
        return laps;
    }
}