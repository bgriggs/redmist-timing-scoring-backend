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
public class GpsLapPositionEnricherTests
{
    private const int EventId = 1;
    private SessionContext _sessionContext = null!;
    private FakeTimeProvider _timeProvider = null!;
    private TrackMapService _trackMapService = null!;
    private GpsLapPositionEnricher _enricher = null!;

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

        _enricher = new GpsLapPositionEnricher(loggerFactory.Object, _trackMapService, _sessionContext, _timeProvider);
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"GpsPositionTests_{Guid.NewGuid()}")
            .Options;
        return new TestDbContextFactory(options);
    }

    /// <summary>Learns the circular map and pins its start/finish line to a known point on it.</summary>
    private async Task GivenCalibratedMapAsync(double startFinishFraction = 0.0)
    {
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        _trackMapService.CurrentMap!.StartFinishOffsetMeters =
            _trackMapService.CurrentMap.TotalLengthMeters * startFinishFraction;
    }

    /// <summary>A car registered in the session, positioned at a fraction around the circle.</summary>
    private CarPosition GivenCarAt(double fraction, string number = "5", int lap = 3)
    {
        var (lat, lon) = CircleTrack.Point(fraction);
        var car = new CarPosition
        {
            Number = number,
            LastLapCompleted = lap,
            Latitude = lat,
            Longitude = lon,
        };
        _sessionContext.UpdateCars([car]);
        return _sessionContext.GetCarByNumber(number)!;
    }

    private static void MoveTo(CarPosition car, double fraction)
    {
        var (lat, lon) = CircleTrack.Point(fraction);
        car.Latitude = lat;
        car.Longitude = lon;
    }

    /// <summary>
    /// Drives <paramref name="crossings"/> lap rollovers past the enricher with the car at
    /// <paramref name="fraction"/> along the path each time. The car's first update only establishes
    /// its lap count - a rollover cannot be recognised until there is a previous lap to compare
    /// against - so one extra update is fed in.
    /// </summary>
    private async Task GivenLapRolloversAsync(CarPosition car, double fraction, int crossings)
    {
        for (int i = 0; i <= crossings; i++)
        {
            _timeProvider.Advance(TimeSpan.FromSeconds(2));
            car.LastLapCompleted += 1;
            MoveTo(car, fraction);
            await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);
        }
    }

    #region Position reporting

    [TestMethod]
    public async Task ProcessCar_CalibratedMap_ReportsPositionAsPercentOfLap()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual("5", patch.Number);
        Assert.AreEqual(50, patch.LapPositionPercent!.Value, 1);
        Assert.AreEqual(patch.LapPositionPercent, car.LapPositionPercent);
    }

    [TestMethod]
    public async Task ProcessCar_PositionMeasuredFromTheCalibratedLineNotThePathOrigin()
    {
        // The line sits a quarter of the way along the learned path, so a car three quarters along
        // the path is only half a lap in.
        await GivenCalibratedMapAsync(startFinishFraction: 0.25);
        var car = GivenCarAt(0.75);

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(50, patch.LapPositionPercent!.Value, 1);
    }

    [TestMethod]
    public async Task ProcessCar_NeverWritesProjectedLapTime()
    {
        // Lap-time projection belongs to ProjectedLapTimeEnricher alone.
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        car.ProjectedLapTimeMs = 91_000;

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.IsNull(patch.ProjectedLapTimeMs);
        Assert.AreEqual(91_000, car.ProjectedLapTimeMs);
    }

    [TestMethod]
    public async Task ProcessCar_MovedLessThanAWholePercent_ReturnsNull()
    {
        // Both positions sit inside the same whole percent, away from the bucket edge.
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.504);
        var first = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);
        Assert.AreEqual(50, first!.LapPositionPercent);

        // A fifth of a percent further round: a real move, but not one the published resolution shows.
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        MoveTo(car, 0.506);
        var second = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNull(second, "A car that has not moved a whole percent should not emit a patch");
        Assert.AreEqual(50, car.LapPositionPercent);
    }

    [TestMethod]
    public async Task ProcessCar_JustBeforeTheLine_ReportsNinetyNineNotOneHundred()
    {
        // A lap is 99% complete right up until the line, then wraps to 0. A "100" would name the
        // same point as 0 and would show up every lap.
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.999);

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(99, patch.LapPositionPercent);
    }

    [TestMethod]
    public async Task ProcessCar_OnTheLine_ReportsZero()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.0);

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(0, patch.LapPositionPercent);
    }

    [TestMethod]
    public async Task ProcessCar_WithinThrottleWindow_ReturnsNull()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.10);
        var first = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        _timeProvider.Advance(TimeSpan.FromMilliseconds(400));
        MoveTo(car, 0.15);
        var second = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(first);
        Assert.IsNull(second, "Updates inside the throttle window should be suppressed");
    }

    [TestMethod]
    public async Task ProcessCar_AnyFlag_StillReportsPosition()
    {
        // Where a car is does not depend on the flag, unlike a projected lap time.
        await GivenCalibratedMapAsync();
        _sessionContext.SessionState.CurrentFlag = Flags.Red;
        var car = GivenCarAt(0.5);

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(50, patch.LapPositionPercent!.Value, 1);
    }

    #endregion

    #region Ambiguous geometry

    /// <summary>Learns the dogbone map and pins its start/finish line to the path origin.</summary>
    private async Task GivenCalibratedDogboneAsync()
    {
        await DogboneTrack.FeedFullLapAsync(_trackMapService);
        _trackMapService.CurrentMap!.StartFinishOffsetMeters = 0;
    }

    private CarPosition GivenCarOnDogboneAt(double distanceMeters, string number = "5")
    {
        var (lat, lon) = DogboneTrack.AtDistance(distanceMeters);
        var car = new CarPosition { Number = number, LastLapCompleted = 3, Latitude = lat, Longitude = lon };
        _sessionContext.UpdateCars([car]);
        return _sessionContext.GetCarByNumber(number)!;
    }

    private static void MoveToLocal(CarPosition car, double east, double north)
    {
        var (lat, lon) = DogboneTrack.PointAt(east, north);
        car.Latitude = lat;
        car.Longitude = lon;
    }

    private int PercentOf(double distanceMeters) =>
        (int)(distanceMeters / _trackMapService.CurrentMap!.TotalLengthMeters * 100);

    [TestMethod]
    public async Task ProcessCar_BetweenTwoLegs_StaysOnTheLegItWasAlreadyOn()
    {
        // The car is 13 m from the out leg and 12 m from the return leg, so the nearest point on the
        // map is the wrong one. Having just been seen on the out leg is what settles it.
        await GivenCalibratedDogboneAsync();
        var car = GivenCarOnDogboneAt(425);
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        MoveToLocal(car, 500, 13);
        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(PercentOf(500), patch.LapPositionPercent!.Value, 2,
            "Should stay on the out leg rather than jump to the marginally nearer return leg");
    }

    [TestMethod]
    public async Task ProcessCar_AnchorOnTheWrongLeg_RecoversInsteadOfLockingOn()
    {
        // The anchor is a hint, and this one is wrong: the car is squarely on the return leg while
        // its last known position sits on the out leg. Honouring the anchor would put the car 25 m
        // off the racing line - and, because the legs run in opposite directions, would report its
        // position counting backwards. It has to give way to the unambiguous match.
        await GivenCalibratedDogboneAsync();
        var car = GivenCarOnDogboneAt(425);
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        _timeProvider.Advance(TimeSpan.FromSeconds(1));
        MoveToLocal(car, 425, DogboneTrack.LegSeparation);
        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        // Distance along the return leg at east = 425: out leg + far end + (800 - 425).
        var expected = PercentOf(DogboneTrack.StraightLength + DogboneTrack.LegSeparation + 375);
        Assert.IsNotNull(patch);
        Assert.AreEqual(expected, patch.LapPositionPercent!.Value, 2);
    }

    [TestMethod]
    public async Task ProcessCar_RejectedPositionDoesNotBecomeTheAnchor()
    {
        // A position thrown out as untrustworthy must not be remembered as where the car was: the
        // next snap would be anchored to it, confirm it, and the two would prop each other up
        // indefinitely.
        await GivenCalibratedDogboneAsync();
        var car = GivenCarOnDogboneAt(425);
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        // Well off the track: rejected, and not worth anchoring to.
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        MoveToLocal(car, 425, 200);
        var offTrack = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);
        Assert.AreEqual(CarPosition.InvalidTrackPosition, offTrack!.LapPositionPercent);

        // Back on track a long way round - further than the rejected position could explain.
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        var (lat, lon) = DogboneTrack.AtDistance(1400);
        car.Latitude = lat;
        car.Longitude = lon;
        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(PercentOf(1400), patch.LapPositionPercent!.Value, 2);
    }

    #endregion

    #region Confidence

    [TestMethod]
    public async Task ProcessCar_NoMap_ReportsInvalid()
    {
        var car = GivenCarAt(0.5);

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNull(_trackMapService.CurrentMap);
        Assert.IsNotNull(patch);
        Assert.AreEqual(CarPosition.InvalidTrackPosition, patch.LapPositionPercent);
    }

    [TestMethod]
    public async Task ProcessCar_MapNotYetCalibrated_ReportsInvalid()
    {
        // The map is learned but its start/finish line has not been located, so a percentage of the
        // lap cannot be stated yet.
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        var car = GivenCarAt(0.5);

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(_trackMapService.CurrentMap);
        Assert.IsFalse(_trackMapService.IsStartFinishCalibrated);
        Assert.IsNotNull(patch);
        Assert.AreEqual(CarPosition.InvalidTrackPosition, patch.LapPositionPercent);
    }

    [TestMethod]
    public async Task ProcessCar_InPit_ReportsInvalid()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        car.IsInPit = true;

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(CarPosition.InvalidTrackPosition, patch.LapPositionPercent);
    }

    [TestMethod]
    public async Task ProcessCar_InPitFlaggingZone_ReportsInvalid()
    {
        // Pit and paddock zones start at 128; a pit lane can parallel the main straight closely
        // enough to snap onto it, so geometry alone would not catch this.
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        car.FlaggingZone = 130;

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(CarPosition.InvalidTrackPosition, patch.LapPositionPercent);
    }

    [TestMethod]
    public async Task ProcessCar_OnTrackFlaggingZone_ReportsPosition()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        car.FlaggingZone = 12;

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(50, patch.LapPositionPercent!.Value, 1);
    }

    [TestMethod]
    public async Task ProcessCar_WellOffTheTrackPath_ReportsInvalid()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        // Well outside the circle: a paddock, an access road, or a junk fix.
        car.Latitude = CircleTrack.CenterLat + 0.01;
        car.Longitude = CircleTrack.CenterLon + 0.01;

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(CarPosition.InvalidTrackPosition, patch.LapPositionPercent);
    }

    [TestMethod]
    public async Task ProcessCar_NoGpsOnCar_ReportsInvalid()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        car.Latitude = null;
        car.Longitude = null;

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(CarPosition.InvalidTrackPosition, patch.LapPositionPercent);
    }

    [TestMethod]
    public async Task ProcessCar_RecoversAfterLosingConfidence()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        car.IsInPit = true;
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        car.IsInPit = false;
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(50, patch.LapPositionPercent!.Value, 1);
    }

    #endregion

    #region Telemetry signal strength

    [TestMethod]
    public async Task ProcessCar_HealthySignal_ReportsPosition()
    {
        await GivenCalibratedMapAsync();
        _sessionContext.SessionState.HasTelemetrySource = true;
        var car = GivenCarAt(0.5);
        car.SignalBars = 4;

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(50, patch.LapPositionPercent!.Value, 1);
    }

    [TestMethod]
    public async Task ProcessCar_DegradedSignal_ReportsInvalid()
    {
        // The individual reading looks fine, but the device has been producing enough nonsense
        // recently that its positioning should not be shown at all.
        await GivenCalibratedMapAsync();
        _sessionContext.SessionState.HasTelemetrySource = true;
        var car = GivenCarAt(0.5);
        car.SignalBars = 3;

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(CarPosition.InvalidTrackPosition, patch.LapPositionPercent);
    }

    [TestMethod]
    public async Task ProcessCar_NoSignal_ReportsInvalid()
    {
        // Zero bars is a device that is connected but has nothing usable to say - meaningfully
        // different from null, and the one reading that must never be treated as falsy.
        await GivenCalibratedMapAsync();
        _sessionContext.SessionState.HasTelemetrySource = true;
        var car = GivenCarAt(0.5);
        car.SignalBars = CarPosition.MinSignalBars;

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(CarPosition.InvalidTrackPosition, patch.LapPositionPercent);
    }

    [TestMethod]
    public async Task ProcessCar_SignalRecovers_ReportsPositionAgain()
    {
        await GivenCalibratedMapAsync();
        _sessionContext.SessionState.HasTelemetrySource = true;
        var car = GivenCarAt(0.5);
        car.SignalBars = 1;
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        car.SignalBars = 5;
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(50, patch.LapPositionPercent!.Value, 1);
    }

    [TestMethod]
    public async Task ProcessCar_NoInCarDevice_IsJudgedOnItsPositionAlone()
    {
        // Null bars mean no in-car device, not a bad device: this car's GPS arrives from the timing
        // source itself. Treating null as untrusted would disable track position for every event
        // whose positions come that way.
        await GivenCalibratedMapAsync();
        _sessionContext.SessionState.HasTelemetrySource = true;
        var car = GivenCarAt(0.5);
        Assert.IsNull(car.SignalBars);

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(50, patch.LapPositionPercent!.Value, 1);
    }

    [TestMethod]
    public async Task ProcessCar_DegradedSignal_IsNotUsedAsAnAnchor()
    {
        // A device whose positioning is not trusted must not seed the continuity anchor either.
        await GivenCalibratedDogboneAsync();
        _sessionContext.SessionState.HasTelemetrySource = true;
        var car = GivenCarOnDogboneAt(425);
        car.SignalBars = 1;
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        // Signal recovers with the car between the legs, closer to the return one. With no anchor
        // to hold it, the unambiguous nearest match is what stands.
        car.SignalBars = 5;
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        MoveToLocal(car, 500, 13);
        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        var returnLeg = PercentOf(DogboneTrack.StraightLength + DogboneTrack.LegSeparation + 300);
        Assert.IsNotNull(patch);
        Assert.AreEqual(returnLeg, patch.LapPositionPercent!.Value, 2);
    }

    [TestMethod]
    public async Task ProcessCar_DeadDeviceButPositionFromTheTimingSource_StillReportsPosition()
    {
        // Bars describe the in-car link. Once a device dies they latch at zero for the rest of the
        // session, but a position the timing source produced never depended on that device and must
        // not be condemned by it.
        await GivenCalibratedMapAsync();
        _sessionContext.SessionState.HasTelemetrySource = true;
        var car = GivenCarAt(0.5);
        car.SignalBars = CarPosition.MinSignalBars;

        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: false);

        Assert.IsNotNull(patch);
        Assert.AreEqual(50, patch.LapPositionPercent!.Value, 1);
    }

    [TestMethod]
    public async Task LapRollovers_FromADegradedDevice_DoNotCalibrate()
    {
        // Calibration is permanent once it settles, so a failing device's crossings must not be
        // baked into the map.
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        _sessionContext.SessionState.HasTelemetrySource = true;
        var car = GivenCarAt(0.10, lap: 1);
        car.SignalBars = 2;

        await GivenLapRolloversAsync(car, 0.10, crossings: 8);

        Assert.IsFalse(_trackMapService.IsStartFinishCalibrated);
    }

    #endregion

    #region GPS dropout

    [TestMethod]
    public async Task ExpireStale_AfterTimeout_RetiresThePosition()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        _timeProvider.Advance(TimeSpan.FromSeconds(11));
        var patches = _enricher.ExpireStalePositions();

        Assert.AreEqual(1, patches.Count);
        Assert.AreEqual(CarPosition.InvalidTrackPosition, patches[0].LapPositionPercent);
        Assert.AreEqual(CarPosition.InvalidTrackPosition, car.LapPositionPercent);
    }

    [TestMethod]
    public async Task ExpireStale_WithinTimeout_LeavesThePositionAlone()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        _timeProvider.Advance(TimeSpan.FromSeconds(9));
        var patches = _enricher.ExpireStalePositions();

        Assert.AreEqual(0, patches.Count);
        Assert.AreEqual(50, car.LapPositionPercent!.Value, 1);
    }

    [TestMethod]
    public async Task ExpireStale_AlreadyRetired_DoesNotRepublish()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);
        _timeProvider.Advance(TimeSpan.FromSeconds(11));
        _enricher.ExpireStalePositions();

        _timeProvider.Advance(TimeSpan.FromSeconds(11));
        var patches = _enricher.ExpireStalePositions();

        Assert.AreEqual(0, patches.Count);
    }

    [TestMethod]
    public async Task ExpireStale_StationaryCarStillOnTheAir_KeepsItsPosition()
    {
        // A car sitting still reports the same position over and over, which produces no update of
        // its own. It is still on the air, and its position is still correct.
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        for (int i = 0; i < 4; i++)
        {
            _timeProvider.Advance(TimeSpan.FromSeconds(4));
            _enricher.MarkSeen(["5"], confirmed: true);
            Assert.AreEqual(0, _enricher.ExpireStalePositions().Count);
        }

        Assert.AreEqual(50, car.LapPositionPercent!.Value, 1);
    }

    [TestMethod]
    public async Task ExpireStale_CarThatNeverReported_IsIgnored()
    {
        await GivenCalibratedMapAsync();
        GivenCarAt(0.5, number: "77");

        _timeProvider.Advance(TimeSpan.FromSeconds(30));
        var patches = _enricher.ExpireStalePositions();

        Assert.AreEqual(0, patches.Count);
    }

    [TestMethod]
    public async Task ProcessCar_AfterDropout_ReportsPositionAgain()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);
        _timeProvider.Advance(TimeSpan.FromSeconds(11));
        _enricher.ExpireStalePositions();

        // GPS returns with the car somewhere else entirely - the stale anchor must not hold it back.
        MoveTo(car, 0.20);
        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(20, patch.LapPositionPercent!.Value, 1);
    }

    #endregion

    #region Start/finish calibration

    [TestMethod]
    public async Task LapRollovers_CalibrateTheStartFinishLine()
    {
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        var car = GivenCarAt(0.10, lap: 1);

        // Five crossings, each observed with the car a tenth of the way along the learned path.
        await GivenLapRolloversAsync(car, 0.10, crossings: 5);

        Assert.IsTrue(_trackMapService.IsStartFinishCalibrated);
        Assert.AreEqual(_trackMapService.CurrentMap!.TotalLengthMeters * 0.10,
            _trackMapService.CurrentMap.StartFinishOffsetMeters!.Value,
            _trackMapService.CurrentMap.TotalLengthMeters * 0.02);
    }

    [TestMethod]
    public async Task LapRollovers_InsideTheThrottleWindow_AreStillObserved()
    {
        // Crossings are rare and the calibration depends on them, so they are worth a look even when
        // the car's position would otherwise be throttled away.
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        var car = GivenCarAt(0.10, lap: 1);

        for (int i = 0; i <= 5; i++)
        {
            _timeProvider.Advance(TimeSpan.FromMilliseconds(200));
            car.LastLapCompleted += 1;
            MoveTo(car, 0.10);
            await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);
        }

        Assert.IsTrue(_trackMapService.IsStartFinishCalibrated,
            "Rollovers should be observed even when position updates are throttled");
    }

    [TestMethod]
    public async Task LapRollovers_InThePits_AreNotObserved()
    {
        // A car in the pits is not on the racing line, so where it sits says nothing about where the
        // start/finish line is.
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        var car = GivenCarAt(0.10, lap: 1);
        car.IsInPit = true;

        await GivenLapRolloversAsync(car, 0.10, crossings: 8);

        Assert.IsFalse(_trackMapService.IsStartFinishCalibrated);
    }

    [TestMethod]
    public async Task LapRollovers_OffTheTrackPath_AreNotObserved()
    {
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        var car = GivenCarAt(0.10, lap: 1);

        for (int i = 0; i <= 8; i++)
        {
            _timeProvider.Advance(TimeSpan.FromSeconds(2));
            car.LastLapCompleted += 1;
            car.Latitude = CircleTrack.CenterLat + 0.01;
            car.Longitude = CircleTrack.CenterLon + 0.01;
            await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);
        }

        Assert.IsFalse(_trackMapService.IsStartFinishCalibrated);
    }

    [TestMethod]
    public async Task LapRollovers_TooFewCrossings_LeavesMapUncalibrated()
    {
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        var car = GivenCarAt(0.10, lap: 1);

        await GivenLapRolloversAsync(car, 0.10, crossings: 3);

        Assert.IsFalse(_trackMapService.IsStartFinishCalibrated);
    }

    [TestMethod]
    public async Task CalibratedByRollovers_PositionsAreMeasuredFromThatLine()
    {
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        var car = GivenCarAt(0.10, lap: 1);
        await GivenLapRolloversAsync(car, 0.10, crossings: 5);

        // A tenth of the path further on is a tenth of a lap past the calibrated line.
        _timeProvider.Advance(TimeSpan.FromSeconds(3));
        MoveTo(car, 0.20);
        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(10, patch.LapPositionPercent!.Value, 2);
    }

    #endregion

    [TestMethod]
    public async Task SessionChange_ForgetsTheCarsLapCount()
    {
        // Lap counts restart with the session, so the first update of the new session must not be
        // read as a rollover and fed to the calibration as a start/finish sighting.
        await CircleTrack.FeedFullLapAsync(_trackMapService);
        var car = GivenCarAt(0.10, lap: 1);
        await GivenLapRolloversAsync(car, 0.10, crossings: 4);
        Assert.IsFalse(_trackMapService.IsStartFinishCalibrated, "Four crossings is one short");

        _sessionContext.SessionState.SessionId = 8;
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        car.LastLapCompleted += 1;
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsFalse(_trackMapService.IsStartFinishCalibrated,
            "The first update after a session change has no previous lap to compare against");
    }

    [TestMethod]
    public async Task SessionChange_ForgetsWhatWasPublished()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        // The new session's car starts with nothing published, so the position has to be restated
        // rather than suppressed as unchanged.
        _sessionContext.SessionState.SessionId = 8;
        car.LapPositionPercent = null;
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        var patch = await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        Assert.IsNotNull(patch);
        Assert.AreEqual(50, patch.LapPositionPercent!.Value, 1);
    }

    [TestMethod]
    public async Task SessionChange_StaleSweepDoesNotTouchTheNewSession()
    {
        await GivenCalibratedMapAsync();
        var car = GivenCarAt(0.5);
        await _enricher.ProcessCarAsync(car, fromInCarTelemetry: true);

        _sessionContext.SessionState.SessionId = 8;
        _timeProvider.Advance(TimeSpan.FromSeconds(30));
        var patches = _enricher.ExpireStalePositions();

        Assert.AreEqual(0, patches.Count, "The previous session's cars should not be retired into the new one");
    }
}
