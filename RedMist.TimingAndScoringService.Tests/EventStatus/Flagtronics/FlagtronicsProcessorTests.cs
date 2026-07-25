using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

        _processor = new FlagtronicsProcessor(_mockLoggerFactory.Object, _sessionContext);
    }

    private static TimingMessage FtMessage(string json) =>
        new(Backend.Shared.Consts.FLAGTRONICS_TYPE, json, 1, DateTime.UtcNow);

    private PatchUpdates? Process(string json) => _processor.Process(FtMessage(json));

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
        var result = Process("""[{ "carNumber": "42", "pitActive": true, "pitEntryTime": "2026-07-17T09:12:41Z", "pitDuration": "00:03:05.000" }]""");

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
        Process("""[{ "carNumber": "42", "pitActive": true }]""");
        Process("""[{ "carNumber": "42", "pitActive": true, "pitDuration": "00:00:30.000" }]""");

        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.IsInPit);
        Assert.IsFalse(car.IsEnteredPit);
    }

    [TestMethod]
    public void Process_PitExit_SetsExitedEdge()
    {
        Process("""[{ "carNumber": "42", "pitActive": true }]""");
        var result = Process("""[{ "carNumber": "42", "pitActive": false, "pitDuration": "00:02:00.500" }]""");

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
        Process("""[{ "carNumber": "42", "pitActive": true }]""");
        Process("""[{ "carNumber": "42", "pitActive": false }]""");

        var car = _sessionContext.GetCarByNumber("42")!;
        // Lap 11 not yet completed; current lap did not include the completed lap 10
        Assert.IsFalse(car.LapIncludedPit);

        // Complete lap 11 and process another update: lap 11 included the stop
        car.LastLapCompleted = 11;
        Process("""[{ "carNumber": "42", "pitActive": false, "speed": 50 }]""");
        Assert.IsTrue(car.LapIncludedPit);
    }

    [TestMethod]
    public void Process_StuckPitActive_ClearedByOnTrackZone()
    {
        // Car enters the pit (pit zone, pitActive true)
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 10 }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.IsInPit);

        // Device leaves pitActive latched true, but the car is back on the racing surface at
        // speed: the on-track flagging zone overrides the stuck flag and clears IsInPit.
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 5, "speed": 90 }]""");
        Assert.IsFalse(car.IsInPit);
        Assert.IsTrue(car.IsExitedPit);
    }

    [TestMethod]
    public void Process_PitZoneWithoutPitActive_IndicatesInPit()
    {
        // pitActive lags/misses entry, but the car is physically in the pit-entry lane zone:
        // location drives the indication so it is not off or late.
        Process("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 145, "speed": 30 }]""");
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
        Process("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 161, "speed": 0 }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.IsInPit);
        Assert.IsFalse(car.LapIncludedPit);

        // Once the car has run on track, a subsequent pit stop is tagged normally.
        Process("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 5, "speed": 80 }]""");
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 10 }]""");
        Assert.IsTrue(car.IsInPit);
        Assert.IsTrue(car.LapIncludedPit);
    }

    #endregion

    #region HasGps and no-GPS lap-completion fallback

    [TestMethod]
    public void Process_HasGps_TrueWithZone_FalseWithout()
    {
        Process("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 5, "speed": 40 }]""");
        Assert.IsTrue(_sessionContext.GetCarByNumber("42")!.HasGps);

        // Zone 0 and no position => no GPS fix.
        Process("""[{ "carNumber": "42", "pitActive": false, "flaggingZone": 0 }]""");
        Assert.IsFalse(_sessionContext.GetCarByNumber("42")!.HasGps);
    }

    [TestMethod]
    public void Process_HasGps_TrueFromLatLonWithoutZone()
    {
        Process("""[{ "carNumber": "42", "pitActive": false, "lat": 36.5, "lon": -121.7 }]""");
        Assert.IsTrue(_sessionContext.GetCarByNumber("42")!.HasGps);
    }

    [TestMethod]
    public void NotifyLapCompleted_NoGpsStuckPit_ClearsAndSuppresses()
    {
        // Car is in the pit with no GPS (zone 0) and a stuck pitActive.
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 0 }]""");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsTrue(car.IsInPit);

        // A completed lap proves it crossed S/F on track: clear the frozen pit state.
        var patch = _processor.NotifyLapCompleted("42");
        Assert.IsNotNull(patch);
        Assert.IsFalse(car.IsInPit);

        // The stuck flag stays suppressed: further no-GPS ticks do not re-set IsInPit.
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 0 }]""");
        Assert.IsFalse(car.IsInPit);
    }

    [TestMethod]
    public void NotifyLapCompleted_WithGps_NoOp()
    {
        // GPS present (pit zone): the zone is authoritative, so the fallback does nothing.
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 10 }]""");
        Assert.IsTrue(_sessionContext.GetCarByNumber("42")!.IsInPit);

        var patch = _processor.NotifyLapCompleted("42");
        Assert.IsNull(patch);
        Assert.IsTrue(_sessionContext.GetCarByNumber("42")!.IsInPit);
    }

    [TestMethod]
    public void NotifyLapCompleted_SuppressionClearedWhenGpsReturns()
    {
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 0 }]""");
        _processor.NotifyLapCompleted("42");
        var car = _sessionContext.GetCarByNumber("42")!;
        Assert.IsFalse(car.IsInPit);

        // GPS returns and shows a real pit zone: suppression clears and pit shows again.
        Process("""[{ "carNumber": "42", "pitActive": true, "flaggingZone": 129, "speed": 5 }]""");
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
        Process("""[{ "carNumber": "42", "pitActive": true, "speed": 30, "lat": 36.5, "lon": -121.7 }]""");

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
        var result = Process("""[{"carNumber": "23", "ft200DeviceId": 20003022, "class": ["B"], "teamName": "Team 23", "speed": 254, "lat": 36.5593572, "lon": -79.2102957, "carFlag": "Green", "localFlag": null, "fullCourseFlag": "Green", "flaggingZone": 130, "timingZone": 130, "driverId": 70000221, "driverName": "Driver 23-1", "driverSource": "blePuck", "currentLapNumber": 299, "lastLapTime": "00:02:08.000", "bestLapTime": "00:02:07.000", "pitEntryTime": "2026-07-20T17:00:01Z", "pitDuration": "00:03:02.000", "pitActive": true, "enforced": true, "speedViolation": false}]""");

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
