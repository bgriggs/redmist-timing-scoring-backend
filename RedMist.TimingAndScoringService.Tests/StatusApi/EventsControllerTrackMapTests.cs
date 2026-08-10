using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.StatusApi.Controllers.V2;
using RedMist.TimingCommon.LapTiming;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Tests for the V2 LoadTrackMap endpoint: where it sources the learned map from, what it does when
/// each source is missing or broken, and that what it hands back is actually drawable.
/// </summary>
[TestClass]
public class EventsControllerTrackMapTests
{
    private const int EventId = 297;
    private const double Radius = CircleTrack.Radius;

    private EventsController _controller = null!;
    private Mock<IDatabase> _redis = null!;
    private DbContextOptions<TsContext> _dbOptions = null!;
    private IMemoryCache _memoryCache = null!;

    private Mock<IConnectionMultiplexer> _mux = null!;
    private readonly List<MemoryCache> _caches = [];

    [TestInitialize]
    public void Setup()
    {
        _dbOptions = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _redis = new Mock<IDatabase>();
        _redis.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        _mux = new Mock<IConnectionMultiplexer>();
        _mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_redis.Object);

        _controller = BuildController(new TestDbContextFactory(_dbOptions));
        _memoryCache = _caches[0];
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var cache in _caches)
            cache.Dispose();
    }

    private EventsController BuildController(IDbContextFactory<TsContext> dbFactory, IConnectionMultiplexer? mux = null)
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        var cache = new MemoryCache(new MemoryCacheOptions());
        _caches.Add(cache);

        return new EventsController(
            loggerFactory.Object,
            dbFactory,
            new FakeHybridCache(),
            mux ?? _mux.Object,
            cache,
            new Mock<IHttpClientFactory>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    /// <summary>
    /// A database that is down. Only <see cref="CreateDbContext"/> is overridden because
    /// <see cref="IDbContextFactory{TContext}.CreateDbContextAsync"/> has a default implementation
    /// that defers to it, which is what the controller ends up calling.
    /// </summary>
    private sealed class ThrowingDbContextFactory : IDbContextFactory<TsContext>
    {
        public TsContext CreateDbContext() => throw new InvalidOperationException("database unavailable");
    }

    /// <summary>A database read that observes the request being aborted, as a real one does.</summary>
    private sealed class CancelingDbContextFactory : IDbContextFactory<TsContext>
    {
        public TsContext CreateDbContext() => throw new OperationCanceledException();
    }

    #region Helpers

    /// <summary>A circular map with cumulative distances filled in, as the builder would produce.</summary>
    private static TrackMap BuildCircleMap(int pointCount = 72, double? startFinishOffsetMeters = null)
    {
        var map = new TrackMap { EventId = EventId, SessionId = 5, Version = 1, BuiltUtc = DateTime.UtcNow };
        var coords = new List<(double lat, double lon)>();
        for (int i = 0; i < pointCount; i++)
            coords.Add(CircleTrack.Point((double)i / pointCount));

        double cumulative = 0;
        for (int i = 0; i < coords.Count; i++)
        {
            if (i > 0)
                cumulative += TrackGeometry.DistanceMeters(coords[i - 1].lat, coords[i - 1].lon, coords[i].lat, coords[i].lon);
            map.Points.Add(new TrackMapPoint
            {
                Latitude = coords[i].lat,
                Longitude = coords[i].lon,
                CumulativeDistanceMeters = cumulative,
            });
        }
        map.TotalLengthMeters = cumulative + TrackGeometry.DistanceMeters(
            coords[^1].lat, coords[^1].lon, coords[0].lat, coords[0].lon);
        map.StartFinishOffsetMeters = startFinishOffsetMeters;
        return map;
    }

    private void GivenCachedMap(TrackMap map) =>
        _redis.Setup(d => d.StringGetAsync(
                It.Is<RedisKey>(k => k == string.Format(Consts.TRACK_MAP_KEY, EventId)), It.IsAny<CommandFlags>()))
            .ReturnsAsync(JsonSerializer.Serialize(map));

    private async Task GivenStoredMapAsync(TrackMap map)
    {
        using var db = new TsContext(_dbOptions);
        db.TrackMaps.Add(new TrackMapRecord { EventId = EventId, Map = map, UpdatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task GivenEventAsync(string trackName)
    {
        using var db = new TsContext(_dbOptions);
        db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event
        {
            Id = EventId,
            Name = "Test Event",
            TrackName = trackName,
        });
        await db.SaveChangesAsync();
    }

    private static TrackMapRender Unwrap(ActionResult<TrackMapRender> result)
    {
        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, "Expected a 200 response");
        var render = ok.Value as TrackMapRender;
        Assert.IsNotNull(render);
        return render;
    }

    #endregion

    [TestMethod]
    public async Task LoadTrackMap_NoMapAnywhere_Returns404()
    {
        var result = await _controller.LoadTrackMap(EventId);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task LoadTrackMap_FromCache_ReturnsProjectedMap()
    {
        var map = BuildCircleMap();
        GivenCachedMap(map);

        var render = Unwrap(await _controller.LoadTrackMap(EventId));

        Assert.AreEqual(EventId, render.EventId);
        Assert.AreEqual(map.Points.Count, render.Points.Count);
        Assert.AreEqual(map.TotalLengthMeters, render.LengthMeters, 0.001);
        Assert.AreEqual(2 * Radius, render.Bounds.WidthMeters, 2.0);
        Assert.AreEqual(2 * Radius, render.Bounds.HeightMeters, 2.0);
    }

    [TestMethod]
    public async Task LoadTrackMap_CacheMiss_FallsBackToTheDatabase()
    {
        var map = BuildCircleMap();
        await GivenStoredMapAsync(map);

        var render = Unwrap(await _controller.LoadTrackMap(EventId));

        Assert.AreEqual(map.Points.Count, render.Points.Count);
    }

    [TestMethod]
    public async Task LoadTrackMap_CacheFailure_StillServesTheStoredMap()
    {
        _redis.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var stored = BuildCircleMap(pointCount: 55);
        await GivenStoredMapAsync(stored);

        var render = Unwrap(await _controller.LoadTrackMap(EventId));

        Assert.AreEqual(55, render.Points.Count, "A cache outage must not deny a client the stored map");
    }

    [TestMethod]
    public async Task LoadTrackMap_CorruptCachedJson_FallsBackToTheStoredMap()
    {
        _redis.Setup(d => d.StringGetAsync(
                It.Is<RedisKey>(k => k == string.Format(Consts.TRACK_MAP_KEY, EventId)), It.IsAny<CommandFlags>()))
            .ReturnsAsync("{ this is not a track map");
        await GivenStoredMapAsync(BuildCircleMap(pointCount: 48));

        var render = Unwrap(await _controller.LoadTrackMap(EventId));

        Assert.AreEqual(48, render.Points.Count);
    }

    [TestMethod]
    public async Task LoadTrackMap_StoredMapTooSmallToDraw_Returns404()
    {
        var map = new TrackMap { EventId = EventId, TotalLengthMeters = 1000 };
        map.Points.Add(new TrackMapPoint { Latitude = 45, Longitude = -75 });
        await GivenStoredMapAsync(map);

        var result = await _controller.LoadTrackMap(EventId);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result.Result,
            "A map that cannot be projected reads the same to a client as not having one");
    }

    [TestMethod]
    public async Task LoadTrackMap_StampsTheTrackName()
    {
        GivenCachedMap(BuildCircleMap());
        await GivenEventAsync("Test Raceway");

        var render = Unwrap(await _controller.LoadTrackMap(EventId));

        Assert.AreEqual("Test Raceway", render.TrackName);
    }

    [TestMethod]
    public async Task LoadTrackMap_UnknownEvent_StillReturnsTheMapWithoutAName()
    {
        GivenCachedMap(BuildCircleMap());

        var render = Unwrap(await _controller.LoadTrackMap(EventId));

        Assert.IsNull(render.TrackName);
        Assert.AreEqual(72, render.Points.Count, "A missing event row must not cost the map itself");
    }

    [TestMethod]
    public async Task LoadTrackMap_UncalibratedMap_HasNoStartFinishButStillDraws()
    {
        GivenCachedMap(BuildCircleMap());

        var render = Unwrap(await _controller.LoadTrackMap(EventId));

        Assert.IsNull(render.StartFinish);
        Assert.AreEqual(72, render.Points.Count, "The outline is complete even before the line is located");
    }

    [TestMethod]
    public async Task LoadTrackMap_CalibratedMap_LocatesTheStartFinishLine()
    {
        var map = BuildCircleMap();
        map.StartFinishOffsetMeters = map.TotalLengthMeters / 4;
        GivenCachedMap(map);

        var render = Unwrap(await _controller.LoadTrackMap(EventId));

        Assert.IsNotNull(render.StartFinish);
        Assert.AreEqual(map.TotalLengthMeters / 4, render.StartFinish.DistanceAlongMeters, 0.001);

        // It must sit on the drawn outline, not merely somewhere on the map.
        var snap = TrackGeometry.Snap(map.Points, map.TotalLengthMeters,
            render.StartFinish.Latitude, render.StartFinish.Longitude);
        Assert.IsNotNull(snap);
        Assert.AreEqual(0.0, snap.Value.LateralOffsetMeters, 1.0);
    }

    [TestMethod]
    public async Task LoadTrackMap_SecondCall_IsServedFromMemoryWithoutRereadingTheSources()
    {
        GivenCachedMap(BuildCircleMap());

        var first = Unwrap(await _controller.LoadTrackMap(EventId));
        var second = Unwrap(await _controller.LoadTrackMap(EventId));

        Assert.AreSame(first, second);
        _redis.Verify(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task LoadTrackMap_RepeatedMisses_AreNotRereadFromBothStoresEveryTime()
    {
        Assert.IsInstanceOfType<NotFoundObjectResult>((await _controller.LoadTrackMap(EventId)).Result);
        Assert.IsInstanceOfType<NotFoundObjectResult>((await _controller.LoadTrackMap(EventId)).Result);
        Assert.IsInstanceOfType<NotFoundObjectResult>((await _controller.LoadTrackMap(EventId)).Result);

        _redis.Verify(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Once,
            "The window where clients poll for a map that does not exist yet is the busiest one");
    }

    [TestMethod]
    public async Task LoadTrackMap_ReadFailure_Returns503RatherThanClaimingThereIsNoMap()
    {
        // Both stores unreachable. "We could not look" is not the same answer as "there is nothing
        // there", and only the former is a fault worth alerting on.
        _redis.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var controller = BuildController(new ThrowingDbContextFactory());

        var result = await controller.LoadTrackMap(EventId);

        var status = result.Result as ObjectResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }

    [TestMethod]
    public async Task LoadTrackMap_ReadFailure_IsNotCached()
    {
        var failing = new Mock<IDatabase>();
        failing.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(failing.Object);
        var controller = BuildController(new ThrowingDbContextFactory(), mux.Object);

        await controller.LoadTrackMap(EventId);
        await controller.LoadTrackMap(EventId);

        failing.Verify(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Exactly(2),
            "An outage should be retried, not remembered as a miss");
    }

    [TestMethod]
    public async Task LoadTrackMap_CachedMapWins_WhenBothStoresHoldOne()
    {
        var cachedMap = BuildCircleMap(pointCount: 90);
        var storedMap = BuildCircleMap(pointCount: 40);
        GivenCachedMap(cachedMap);
        await GivenStoredMapAsync(storedMap);

        var render = Unwrap(await _controller.LoadTrackMap(EventId));

        Assert.AreEqual(90, render.Points.Count, "The cache is the live copy and takes precedence");
    }

    [TestMethod]
    public async Task LoadTrackMap_MapClaimingAnotherEvent_IsRefused()
    {
        var map = BuildCircleMap();
        map.EventId = 999;
        GivenCachedMap(map);

        var result = await _controller.LoadTrackMap(EventId);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result.Result,
            "Serving it would put another track on this event's screen");
    }

    [TestMethod]
    public async Task LoadTrackMap_MapWithNullPoints_Returns404RatherThan500()
    {
        // A hand-written or truncated cache value: parseable as a TrackMap, but its point list is a
        // nil member rather than an empty list.
        _redis.Setup(d => d.StringGetAsync(
                It.Is<RedisKey>(k => k == string.Format(Consts.TRACK_MAP_KEY, EventId)), It.IsAny<CommandFlags>()))
            .ReturnsAsync($"{{\"eid\":{EventId},\"pts\":null,\"len\":4000.0}}");

        var result = await _controller.LoadTrackMap(EventId);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result.Result,
            "Misses are barely cached, so a throw here would 500 every poll until the value was repaired");
    }

    [TestMethod]
    public async Task LoadTrackMap_ClientDisconnect_PropagatesInsteadOfBeingLoggedAsAFault()
    {
        // This endpoint is documented as one to poll, so clients hang up mid-request routinely.
        // Folding that into the general catch would report every abandoned poll as a database error
        // and answer 503.
        var controller = BuildController(new CancelingDbContextFactory());
        var context = new DefaultHttpContext();
        var aborted = new CancellationTokenSource();
        aborted.Cancel();
        context.RequestAborted = aborted.Token;
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await controller.LoadTrackMap(EventId));
    }

    [TestMethod]
    public async Task LoadTrackMap_Result_SurvivesAMessagePackRoundTrip()
    {
        // This endpoint advertises application/x-msgpack, and MessagePack decodes positionally, so a
        // renumbered key silently shifts every field after it. The repo has been bitten by exactly
        // that before (see EventsControllerSessionStateSerializationTests).
        var map = BuildCircleMap();
        map.StartFinishOffsetMeters = map.TotalLengthMeters / 3;
        GivenCachedMap(map);

        var render = Unwrap(await _controller.LoadTrackMap(EventId));
        var round = MessagePack.MessagePackSerializer.Deserialize<TrackMapRender>(
            MessagePack.MessagePackSerializer.Serialize(render));

        Assert.IsNotNull(round);
        Assert.AreEqual(render.EventId, round.EventId);
        Assert.AreEqual(render.Points.Count, round.Points.Count);
        Assert.AreEqual(render.Points[5].X, round.Points[5].X, 1e-9);
        Assert.AreEqual(render.Points[5].Y, round.Points[5].Y, 1e-9);
        Assert.AreEqual(render.Bounds.WidthMeters, round.Bounds.WidthMeters, 1e-9);
        Assert.IsNotNull(round.StartFinish);
        Assert.AreEqual(render.StartFinish!.DistanceAlongMeters, round.StartFinish.DistanceAlongMeters, 1e-9);
        Assert.AreEqual(render.StartFinish.HeadingDegrees, round.StartFinish.HeadingDegrees, 1e-9);
    }

    [TestMethod]
    public async Task LoadTrackMap_ProjectedPointsAreDrawableInScreenOrientation()
    {
        GivenCachedMap(BuildCircleMap());

        var render = Unwrap(await _controller.LoadTrackMap(EventId));

        Assert.AreEqual(0.0, render.Points.Min(p => p.X), 1e-6);
        Assert.AreEqual(0.0, render.Points.Min(p => p.Y), 1e-6);
        Assert.IsTrue(render.Points.All(p => p.X >= 0 && p.X <= render.Bounds.WidthMeters + 1e-6));
        Assert.IsTrue(render.Points.All(p => p.Y >= 0 && p.Y <= render.Bounds.HeightMeters + 1e-6));

        var northernmost = render.Points.OrderByDescending(p => p.Latitude).First();
        Assert.AreEqual(0.0, northernmost.Y, 1e-6, "Y must increase downward for a client to draw without flipping");
    }
}
