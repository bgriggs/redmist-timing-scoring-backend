using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.Flagtronics;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.X2;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.Flagtronics;

[TestClass]
public class FlagtronicsProcessorTests
{
    private FlagtronicsProcessor _processor = null!;
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private SessionContext _sessionContext = null!;
    private FakeTimeProvider _time = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        var dict = new Dictionary<string, string?> { { "event_id", "1" }, };
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        var dbContextFactory = new TestDbContextFactory(optionsBuilder.Options);

        var mockLapHistoryService = new Mock<ICarLapHistoryService>();
        _sessionContext = new SessionContext(config, dbContextFactory, _mockLoggerFactory.Object, mockLapHistoryService.Object);
        _sessionContext.UpdateCars(
            [
            new CarPosition { Number = "42", TransponderId = 42, LastLapCompleted = 10 },
            new CarPosition { Number = "7x", TransponderId = 77 },
            ]);

        _time = new FakeTimeProvider();
        _processor = new FlagtronicsProcessor(_mockLoggerFactory.Object, _sessionContext, _time);
    }

    private static TimingMessage FtMessage(string json) =>
        new(Backend.Shared.Consts.FLAGTRONICS_TYPE, json, 1, DateTime.UtcNow);

    private PatchUpdates? Process(string json) => _processor.Process(FtMessage(json));

    /// <summary>
    /// Feeds the same record either side of the pit-state confirm window so a changed pit
    /// reading is committed, standing in for a car whose state genuinely persists across ticks.
    /// </summary>
    private PatchUpdates? ProcessSettled(string json)
    {
        Process(json);
        _time.Advance(TimeSpan.FromSeconds(11));
        return Process(json);
    }

    #region Impossible jumps

    // Road Atlanta start/finish, and points a known distance east of it.
    private const double BaseLat = 34.1500;
    private const double BaseLon = -83.8160;

    private static string At(double eastMeters, int speed = 80)
    {
        var lon = BaseLon + eastMeters / (6_371_000.0 * Math.Cos(BaseLat * Math.PI / 180.0)) * (180.0 / Math.PI);
        return $$"""[{ "carNumber": "42", "speed": {{speed}}, "lat": {{BaseLat.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "lon": {{lon.ToString("F8", System.Globalization.CultureInfo.InvariantCulture)}} }]""";
    }

    [TestMethod]
    public void Teleport_PlausibleMovement_IsApplied()
    {
        Process(At(0));
        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(60));

        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.AreEqual(BaseLon + 60 / (6_371_000.0 * Math.Cos(BaseLat * Math.PI / 180.0)) * (180.0 / Math.PI),
            car.Longitude!.Value, 1e-6, "A car doing 60 m in a second is just a fast car");
    }

    [TestMethod]
    public void Teleport_ImpossibleJump_KeepsTheLastKnownPosition()
    {
        Process(At(0));
        var before = _sessionContext.GetCarByNumber("42")!.Longitude;

        // 900 m in one second is 2,000 mph. The car is still where it was.
        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(900));

        Assert.AreEqual(before, _sessionContext.GetCarByNumber("42")!.Longitude);
    }

    [TestMethod]
    public void Teleport_ImpossibleJump_IsExcludedFromUsableFixes()
    {
        Process(At(0));
        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(900));

        Assert.AreEqual(0, _processor.CarsWithUsableFix.Count);
    }

    [TestMethod]
    public void Teleport_AllowanceScalesWithTheGap()
    {
        // The same 900 m jump is ordinary progress when the car has been out of contact for 20 s.
        Process(At(0));
        _time.Advance(TimeSpan.FromSeconds(20));
        Process(At(900));

        var expected = BaseLon + 900 / (6_371_000.0 * Math.Cos(BaseLat * Math.PI / 180.0)) * (180.0 / Math.PI);
        Assert.AreEqual(expected, _sessionContext.GetCarByNumber("42")!.Longitude!.Value, 1e-6);
    }

    [TestMethod]
    public void Teleport_SustainedRelocation_IsAdoptedRatherThanPinningTheCarForever()
    {
        // A car really can change place without driving there - recovered on a truck, a device
        // reset. Without an escape hatch it would be stuck at its old position for the session.
        Process(At(0));

        for (int i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            Process(At(900));
        }

        var expected = BaseLon + 900 / (6_371_000.0 * Math.Cos(BaseLat * Math.PI / 180.0)) * (180.0 / Math.PI);
        Assert.AreEqual(expected, _sessionContext.GetCarByNumber("42")!.Longitude!.Value, 1e-6,
            "A relocation that persists is the genuine article");
    }

    [TestMethod]
    public void Teleport_RecoveryAfterAGlitch_ResumesFromTheGoodPosition()
    {
        Process(At(0));
        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(900));                      // rejected
        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(60));                       // back where it should be

        var expected = BaseLon + 60 / (6_371_000.0 * Math.Cos(BaseLat * Math.PI / 180.0)) * (180.0 / Math.PI);
        Assert.AreEqual(expected, _sessionContext.GetCarByNumber("42")!.Longitude!.Value, 1e-6);
    }

    [TestMethod]
    public void Teleport_OtherFieldsOnTheRecordStillApply()
    {
        // A bad fix says nothing about the pit flag or the speed reported alongside it.
        Process(At(0));
        _time.Advance(TimeSpan.FromSeconds(1));
        Process("""[{ "carNumber": "42", "speed": 45, "lat": 34.1500, "lon": -83.8060, "flaggingZone": 12 }]""");

        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.AreEqual(45, car.SpeedMph);
        Assert.AreEqual(12, car.FlaggingZone);
    }

    [TestMethod]
    public void Teleport_ReplayAfterAReset_DoesNotReinstateTheRejectedPosition()
    {
        // The last record is replayed onto the car after a timing-system reset, and it runs on
        // every RMonitor message - so replaying a rejected position would undo the rejection within
        // a second and leave the car exactly where it was refused.
        Process(At(0));
        var before = _sessionContext.GetCarByNumber("42")!.Longitude;
        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(900));

        _processor.ProcessCar("42");

        Assert.AreEqual(before, _sessionContext.GetCarByNumber("42")!.Longitude);
    }

    [TestMethod]
    public void Teleport_ReplayOfAnAcceptedPosition_StillApplies()
    {
        Process(At(0));
        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(60));
        var expected = _sessionContext.GetCarByNumber("42")!.Longitude;

        _sessionContext.GetCarByNumber("42")!.Longitude = null;
        _processor.ProcessCar("42");

        Assert.AreEqual(expected, _sessionContext.GetCarByNumber("42")!.Longitude);
    }

    [TestMethod]
    public void Teleport_AlternatingStaleAndTruePosition_IsNotPinnedForever()
    {
        // A device re-acquiring can flip between a cached position and its real one. Each accepted
        // cached reading would reset a consecutive-rejection counter, so the escape hatch would
        // never fire and the car would sit at the stale position for the session.
        Process(At(0));
        for (int i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            Process(At(900));   // true position, rejected as a jump
            _time.Advance(TimeSpan.FromSeconds(1));
            Process(At(0));     // cached stale position, accepted - but the car has not moved
        }

        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(900));
        var expected = BaseLon + 900 / (6_371_000.0 * Math.Cos(BaseLat * Math.PI / 180.0)) * (180.0 / Math.PI);
        Assert.AreEqual(expected, _sessionContext.GetCarByNumber("42")!.Longitude!.Value, 1e-6,
            "Standing still must not keep resetting the escape hatch");
    }

    [TestMethod]
    public void Teleport_AdoptsOnTheThirdReading()
    {
        Process(At(0));
        var startLon = _sessionContext.GetCarByNumber("42")!.Longitude;

        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(900));
        Assert.AreEqual(startLon, _sessionContext.GetCarByNumber("42")!.Longitude, "first jump rejected");

        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(900));
        Assert.AreEqual(startLon, _sessionContext.GetCarByNumber("42")!.Longitude, "second jump rejected");

        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(900));
        var expected = BaseLon + 900 / (6_371_000.0 * Math.Cos(BaseLat * Math.PI / 180.0)) * (180.0 / Math.PI);
        Assert.AreEqual(expected, _sessionContext.GetCarByNumber("42")!.Longitude!.Value, 1e-6,
            "third adopted");
    }

    [TestMethod]
    public void Teleport_PositionlessRecordMidRun_DoesNotResetTheCount()
    {
        Process(At(0));
        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(900));                                                   // rejection 1
        _time.Advance(TimeSpan.FromSeconds(1));
        Process("""[{ "carNumber": "42", "speed": 40, "flaggingZone": 12 }]""");  // no coordinates
        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(900));                                                   // rejection 2
        _time.Advance(TimeSpan.FromSeconds(1));
        Process(At(900));                                                   // adopted

        var expected = BaseLon + 900 / (6_371_000.0 * Math.Cos(BaseLat * Math.PI / 180.0)) * (180.0 / Math.PI);
        Assert.AreEqual(expected, _sessionContext.GetCarByNumber("42")!.Longitude!.Value, 1e-6);
    }

    [TestMethod]
    public void Teleport_SessionChange_ForgetsWhereTheCarWas()
    {
        Process(At(0));

        _sessionContext.SessionState.SessionId = 99;
        Process(At(5000));

        var expected = BaseLon + 5000 / (6_371_000.0 * Math.Cos(BaseLat * Math.PI / 180.0)) * (180.0 / Math.PI);
        Assert.AreEqual(expected, _sessionContext.GetCarByNumber("42")!.Longitude!.Value, 1e-6,
            "A new session starts from wherever the car now is");
    }

    [TestMethod]
    public void Teleport_FirstFixForACar_IsAlwaysAccepted()
    {
        Process(At(5000));

        Assert.IsNotNull(_sessionContext.GetCarByNumber("42")!.Longitude);
    }

    #endregion

    #region Cars with a usable fix

    [TestMethod]
    public void CarsWithUsableFix_ReportedPosition_IsListed()
    {
        Process("""[{ "carNumber": "42", "speed": 80, "lat": 36.5841, "lon": -121.7539 }]""");

        CollectionAssert.AreEquivalent(new[] { "42" }, _processor.CarsWithUsableFix.ToArray());
    }

    [TestMethod]
    public void CarsWithUsableFix_UnchangedPosition_IsStillListed()
    {
        // The point of this list: an unchanged record produces no patch, but the car is plainly
        // still reporting its position.
        var json = """[{ "carNumber": "42", "speed": 80, "lat": 36.5841, "lon": -121.7539 }]""";
        Process(json);
        var second = Process(json);

        Assert.IsNull(second, "Nothing changed, so there is no patch to carry the fact it reported");
        CollectionAssert.AreEquivalent(new[] { "42" }, _processor.CarsWithUsableFix.ToArray());
    }

    [TestMethod]
    public void CarsWithUsableFix_BadGpsSentinelSpeed_IsExcluded()
    {
        Process("""[{ "carNumber": "42", "speed": 255, "lat": 36.5841, "lon": -121.7539 }]""");

        Assert.AreEqual(0, _processor.CarsWithUsableFix.Count);
    }

    [TestMethod]
    public void CarsWithUsableFix_ZoneWithoutCoordinates_IsExcluded()
    {
        // Not faulted - the zone locates the car well enough to grade its signal - but there is
        // nothing here to position from.
        Process("""[{ "carNumber": "42", "speed": 40, "flaggingZone": 45 }]""");

        Assert.AreEqual(0, _processor.CarsWithUsableFix.Count);
    }

    [TestMethod]
    public void CarsWithUsableFix_ZeroCoordinates_IsExcluded()
    {
        Process("""[{ "carNumber": "42", "speed": 40, "flaggingZone": 45, "lat": 0, "lon": 0 }]""");

        Assert.AreEqual(0, _processor.CarsWithUsableFix.Count);
    }

    [TestMethod]
    public void CarsWithUsableFix_CarUnknownToTiming_IsExcluded()
    {
        Process("""[{ "carNumber": "999", "speed": 80, "lat": 36.5841, "lon": -121.7539 }]""");

        Assert.AreEqual(0, _processor.CarsWithUsableFix.Count);
    }

    [TestMethod]
    public void CarsWithUsableFix_IsNotCarriedOverFromAnEarlierMessage()
    {
        Process("""[{ "carNumber": "42", "speed": 80, "lat": 36.5841, "lon": -121.7539 }]""");
        Assert.AreEqual(1, _processor.CarsWithUsableFix.Count);

        // Every early return has to leave the list empty, or cars that are no longer reporting keep
        // looking current.
        _processor.Process(new TimingMessage(Backend.Shared.Consts.X2PASS_TYPE, "[]", 1, DateTime.UtcNow));
        Assert.AreEqual(0, _processor.CarsWithUsableFix.Count);

        Process("""[{ "carNumber": "42", "speed": 80, "lat": 36.5841, "lon": -121.7539 }]""");
        Assert.AreEqual(1, _processor.CarsWithUsableFix.Count);

        Process("not json at all");
        Assert.AreEqual(0, _processor.CarsWithUsableFix.Count);

        Process("""[{ "carNumber": "42", "speed": 80, "lat": 36.5841, "lon": -121.7539 }]""");
        Assert.AreEqual(1, _processor.CarsWithUsableFix.Count);

        Process("[]");
        Assert.AreEqual(0, _processor.CarsWithUsableFix.Count);
    }

    #endregion

    #region Basic processing

    [TestMethod]
    public void Process_WrongMessageType_ReturnsNull()
    {
        var result = _processor.Process(new TimingMessage(Backend.Shared.Consts.X2PASS_TYPE, "[]", 1, DateTime.UtcNow));
        Assert.IsNull(result);
        Assert.IsFalse(_sessionContext.IsFlagtronicsPitActive);
    }

    [TestMethod]
    public void Process_InvalidJson_ReturnsNull()
    {
        Assert.IsNull(Process("not json"));
    }

    [TestMethod]
    public void Process_VehicleData_SetsFlagtronicsPitActive()
    {
        Process("""[{ "carNumber": "42", "pitActive": false }]""");
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitActive);
    }

    [TestMethod]
    public void Process_UnknownCar_Ignored()
    {
        var result = Process("""[{ "carNumber": "999", "speed": 88, "lat": 36.5, "lon": -121.7, "pitActive": false }]""");
        Assert.IsNull(result);
    }

    #endregion

    #region GPS and speed

    [TestMethod]
    public void Process_GpsAndSpeed_AppliedToCar()
    {
        var result = Process("""[{ "carNumber": "42", "speed": 88, "lat": 36.5841, "lon": -121.7539, "pitActive": false }]""");

        Assert.IsNotNull(result);
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.AreEqual(36.5841, car.Latitude);
        Assert.AreEqual(-121.7539, car.Longitude);
        Assert.AreEqual(88, car.SpeedMph);
    }

    [TestMethod]
    public void Process_ZeroZeroGps_Ignored()
    {
        Process("""[{ "carNumber": "42", "lat": 36.5841, "lon": -121.7539, "pitActive": false }]""");
        Process("""[{ "carNumber": "42", "lat": 0, "lon": 0, "pitActive": false }]""");

        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.AreEqual(36.5841, car.Latitude);
        Assert.AreEqual(-121.7539, car.Longitude);
    }

    [TestMethod]
    public void Process_SpeedBadGpsSentinel_Ignored()
    {
        Process("""[{ "carNumber": "42", "speed": 88, "pitActive": false }]""");
        Process("""[{ "carNumber": "42", "speed": 255, "pitActive": false }]""");

        Assert.AreEqual(88, _sessionContext.GetCarByNumber("42")!.SpeedMph);
    }

    [TestMethod]
    public void Process_SpeedStoppedSentinel_MapsToZero()
    {
        Process("""[{ "carNumber": "42", "speed": 254, "pitActive": false }]""");
        Assert.AreEqual(0, _sessionContext.GetCarByNumber("42")!.SpeedMph);
    }

    #endregion

    #region Pit state

    [TestMethod]
    public void Process_PitEntry_SetsInPitAndEnteredEdge()
    {
        var result = ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "pitEntryTime": "2026-07-17T09:12:41Z", "pitDuration": "00:03:05.000" }]""");

        Assert.IsNotNull(result);
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.IsInPit);
        Assert.IsTrue(car.IsEnteredPit);
        Assert.IsFalse(car.IsExitedPit);
        Assert.IsTrue(car.LapIncludedPit);
        Assert.AreEqual(new DateTime(2026, 7, 17, 9, 12, 41, DateTimeKind.Utc), car.PitEntryTime);
        Assert.AreEqual(185000, car.PitDurationMs);
    }

    [TestMethod]
    public void Process_SecondUpdateInPit_ClearsEnteredEdge()
    {
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true }]""");
        Process("""[{ "carNumber": "42", "pitActive": true, "pitDuration": "00:00:30.000" }]""");

        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.IsInPit);
        Assert.IsFalse(car.IsEnteredPit);
    }

    [TestMethod]
    public void Process_PitExit_SetsExitedEdge()
    {
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true }]""");
        var result = ProcessSettled("""[{ "carNumber": "42", "pitActive": false, "pitDuration": "00:02:00.500" }]""");

        Assert.IsNotNull(result);
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsFalse(car.IsInPit);
        Assert.IsFalse(car.IsEnteredPit);
        Assert.IsTrue(car.IsExitedPit);
        Assert.AreEqual(120500, car.PitDurationMs);
    }

    [TestMethod]
    public void Process_SpeedEnforcementFields_Applied()
    {
        Process("""[{ "carNumber": "42", "pitActive": true, "enforced": true, "speedViolation": true, "flaggingZone": 130 }]""");

        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.PitSpeedEnforced);
        Assert.IsTrue(car.SpeedViolation);
        Assert.AreEqual(130, car.FlaggingZone);
    }

    [TestMethod]
    public void Process_LapIncludedPit_TrueForPittedLap()
    {
        // Car pits during lap 11 (LastLapCompleted 10)
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.LapIncludedPit);

        // Back out on track, still on lap 11: the lap in progress did include the stop.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": false }]""");
        Assert.IsTrue(car.LapIncludedPit);

        // Lap 11 completes and lap 12 starts clean: the flag drops.
        car.LastLapCompleted = 11;
        Process("""[{ "carNumber": "42", "pitActive": false, "speed": 50 }]""");
        Assert.IsFalse(car.LapIncludedPit);
    }

    [TestMethod]
    public void UpdateCarPositionForLogging_TagsOnlyTheLapsThatIncludedTheStop()
    {
        // Regression: the live flag used to stay asserted for the whole lap after the stop, so
        // a stop spanning laps 11-12 logged three consecutive laps as pit laps instead of two.
        var car = _sessionContext.GetCarByNumber("42")!;   // on lap 11 (LastLapCompleted 10)
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 5 }]""");

        // Scored across start/finish while still stopped, so lap 12 also included the stop.
        car.LastLapCompleted = 11;
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 5 }]""");

        // Out on track for lap 13 onwards.
        car.LastLapCompleted = 12;
        ProcessSettled("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 5, "speed": 90 }]""");

        foreach (var (lap, expected) in new[] { (10, false), (11, true), (12, true), (13, false), (14, false) })
        {
            var logged = new CarPosition { Number = "42", LastLapCompleted = lap, LapIncludedPit = !expected };
            _processor.UpdateCarPositionForLogging(logged);
            Assert.AreEqual(expected, logged.LapIncludedPit, $"lap {lap}");
        }
    }

    [TestMethod]
    public void UpdateCarPositionForLogging_IgnoredWhenFlagtronicsNotActive()
    {
        var logged = new CarPosition { Number = "42", LastLapCompleted = 11, LapIncludedPit = true };
        _processor.UpdateCarPositionForLogging(logged);

        // No Flagtronics data seen, so X2 stays authoritative for the logged value.
        Assert.IsTrue(logged.LapIncludedPit);
    }

    [TestMethod]
    public void Process_StuckPitActive_ClearedByOnTrackZone()
    {
        // Car enters the pit (pit zone, pitActive true)
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 10 }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.IsInPit);

        // Device leaves pitActive latched true, but the car is back on the racing surface at
        // speed: the on-track flagging zone overrides the stuck flag and clears IsInPit.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 5, "speed": 90 }]""");
        Assert.IsFalse(car.IsInPit);
        Assert.IsTrue(car.IsExitedPit);
    }

    [TestMethod]
    public void Process_PitZoneWithoutPitActive_IndicatesInPit()
    {
        // pitActive lags/misses entry, but the car is physically in the pit-entry lane zone:
        // location drives the indication so it is not off or late.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 145, "speed": 30 }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.IsInPit);
        Assert.IsTrue(car.IsEnteredPit);
    }

    [TestMethod]
    public void Process_PitZoneAtRacingSpeed_TreatedAsGlitch()
    {
        // A pit-zone reading at racing speed with pitActive false is a GPS glitch (an on-track
        // car momentarily mis-tagged) and must not flip the car into the pit.
        Process("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 129, "speed": 95 }]""");
        Assert.IsFalse(_sessionContext.GetCarByNumber("42")!.IsInPit);
    }

    [TestMethod]
    public void Process_StuckPitActiveOnTrack_DoesNotMarkLapAsPit()
    {
        // Stuck pitActive while racing must not tag the lap as a pit lap.
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 5, "speed": 90 }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsFalse(car.IsInPit);
        Assert.IsFalse(car.LapIncludedPit);
    }

    [TestMethod]
    public void Process_StuckPitActiveOnTrack_DoesNotApplyRunawayDuration()
    {
        // Real pit stop reports a duration.
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "pitDuration": "00:01:00.000" }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.AreEqual(60000, car.PitDurationMs);

        // Stuck pitActive while on track reports a runaway duration - it must be ignored.
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 5, "speed": 90, "pitDuration": "48:00:00.000" }]""");
        Assert.IsFalse(car.IsInPit);
        Assert.AreEqual(60000, car.PitDurationMs);
    }

    [TestMethod]
    public void Process_PreGridStaging_ShownInPitButLapNotTagged()
    {
        // Pre-race: car sits in a paddock/pit zone with pitActive false, never having run a
        // lap. It shows in pit (it physically is), but the lap is not tagged as a pit lap.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 161, "speed": 0 }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.IsInPit);
        Assert.IsFalse(car.LapIncludedPit);

        // Once the car has run on track, a subsequent pit stop is tagged normally.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 5, "speed": 80 }]""");
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 10 }]""");
        Assert.IsTrue(car.IsInPit);
        Assert.IsTrue(car.LapIncludedPit);
    }

    #endregion

    #region Pit state debounce

    [TestMethod]
    public void Process_BriefPitZoneGlitch_DoesNotProduceEnterExitEdges()
    {
        // Observed live: a car mid-lap reports a pit/paddock zone for a couple of ticks. Without
        // the confirm window this emitted a pit entry immediately followed by a pit exit.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 5, "speed": 90 }]""");

        _time.Advance(TimeSpan.FromSeconds(2));
        Process("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 144, "speed": 60 }]""");
        _time.Advance(TimeSpan.FromSeconds(2));
        Process("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 160, "speed": 62 }]""");

        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsFalse(car.IsInPit);
        Assert.IsFalse(car.IsEnteredPit);
        Assert.IsFalse(car.LapIncludedPit);
    }

    [TestMethod]
    public void Process_BriefOnTrackZoneMidStop_DoesNotProduceSpuriousExit()
    {
        // Observed live: zone flickers 129 -> 12 -> 129 at pit-lane speed during a real stop.
        // The car must stay shown as in the pit rather than exiting and re-entering.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 29 }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.IsInPit);

        _time.Advance(TimeSpan.FromSeconds(2));
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 12, "speed": 32 }]""");
        Assert.IsTrue(car.IsInPit);
        Assert.IsFalse(car.IsExitedPit);

        _time.Advance(TimeSpan.FromSeconds(2));
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 31 }]""");
        Assert.IsTrue(car.IsInPit);
        Assert.IsFalse(car.IsEnteredPit);
    }

    [TestMethod]
    public void Process_BogusPitDuration_DiscardsPitActive()
    {
        // Observed live: a device with no valid pit entry time reports pitActive true with a
        // duration of ~17753304 hours while the car is doing 118 mph. That flag means nothing,
        // and previously drove a pit entry through the pit-zone-at-racing-speed fallback.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 5, "speed": 100 }]""");

        for (int i = 0; i < 6; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 128, "speed": 118, "pitDuration": "17753304:00:07.742" }]""");
        }

        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsFalse(car.IsInPit);
        Assert.IsFalse(car.IsEnteredPit);
        Assert.IsFalse(car.LapIncludedPit);
        Assert.IsNull(car.PitDurationMs);
    }

    [TestMethod]
    public void Process_BogusPitDurationWithoutGps_DoesNotEnterPit()
    {
        // Same device fault with zone 0 (no GPS), where pitActive would otherwise be the only
        // pit-presence source.
        for (int i = 0; i < 6; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 0, "speed": 78, "pitDuration": "17753304:00:17.742" }]""");
        }

        Assert.IsFalse(_sessionContext.GetCarByNumber("42")!.IsInPit);
    }

    [TestMethod]
    public void Process_SustainedPitReading_CommitsAfterConfirmWindow()
    {
        // The debounce must not swallow real stops: a reading that persists is applied.
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 8 }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsFalse(car.IsInPit, "should not commit on the first tick");

        _time.Advance(TimeSpan.FromSeconds(11));
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 0 }]""");
        Assert.IsTrue(car.IsInPit);
        Assert.IsTrue(car.IsEnteredPit);
    }

    [TestMethod]
    public void ProcessCar_ReplayDoesNotConfirmPendingChange()
    {
        // ProcessCar replays the last record on every RMonitor pass. If it advanced the
        // debounce, a single glitched record would be re-confirmed until it passed the window.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 5, "speed": 90 }]""");
        Process("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 144, "speed": 60 }]""");

        _time.Advance(TimeSpan.FromSeconds(30));
        _processor.ProcessCar("42");
        _processor.ProcessCar("42");

        Assert.IsFalse(_sessionContext.GetCarByNumber("42")!.IsInPit);
    }

    [TestMethod]
    public void Process_EntryConfirmedAfterLapRollover_TagsTheEntryLap()
    {
        var car = _sessionContext.GetCarByNumber("42")!;   // on lap 11 (LastLapCompleted 10)

        // Entry seen on lap 11 but only confirmed after the car is scored across start/finish.
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 20 }]""");
        car.LastLapCompleted = 11;
        _time.Advance(TimeSpan.FromSeconds(11));
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 0 }]""");

        Assert.IsTrue(car.IsInPit);

        var lap11 = new CarPosition { Number = "42", LastLapCompleted = 11 };
        _processor.UpdateCarPositionForLogging(lap11);
        Assert.IsTrue(lap11.LapIncludedPit, "the lap the car entered on must still be tagged");
    }

    [TestMethod]
    public void ProcessCar_ReplayDoesNotKeepTaggingLapsAfterTheDeviceGoesQuiet()
    {
        // A car whose device stops reporting mid-stop leaves a pit record in lastVehicles, which
        // ProcessCar replays on every RMonitor pass. That must not keep appending pit laps - the
        // recorded set is what the lap log is stamped from.
        var car = _sessionContext.GetCarByNumber("42")!;   // on lap 11 (LastLapCompleted 10)
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 0 }]""");

        // Feed goes silent; the car is scored around for several more laps.
        for (var lap = 11; lap <= 15; lap++)
        {
            car.LastLapCompleted = lap;
            _processor.ProcessCar("42");
        }

        foreach (var lap in new[] { 12, 13, 14, 15, 16 })
        {
            var logged = new CarPosition { Number = "42", LastLapCompleted = lap };
            _processor.UpdateCarPositionForLogging(logged);
            Assert.IsFalse(logged.LapIncludedPit, $"lap {lap} was never observed in the pit");
        }
    }

    [TestMethod]
    public void Process_GlitchesEitherSideOfAFeedGap_DoNotConfirmEachOther()
    {
        // A candidate pit state is only evidence while the car is actually being observed. Two
        // isolated glitches minutes apart must not satisfy the confirm window between them.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 5, "speed": 90 }]""");

        Process("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 144, "speed": 60 }]""");
        _time.Advance(TimeSpan.FromMinutes(5));
        Process("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 144, "speed": 60 }]""");

        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsFalse(car.IsInPit);
        Assert.IsFalse(car.IsEnteredPit);
    }

    [TestMethod]
    public void Process_UnrecognisedDurationFormat_DoesNotDisablePitDetection()
    {
        // A duration this code cannot parse may just be a feed format change. That must not be
        // read as "the device is faulty" and silently switch off in-car pit detection.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 0, "pitDuration": "PT3M5S" }]""");

        Assert.IsTrue(_sessionContext.GetCarByNumber("42")!.IsInPit);
    }

    [TestMethod]
    public void Process_BogusPitDuration_DoesNotPublishBogusPitEntryTime()
    {
        // The unset-entry-time fault reports 0001-01-01 alongside the runaway duration.
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 5, "speed": 95, "pitEntryTime": "0001-01-01T00:00:00Z", "pitDuration": "17753304:00:07.742" }]""");

        Assert.IsNull(_sessionContext.GetCarByNumber("42")!.PitEntryTime);
    }

    #endregion

    #region No-GPS lap-completion fallback

    [TestMethod]
    public void NotifyLapCompleted_NoGpsStuckPit_ClearsAndSuppresses()
    {
        // Car is in the pit with no GPS (zone 0) and a stuck pitActive.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 0 }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.IsInPit);

        // A completed lap proves it crossed S/F on track: clear the frozen pit state.
        var patch = _processor.NotifyLapCompleted("42");
        Assert.IsNotNull(patch);
        Assert.IsFalse(car.IsInPit);

        // The stuck flag stays suppressed: further no-GPS ticks do not re-set IsInPit.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 0 }]""");
        Assert.IsFalse(car.IsInPit);
    }

    [TestMethod]
    public void NotifyLapCompleted_StuckPitStillWithinConfirmWindow_CancelsThePendingEntry()
    {
        // The stuck flag may not have been committed yet. A start/finish crossing proves the car
        // is on track, so the pending entry must be dropped rather than committing seconds later.
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 0 }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsFalse(car.IsInPit, "not committed yet");

        _processor.NotifyLapCompleted("42");

        _time.Advance(TimeSpan.FromSeconds(11));
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 0 }]""");
        Assert.IsFalse(car.IsInPit);
        Assert.IsFalse(car.LapIncludedPit);
    }

    [TestMethod]
    public void NotifyLapCompleted_WithGps_NoOp()
    {
        // GPS present (pit zone): the zone is authoritative, so the fallback does nothing.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 10 }]""");
        Assert.IsTrue(_sessionContext.GetCarByNumber("42")!.IsInPit);

        var patch = _processor.NotifyLapCompleted("42");
        Assert.IsNull(patch);
        Assert.IsTrue(_sessionContext.GetCarByNumber("42")!.IsInPit);
    }

    [TestMethod]
    public void NotifyLapCompleted_SuppressionClearedWhenGpsReturns()
    {
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 0 }]""");
        _processor.NotifyLapCompleted("42");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsFalse(car.IsInPit);

        // GPS returns and shows a real pit zone: suppression clears and pit shows again.
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 5 }]""");
        Assert.IsTrue(car.IsInPit);
    }

    #endregion

    #region Flags and driver source

    [TestMethod]
    public void Process_CarFlag_MappedToLocalFlag()
    {
        Process("""[{ "carNumber": "42", "pitActive": false, "carFlag": "StYellow" }]""");
        Assert.AreEqual(Flags.Yellow, _sessionContext.GetCarByNumber("42")!.LocalFlag);

        Process("""[{ "carNumber": "42", "pitActive": false, "carFlag": "MeatBall" }]""");
        Assert.AreEqual(Flags.MeatBall, _sessionContext.GetCarByNumber("42")!.LocalFlag);

        Process("""[{ "carNumber": "42", "pitActive": false, "carFlag": "SomeFutureFlag" }]""");
        Assert.AreEqual(Flags.Unknown, _sessionContext.GetCarByNumber("42")!.LocalFlag);
    }

    [TestMethod]
    public void Process_DriverSource_LegacySpellingNormalized()
    {
        Process("""[{ "carNumber": "42", "pitActive": false, "driverSource": "BleDrid" }]""");
        Assert.AreEqual("blePuck", _sessionContext.GetCarByNumber("42")!.DriverSource);

        Process("""[{ "carNumber": "42", "pitActive": false, "driverSource": "manualOverride" }]""");
        Assert.AreEqual("manualOverride", _sessionContext.GetCarByNumber("42")!.DriverSource);
    }

    #endregion

    #region Overall flag precedence (RMonitor authoritative; Flagtronics Purple override)

    private static EventProcessor.EventStatus.RMonitor.StateChanges.HeartbeatStateUpdate HeartbeatUpdate(
        SessionContext ctx, string flagStatus)
    {
        var heartbeat = new EventProcessor.EventStatus.RMonitor.Heartbeat();
        heartbeat.ProcessF($"$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"{flagStatus}\"");
        ctx.RMonitorTrackFlag = heartbeat.FlagStatus.ToFlag();
        return new EventProcessor.EventStatus.RMonitor.StateChanges.HeartbeatStateUpdate(
            heartbeat, ctx.GetEffectiveTrackFlag());
    }

    [TestMethod]
    public void Process_FullCourseFlagAlone_DoesNotDriveOverallFlag()
    {
        // With no RMonitor flag yet, a Flagtronics full-course flag does not set the overall
        // flag - RMonitor is authoritative. (Speed included only to produce a car patch.)
        var result = Process("""[{ "carNumber": "42", "pitActive": false, "speed": 50, "fullCourseFlag": "Yellow" }]""");

        Assert.IsNotNull(result);
        Assert.IsEmpty(result.SessionPatches);
        Assert.AreEqual(Flags.Yellow, _sessionContext.FlagtronicsFullCourseFlag);
        Assert.AreEqual(Flags.Unknown, _sessionContext.SessionState.CurrentFlag);
    }

    [TestMethod]
    public void Process_Purple_UpgradesRMonitorYellow()
    {
        // RMonitor is showing Yellow.
        _sessionContext.RMonitorTrackFlag = Flags.Yellow;
        _sessionContext.SessionState.CurrentFlag = Flags.Yellow;

        // Flagtronics reports Purple: upgrade the overall flag to Purple35.
        var result = Process("""[{ "carNumber": "42", "pitActive": false, "fullCourseFlag": "Purple" }]""");

        Assert.IsNotNull(result);
        Assert.AreEqual(Flags.Purple35, _sessionContext.SessionState.CurrentFlag);
        Assert.IsTrue(result.SessionPatches.Any(sp => sp.CurrentFlag == Flags.Purple35));
    }

    [TestMethod]
    public void Process_Purple_WithoutRMonitorYellow_NoOverride()
    {
        // RMonitor is Green: Purple does not override anything but a Yellow.
        _sessionContext.RMonitorTrackFlag = Flags.Green;
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        var result = Process("""[{ "carNumber": "42", "pitActive": false, "speed": 50, "fullCourseFlag": "Purple" }]""");

        Assert.IsNotNull(result);
        Assert.IsEmpty(result.SessionPatches);
        Assert.AreEqual(Flags.Green, _sessionContext.SessionState.CurrentFlag);
    }

    [TestMethod]
    public void Process_PurpleReleased_WhenFlagtronicsLeavesPurple()
    {
        _sessionContext.RMonitorTrackFlag = Flags.Yellow;
        _sessionContext.SessionState.CurrentFlag = Flags.Yellow;
        Process("""[{ "carNumber": "42", "pitActive": false, "fullCourseFlag": "Purple" }]""");
        Assert.AreEqual(Flags.Purple35, _sessionContext.SessionState.CurrentFlag);

        // Flagtronics no longer purple: fall back to the RMonitor Yellow.
        Process("""[{ "carNumber": "42", "pitActive": false, "fullCourseFlag": "FCYellow" }]""");
        Assert.AreEqual(Flags.Yellow, _sessionContext.SessionState.CurrentFlag);
    }

    [TestMethod]
    public void Heartbeat_AppliesRMonitorFlag_WithoutFlagtronics()
    {
        var update = HeartbeatUpdate(_sessionContext, "Green ");
        var patch = update.GetChanges(_sessionContext.SessionState);

        Assert.IsNotNull(patch);
        Assert.AreEqual(Flags.Green, patch.CurrentFlag);
        Assert.AreEqual(14, patch.LapsToGo);
    }

    [TestMethod]
    public void Heartbeat_UpgradesYellowToPurple_WhileFlagtronicsPurple()
    {
        _sessionContext.FlagtronicsFullCourseFlag = Flags.Purple35;

        var update = HeartbeatUpdate(_sessionContext, "Yellow");
        var patch = update.GetChanges(_sessionContext.SessionState);

        // RMonitor Yellow heartbeat does not revert an active Purple override.
        Assert.IsNotNull(patch);
        Assert.AreEqual(Flags.Purple35, patch.CurrentFlag);
    }

    [TestMethod]
    public void Heartbeat_YellowStaysYellow_WhenFlagtronicsNotPurple()
    {
        _sessionContext.FlagtronicsFullCourseFlag = Flags.Yellow;

        var update = HeartbeatUpdate(_sessionContext, "Yellow");
        var patch = update.GetChanges(_sessionContext.SessionState);

        Assert.IsNotNull(patch);
        Assert.AreEqual(Flags.Yellow, patch.CurrentFlag);
    }

    #endregion

    #region Reset re-apply

    [TestMethod]
    public void ProcessCar_ReappliesLastStateWithoutEdges()
    {
        ProcessSettled("""[{ "carNumber": "42", "pitActive": true, "speed": 30, "lat": 36.5, "lon": -121.7 }]""");

        // Simulate a timing reset clearing the car's state
        var resetCar = _sessionContext.GetCarByNumber("42")!;
        resetCar.IsInPit = false;
        resetCar.IsEnteredPit = false;
        resetCar.SpeedMph = null;
        resetCar.Latitude = null;
        resetCar.Longitude = null;

        var patch = _processor.ProcessCar("42");

        Assert.IsNotNull(patch);
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.IsInPit);
        Assert.IsFalse(car.IsEnteredPit); // no spurious edge on re-apply
        Assert.AreEqual(30, car.SpeedMph);
        Assert.AreEqual(36.5, car.Latitude);
    }

    [TestMethod]
    public void ProcessCar_NoDataForCar_ReturnsNull()
    {
        Assert.IsNull(_processor.ProcessCar("42"));
    }

    #endregion

    #region Real feed data (captured from api-dev1.flagtronics.com, 2026-07-20)

    [TestMethod]
    public void Process_RealVehicleRecord_MapsAllConsumedFields()
    {
        _sessionContext.UpdateCars([new CarPosition { Number = "23", TransponderId = 23 }]);

        // Car in an enforced pit zone: stopped speed sentinel, null localFlag (zone >= 128), pit stop in progress
        var result = ProcessSettled("""[{"carNumber": "23", "ft200DeviceId": 20003022, "class": ["B"], "teamName": "Team 23", "speed": 254, "lat": 36.5593572, "lon": -79.2102957, "carFlag": "Green", "localFlag": null, "fullCourseFlag": "Green", "flaggingZone": 130, "timingZone": 130, "driverId": 70000221, "driverName": "Driver 23-1", "driverSource": "blePuck", "currentLapNumber": 299, "lastLapTime": "00:02:08.000", "bestLapTime": "00:02:07.000", "pitEntryTime": "2026-07-20T17:00:01Z", "pitDuration": "00:03:02.000", "pitActive": true, "enforced": true, "speedViolation": false}]""");

        Assert.IsNotNull(result);
        var car = _sessionContext.GetCarByNumber("23")!;
        Assert.IsTrue(car.IsInPit);
        Assert.IsTrue(car.IsEnteredPit);
        Assert.AreEqual(0, car.SpeedMph); // 254 = stopped
        Assert.AreEqual(36.5593572, car.Latitude);
        Assert.AreEqual(-79.2102957, car.Longitude);
        Assert.IsTrue(car.PitSpeedEnforced);
        Assert.IsFalse(car.SpeedViolation);
        Assert.AreEqual(130, car.FlaggingZone);
        Assert.AreEqual(Flags.Green, car.LocalFlag);
        Assert.AreEqual("blePuck", car.DriverSource);
        Assert.AreEqual(new DateTime(2026, 7, 20, 17, 0, 1, DateTimeKind.Utc), car.PitEntryTime);
        Assert.AreEqual(182000, car.PitDurationMs);
        // Lap fields stay owned by the primary timing source
        Assert.AreEqual(0, car.LastLapCompleted);
        Assert.IsNull(car.BestTime);
    }

    #endregion

    #region X2 pit precedence

    [TestMethod]
    public async Task X2PitProcessor_Suppressed_WhenFlagtronicsPitActive()
    {
        var mockDbContextFactory = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(mockDbContextFactory.Object, _mockLoggerFactory.Object, _sessionContext);

        Process("""[{ "carNumber": "42", "pitActive": true }]""");
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitActive);

        var x2Message = new TimingMessage(Backend.Shared.Consts.X2PASS_TYPE, "[]", 1, DateTime.UtcNow);
        var result = await pitProcessor.Process(x2Message);
        Assert.IsNull(result);

        Assert.IsNull(pitProcessor.ProcessCar("42"));
    }

    #endregion
}
