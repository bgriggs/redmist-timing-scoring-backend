using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.DriverInformation;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.Video;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarVideo;
using StackExchange.Redis;
using System.Text.Json;
using DriverInfo = RedMist.TimingCommon.Models.DriverInfo;

namespace RedMist.EventProcessor.Tests.EventStatus;

/// <summary>
/// Covers how the driver and video enrichers read the cache for a set of cars. Both run inside the
/// processing pipeline's write lock on every message that touches cars, so what matters is that
/// their cache latency does not scale with the size of the field: the reads for a tier have to be
/// in flight together rather than one car at a time.
/// </summary>
[TestClass]
public class EnricherBatchReadTests
{
    private const int Cars = 20;

    private readonly SessionContext sessionContext;
    private readonly Mock<IDatabase> cache = new();
    private readonly DriverEnricher driverEnricher;
    private readonly VideoEnricher videoEnricher;

    public EnricherBatchReadTests()
    {
        var mockLogger = new Mock<ILogger>();
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);

        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(cache.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");

        sessionContext = new SessionContext(config, new TestDbContextFactory(optionsBuilder.Options),
            mockLoggerFactory.Object, new Mock<ICarLapHistoryService>().Object);

        // No transponders, so every car is answered by the first tier and the number of reads that
        // tier issues is exactly the number of cars.
        sessionContext.UpdateCars([.. Enumerable.Range(1, Cars)
            .Select(i => new CarPosition { Number = i.ToString(), TransponderId = 0 })]);

        driverEnricher = new DriverEnricher(sessionContext, mockLoggerFactory.Object, mux.Object);
        videoEnricher = new VideoEnricher(sessionContext, mockLoggerFactory.Object, mux.Object);
    }

    private static string[] CarNumbers() => [.. Enumerable.Range(1, Cars).Select(i => i.ToString())];

    /// <summary>
    /// Withholds every reply until all of them have been asked for. Reading one car at a time can
    /// never get past the first, because its reply is waiting on a request that only the last car
    /// would make - so a regression to per-car reads shows up as the timeout below rather than as
    /// a slow test.
    /// </summary>
    private void AnswerOnlyOnceAllAreInFlight(int expected)
    {
        var allInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;

        cache.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref inFlight) == expected)
                    allInFlight.SetResult();
                await allInFlight.Task;
                return RedisValue.Null;
            });
    }

    [TestMethod]
    public async Task DriverEnricher_ProcessCarsAsync_IssuesATiersReadsTogether()
    {
        AnswerOnlyOnceAllAreInFlight(Cars);

        await driverEnricher.ProcessCarsAsync(CarNumbers(), cache.Object)
            .WaitAsync(TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    public async Task VideoEnricher_ProcessCarsAsync_IssuesATiersReadsTogether()
    {
        AnswerOnlyOnceAllAreInFlight(Cars);

        await videoEnricher.ProcessCarsAsync(CarNumbers(), cache.Object)
            .WaitAsync(TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    public async Task DriverEnricher_ProcessCarsAsync_ReadsEachCarsKeyOnce()
    {
        var keys = new List<string>();
        cache.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null)
            .Callback<RedisKey, CommandFlags>((k, _) => { lock (keys) keys.Add(k.ToString()); });

        await driverEnricher.ProcessCarsAsync(CarNumbers(), cache.Object);

        // One tier only: no car has a transponder to fall back to.
        Assert.AreEqual(Cars, keys.Count);
        Assert.AreEqual(Cars, keys.Distinct().Count());
    }

    [TestMethod]
    public async Task DriverEnricher_ProcessCarsAsync_ClearsTheDriverOnCarsTheCacheHasNothingFor()
    {
        cache.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        foreach (var car in sessionContext.SessionState.CarPositions)
            car.DriverName = "Someone";

        var patches = await driverEnricher.ProcessCarsAsync(CarNumbers(), cache.Object);

        Assert.AreEqual(Cars, patches.Count);
        CollectionAssert.AreEquivalent(CarNumbers(), patches.Select(p => p.Number).ToList());
        Assert.IsTrue(patches.All(p => p.DriverName == string.Empty));
        Assert.IsTrue(sessionContext.SessionState.CarPositions.All(c => c.DriverName == string.Empty));
    }

    /// <summary>
    /// A later tier is asked only about the cars that fell through the one before, so its replies
    /// come back in a shorter list than the cars themselves and have to be mapped back by index.
    /// These cover a car whose fallback tier HITS while sitting at a different index in its tier
    /// than in the field - the case where mapping the two the wrong way round silently gives one
    /// car another's driver or video.
    /// </summary>
    [TestMethod]
    public async Task DriverEnricher_ProcessCarsAsync_FallbackHitLandsOnTheCarThatFellThrough()
    {
        // Only the second car falls through, so it is first in the transponder tier and second in
        // the field.
        sessionContext.UpdateCars([
            new CarPosition { Number = "a", TransponderId = 0 },
            new CarPosition { Number = "b", TransponderId = 555 }]);

        cache.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        SetupDriver(string.Format(Consts.EVENT_DRIVER_KEY, sessionContext.EventId, "a"), "id-a", "Driver A");
        SetupDriver(string.Format(Consts.DRIVER_TRANSPONDER_KEY, 555), "id-b", "Driver B");

        await driverEnricher.ProcessCarsAsync(["a", "b"], cache.Object);

        Assert.AreEqual("Driver A", sessionContext.GetCarByNumber("a")!.DriverName);
        Assert.AreEqual("Driver B", sessionContext.GetCarByNumber("b")!.DriverName);
    }

    [TestMethod]
    public async Task VideoEnricher_ProcessCarsAsync_FallbackHitsLandOnTheCarsThatFellThrough()
    {
        // One car per tier, each at a different index in its tier than in the field.
        sessionContext.UpdateCars([
            new CarPosition { Number = "a", TransponderId = 0 },
            new CarPosition { Number = "b", TransponderId = 555 },
            new CarPosition { Number = "c", TransponderId = 777 }]);

        cache.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        SetupVideo(string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "a", 0), "srt://a");
        SetupVideo(string.Format(Consts.EVENT_VIDEO_KEY, 0, string.Empty, 555), "srt://b");
        SetupVideo(string.Format(Consts.EVENT_VIDEO_KEY, sessionContext.EventId, "c", 777), "srt://c");

        await videoEnricher.ProcessCarsAsync(["a", "b", "c"], cache.Object);

        foreach (var number in new[] { "a", "b", "c" })
        {
            var car = sessionContext.GetCarByNumber(number)!;
            Assert.IsNotNull(car.InCarVideo, $"car {number} was left without video");
            Assert.AreEqual($"srt://{number}", car.InCarVideo.VideoDestination.Url);
        }
    }

    private void SetupDriver(string key, string driverId, string driverName)
    {
        var payload = JsonSerializer.Serialize(
            new DriverInfoSource(new DriverInfo { DriverId = driverId, DriverName = driverName }, string.Empty, DateTime.Now));
        cache.Setup(x => x.StringGetAsync(key, It.IsAny<CommandFlags>())).ReturnsAsync((RedisValue)payload);
    }

    private void SetupVideo(string key, string url)
    {
        var payload = JsonSerializer.Serialize(new VideoMetadata
        {
            SystemType = VideoSystemType.Sentinel,
            Destinations = [new VideoDestination { Type = VideoDestinationType.DirectSrt, Url = url }]
        });
        cache.Setup(x => x.StringGetAsync(key, It.IsAny<CommandFlags>())).ReturnsAsync((RedisValue)payload);
    }

    [TestMethod]
    public async Task DriverEnricher_ProcessCarsAsync_UnknownCarsAreSkippedWithoutAffectingTheRest()
    {
        cache.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        var patches = await driverEnricher.ProcessCarsAsync(["1", "not-a-car", "2", ""], cache.Object);

        CollectionAssert.AreEquivalent(new List<string?> { "1", "2" }, patches.Select(p => p.Number).ToList());
    }
}
