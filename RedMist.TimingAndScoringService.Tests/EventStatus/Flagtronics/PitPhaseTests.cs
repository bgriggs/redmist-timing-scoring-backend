using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.Flagtronics;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.Flagtronics;

/// <summary>
/// The pit visit sequence from in-car telemetry: entered, stopped in the box, exiting, exited.
/// Zones follow what recorded feeds show: 144 is the pit entry road, 128 the speed-enforced lane,
/// 152 the pit exit road, 160 the paddock.
/// </summary>
[TestClass]
public class PitPhaseTests
{
    private FlagtronicsProcessor _processor = null!;
    private SessionContext _sessionContext = null!;
    private FakeTimeProvider _time = null!;
    private CarPosition _car = null!;

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");

        _time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 13, 50, 0, TimeSpan.Zero));
        _sessionContext = new SessionContext(config, new TestDbContextFactory(optionsBuilder.Options), loggerFactory.Object,
            new Mock<ICarLapHistoryService>().Object, _time);
        _sessionContext.UpdateCars(
            [new CarPosition { Number = "42", TransponderId = 42, LastLapCompleted = 10, SignalBars = CarPosition.MaxSignalBars }]);
        _car = _sessionContext.GetCarByNumber("42")!;

        _processor = new FlagtronicsProcessor(loggerFactory.Object, _sessionContext, _time);
    }

    private PatchUpdates? Tick(int zone, int speed, double seconds = 1)
    {
        _time.Advance(TimeSpan.FromSeconds(seconds));
        _sessionContext.UpdatePitOwnership();
        return _processor.Process(new TimingMessage(Backend.Shared.Consts.FLAGTRONICS_TYPE,
            $$"""[{ "carNumber": "42", "flaggingZone": {{zone}}, "speed": {{speed}}, "lat": 27.45, "lon": -81.35 }]""",
            1, DateTime.UtcNow));
    }

    /// <summary>Repeats a reading once a second for the given time, returning the last result.</summary>
    private PatchUpdates? Hold(int zone, int speed, int seconds)
    {
        PatchUpdates? last = null;
        for (int i = 0; i < seconds; i++)
            last = Tick(zone, speed);
        return last;
    }

    /// <summary>Out through the exit road to the track, until the visit has ended.</summary>
    private void DriveOut()
    {
        Hold(zone: 152, speed: 50, seconds: 2);
        Hold(zone: 1, speed: 70, seconds: 9);
        Assert.IsFalse(_car.IsInPit, "precondition: the visit has ended");
    }

    private void DriveIntoLane()
    {
        Hold(zone: 5, speed: 100, seconds: 3);
        Hold(zone: 144, speed: 50, seconds: 3);
        // Past the confirm window, so the visit is committed.
        Hold(zone: 128, speed: 35, seconds: 8);
        Assert.IsTrue(_car.IsInPit || !_sessionContext.IsFlagtronicsPitTrusted("42"), "precondition: the car is shown in the pit");
    }

    [TestMethod]
    public void FullStop_RunsEnteredBoxExitingExited()
    {
        DriveIntoLane();
        Assert.AreEqual(PitPhase.EnteredPit, _car.PitPhase);
        Assert.IsTrue(_car.IsEnteredPit);

        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 254, seconds: 4);
        Assert.AreEqual(PitPhase.EnteredPit, _car.PitPhase, "not stopped long enough yet");

        Hold(zone: 128, speed: 254, seconds: 30);
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.IsFalse(_car.IsEnteredPit);
        Assert.IsTrue(_car.IsInPit);
        Assert.AreEqual(stoppedAt.UtcDateTime, _car.PitStopStartTime);

        var movedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Tick(zone: 128, speed: 20);
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase, "one moving reading can be a GPS jump");
        Tick(zone: 128, speed: 30);
        Assert.AreEqual(PitPhase.ExitingPit, _car.PitPhase);
        Assert.IsNull(_car.LastPitStopDurationMs, "reported when the visit ends");
        Assert.IsTrue(_car.IsInPit);

        Hold(zone: 152, speed: 50, seconds: 3);
        Assert.AreEqual(PitPhase.ExitingPit, _car.PitPhase);

        Hold(zone: 1, speed: 70, seconds: 9);
        Assert.IsFalse(_car.IsInPit);
        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);
        Assert.IsTrue(_car.IsExitedPit);
        Assert.AreEqual((int)(movedAt - stoppedAt).TotalMilliseconds, _car.LastPitStopDurationMs);

        Hold(zone: 1, speed: 90, seconds: 5);
        Assert.AreEqual(PitPhase.None, _car.PitPhase);
        Assert.IsFalse(_car.IsExitedPit);
        Assert.AreEqual((int)(movedAt - stoppedAt).TotalMilliseconds, _car.LastPitStopDurationMs, "the last stop is kept");
    }

    [TestMethod]
    public void SlowingForTraffic_DoesNotCountAsAStop()
    {
        DriveIntoLane();

        Hold(zone: 128, speed: 2, seconds: 3);
        Hold(zone: 128, speed: 30, seconds: 3);

        Assert.AreEqual(PitPhase.EnteredPit, _car.PitPhase);
        Assert.IsNull(_car.PitStopStartTime);
    }

    [TestMethod]
    public void DriveThrough_GoesStraightToExitingThenExited()
    {
        DriveIntoLane();

        Hold(zone: 128, speed: 35, seconds: 10);
        Tick(zone: 152, speed: 45);
        Tick(zone: 152, speed: 48);

        Assert.AreEqual(PitPhase.ExitingPit, _car.PitPhase);
        Assert.IsNull(_car.LastPitStopDurationMs, "no stop was made");
        Assert.IsFalse(_car.IsEnteredPit);

        Hold(zone: 1, speed: 70, seconds: 9);
        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);
        Assert.IsNull(_car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void StopBeforeTheVisitIsConfirmed_IsTimedFromWhenTheCarStopped()
    {
        // A box just past pit-in: the car is stopped before the debounced visit begins.
        Hold(zone: 5, speed: 100, seconds: 3);
        Tick(zone: 144, speed: 50);
        Tick(zone: 128, speed: 30);
        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 254, seconds: 8);

        Assert.IsTrue(_car.IsInPit);
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.AreEqual(stoppedAt.UtcDateTime, _car.PitStopStartTime);
    }

    [TestMethod]
    public void LongStandstillBeforeTheVisit_IsNotTimed()
    {
        // Seen moving, then stopped on track beside the lane for minutes, then tagged into the pit.
        Hold(zone: 5, speed: 100, seconds: 3);
        Hold(zone: 5, speed: 0, seconds: 120);
        Hold(zone: 128, speed: 0, seconds: 9);

        // Stopped since before it was in the pit, so when its stop began is unknown: shown in the
        // pit, with no clock that would claim minutes in a box.
        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);
        Assert.IsNull(_car.PitStopStartTime);
    }

    [TestMethod]
    public void QueuedInTheLaneAfterTheBox_ShowsStoppedAndKeepsTheLongerStop()
    {
        // A closed pit exit under a full course yellow: the queue is in the enforced lane.
        DriveIntoLane();
        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 254, seconds: 40);
        var movedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 30, seconds: 3);

        Hold(zone: 128, speed: 0, seconds: 20);
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);

        Hold(zone: 128, speed: 30, seconds: 3);
        DriveOut();
        Assert.AreEqual((int)(movedAt - stoppedAt).TotalMilliseconds, _car.LastPitStopDurationMs, "the queue was shorter");
    }

    [TestMethod]
    public void HeldInAJammedLaneBeforeTheBox_ReportsTheBoxStop()
    {
        // Under a full course yellow the lane jams: held 26 s short of the box, then serviced.
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 26);
        Hold(zone: 128, speed: 8, seconds: 3);
        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 254, seconds: 120);
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.AreEqual(stoppedAt.UtcDateTime, _car.PitStopStartTime);

        var movedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 30, seconds: 3);
        DriveOut();

        Assert.AreEqual((int)(movedAt - stoppedAt).TotalMilliseconds, _car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void ParkedFixesWanderingOntoTheTrack_DoNotRestartTheStop()
    {
        // Observed live: a parked car's fixes alternate between its pit zone and the track zone
        // beside it, long enough to end the debounced visit while it never moves.
        DriveIntoLane();
        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 254, seconds: 20);
        Hold(zone: 18, speed: 254, seconds: 10);
        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);

        Hold(zone: 128, speed: 254, seconds: 10);
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.AreEqual(stoppedAt.UtcDateTime, _car.PitStopStartTime);

        var movedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 30, seconds: 3);
        DriveOut();
        Assert.AreEqual((int)(movedAt - stoppedAt).TotalMilliseconds, _car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void ParkedOnTheExitRoadWithoutStopping_IsInPitRatherThanEntering()
    {
        DriveIntoLane();
        Hold(zone: 152, speed: 0, seconds: 20);

        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);
        Assert.IsTrue(_car.IsInPit);
        Assert.IsFalse(_car.IsEnteredPit);
    }

    [TestMethod]
    public void DeviceWithNoSpeed_IsInPitRatherThanEnteringThroughout()
    {
        for (int i = 0; i < 40; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _sessionContext.UpdatePitOwnership();
            _processor.Process(new TimingMessage(Backend.Shared.Consts.FLAGTRONICS_TYPE,
                """[{ "carNumber": "42", "flaggingZone": 128, "lat": 27.45, "lon": -81.35 }]""", 1, DateTime.UtcNow));
        }

        Assert.IsTrue(_car.IsInPit);
        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);
    }

    [TestMethod]
    public void DuplicateRecordsInOneMessage_CountAsOneMovingReading()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);

        _time.Advance(TimeSpan.FromSeconds(1));
        _sessionContext.UpdatePitOwnership();
        _processor.Process(new TimingMessage(Backend.Shared.Consts.FLAGTRONICS_TYPE,
            """[{ "carNumber": "42", "flaggingZone": 128, "speed": 20, "lat": 27.45, "lon": -81.35 }, { "carNumber": "42", "flaggingZone": 128, "speed": 21, "lat": 27.45, "lon": -81.35 }]""", 1, DateTime.UtcNow));

        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
    }

    [TestMethod]
    public void DepartureHiddenByAGap_IsNotTimed()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);

        // Silent for a minute, next seen already moving.
        _time.Advance(TimeSpan.FromSeconds(60));
        Hold(zone: 128, speed: 30, seconds: 3);

        Assert.AreEqual(PitPhase.ExitingPit, _car.PitPhase);
        DriveOut();
        Assert.IsNull(_car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void GapInTheMiddleOfAStop_KeepsItsTiming()
    {
        DriveIntoLane();
        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 254, seconds: 10);

        // A parked device reporting rarely: still stopped either side of the gap.
        _time.Advance(TimeSpan.FromSeconds(40));
        Hold(zone: 128, speed: 254, seconds: 5);
        var movedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 30, seconds: 3);
        DriveOut();

        Assert.AreEqual((int)(movedAt - stoppedAt).TotalMilliseconds, _car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void NewSession_DoesNotExpireTheOldSessionsPhases()
    {
        DriveIntoLane();
        Hold(zone: 1, speed: 70, seconds: 9);
        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);

        _sessionContext.SessionState.SessionId++;
        _time.Advance(PitPhaseTracker.ExitedHold);

        Assert.AreEqual(0, _processor.ExpireExitedPitPhases().Count);
    }

    [TestMethod]
    public void PushedBackToGarage_ReturnsToBoxKeepingTheFirstStopStart()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);
        var firstStop = _car.PitStopStartTime;
        Hold(zone: 128, speed: 10, seconds: 3);
        Assert.AreEqual(PitPhase.ExitingPit, _car.PitPhase);

        Hold(zone: 160, speed: 0, seconds: 10);
        Assert.AreEqual(PitPhase.ExitingPit, _car.PitPhase, "a short pause is not a new stop");

        Hold(zone: 160, speed: 0, seconds: 10);
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.AreEqual(firstStop, _car.PitStopStartTime);
    }

    [TestMethod]
    public void QueuedAtThePitExit_StaysExiting()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);
        Hold(zone: 128, speed: 30, seconds: 3);

        Hold(zone: 152, speed: 0, seconds: 60);

        Assert.AreEqual(PitPhase.ExitingPit, _car.PitPhase);
    }

    [TestMethod]
    public void BadGpsSpeed_HoldsThePhase()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);

        Hold(zone: 128, speed: 255, seconds: 5);

        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
    }

    [TestMethod]
    public void ConfirmingTheStop_ResendsInPitForClientsThatOnlyMoveOnIt()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 5);

        // The reading that confirms the stop clears the entered flag; the in-pit flag rides along
        // unchanged so badges keyed on it move from "entered" to "pit".
        var result = Tick(zone: 128, speed: 254);

        var patch = result!.CarPatches.Single();
        Assert.AreEqual(PitPhase.InPitBox, patch.PitPhase);
        Assert.AreEqual(false, patch.IsEnteredPit);
        Assert.AreEqual(true, patch.IsInPit);
    }

    [TestMethod]
    public void ExitedHold_ExpiresWithoutFurtherRecords()
    {
        DriveIntoLane();
        Hold(zone: 1, speed: 70, seconds: 9);
        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);

        _time.Advance(PitPhaseTracker.ExitedHold);
        var patches = _processor.ExpireExitedPitPhases();

        var patch = patches.Single();
        Assert.AreEqual(PitPhase.None, patch.PitPhase);
        Assert.AreEqual(false, patch.IsExitedPit);
        Assert.AreEqual(false, patch.IsInPit, "resent for clients that only clear the badge on it");
        Assert.AreEqual(PitPhase.None, _car.PitPhase);
        Assert.AreEqual(0, _processor.ExpireExitedPitPhases().Count);
    }

    [TestMethod]
    public void UntrustedDevice_PublishesNoPhase()
    {
        _car.SignalBars = 1;

        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);

        Assert.AreEqual(PitPhase.None, _car.PitPhase, "X2 owns this car's pit state");
    }

    [TestMethod]
    public void HandedBackFromX2_PicksUpTheRealPhase()
    {
        // While X2 owns the car the visit is still followed, just not published.
        _car.SignalBars = 1;
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);
        Assert.AreEqual(PitPhase.None, _car.PitPhase);

        _car.SignalBars = CarPosition.MaxSignalBars;
        Tick(zone: 128, speed: 254);

        // In the pit, but a stop timed from untrusted readings - a frozen GPS repeating a
        // standstill looks just like one - is not shown with a time, nor reported when it ends.
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("42"));
        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);
        Assert.IsTrue(_car.IsInPit);
        Hold(zone: 128, speed: 30, seconds: 3);
        Assert.AreEqual(PitPhase.ExitingPit, _car.PitPhase);
        DriveOut();
        Assert.IsNull(_car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void LapCompletedWithStuckFlag_EndsTheVisit()
    {
        // No GPS: the stuck in-car flag holds the car in the pit until a lap proves otherwise.
        for (int i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _sessionContext.UpdatePitOwnership();
            _processor.Process(new TimingMessage(Backend.Shared.Consts.FLAGTRONICS_TYPE,
                """[{ "carNumber": "42", "pitActive": true }]""", 1, DateTime.UtcNow));
        }
        Assert.AreEqual(PitPhase.EnteredPit, _car.PitPhase);

        var patch = _processor.NotifyLapCompleted("42");

        Assert.IsNotNull(patch);
        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);
        Assert.IsFalse(_car.IsInPit);
    }

    /// <summary>
    /// One car's stop at WRL Sebring, session 19, record by record: down the entry road (144) into
    /// the lane (128), stopped in the box, pushed into the garage (160), back out through the lane
    /// and the exit road (152) to the track. Its in-car pitActive flag dropped while it was still
    /// stopped in the box, which is why the sequence does not use it.
    /// </summary>
    private static readonly (string Time, int Zone, int Speed, bool PitActive)[] SebringStop =
    [
        ("13:56:39", 17, 67, false),
        ("13:56:41", 144, 64, false),
        ("13:56:43", 144, 58, false),
        ("13:56:45", 128, 30, false),
        ("13:56:46", 128, 32, true),
        ("13:56:47", 128, 32, true),
        ("13:56:48", 128, 32, true),
        ("13:56:49", 128, 33, true),
        ("13:56:50", 128, 33, true),
        ("13:56:51", 128, 33, true),
        ("13:56:52", 128, 33, true),
        ("13:56:53", 128, 24, true),
        ("13:56:54", 128, 24, true),
        ("13:56:55", 128, 24, true),
        ("13:56:56", 128, 24, true),
        ("13:56:57", 128, 31, true),
        ("13:56:58", 128, 28, true),
        ("13:56:59", 128, 28, true),
        ("13:57:00", 128, 15, true),
        ("13:57:01", 128, 0, true),
        ("13:57:02", 128, 0, false),
        ("13:57:13", 128, 0, false),
        ("13:57:14", 128, 0, false),
        ("13:57:20", 128, 0, false),
        ("13:57:36", 128, 0, false),
        ("13:57:47", 128, 0, false),
        ("13:58:00", 128, 0, false),
        ("13:58:21", 128, 0, false),
        ("13:58:22", 160, 0, false),
        ("13:58:33", 160, 0, false),
        ("13:58:34", 160, 0, false),
        ("13:58:45", 160, 0, false),
        ("13:58:46", 160, 0, false),
        ("13:59:19", 160, 0, false),
        ("13:59:20", 160, 0, false),
        ("13:59:21", 160, 0, false),
        ("13:59:33", 128, 27, false),
        ("13:59:34", 128, 27, false),
        ("13:59:35", 128, 28, false),
        ("13:59:36", 128, 31, false),
        ("13:59:37", 128, 35, false),
        ("13:59:38", 128, 36, false),
        ("13:59:39", 128, 36, false),
        ("13:59:40", 128, 36, false),
        ("13:59:42", 128, 36, false),
        ("13:59:43", 128, 36, false),
        ("13:59:44", 128, 36, false),
        ("13:59:45", 152, 36, false),
        ("13:59:46", 152, 38, false),
        ("13:59:47", 152, 47, false),
        ("13:59:48", 152, 60, false),
        ("13:59:50", 0, 77, false),
        ("13:59:54", 1, 56, false),
        ("13:59:55", 1, 61, false),
        ("13:59:59", 1, 91, false),
        ("14:00:00", 1, 95, false),
        ("14:00:03", 2, 97, false),
        ("14:00:04", 2, 83, false),
        ("14:00:07", 2, 54, false),
        ("14:00:09", 3, 64, false),
        ("14:00:10", 3, 70, false),
    ];

    [TestMethod]
    public void RecordedSebringStop_FollowsTheVisit()
    {
        var phases = new List<(string Time, PitPhase Phase)>();
        foreach (var (time, zone, speed, pitActive) in SebringStop)
        {
            _time.SetUtcNow(new DateTimeOffset(DateTime.Parse($"2026-10-17T{time}Z").ToUniversalTime()));
            _sessionContext.UpdatePitOwnership();
            _processor.Process(new TimingMessage(Backend.Shared.Consts.FLAGTRONICS_TYPE,
                $$"""[{ "carNumber": "42", "flaggingZone": {{zone}}, "speed": {{speed}}, "pitActive": {{(pitActive ? "true" : "false")}}, "lat": 27.45, "lon": -81.35 }]""",
                1, DateTime.UtcNow));
            if (phases.Count == 0 || phases[^1].Phase != _car.PitPhase)
                phases.Add((time, _car.PitPhase));
        }

        CollectionAssert.AreEqual(new[]
        {
            ("13:56:39", PitPhase.None),
            ("13:56:49", PitPhase.EnteredPit),
            ("13:57:13", PitPhase.InPitBox),
            ("13:59:34", PitPhase.ExitingPit),
            ("13:59:59", PitPhase.ExitedPit),
            ("14:00:04", PitPhase.None),
        }, phases, string.Join(", ", phases));
        Assert.AreEqual(new DateTime(2026, 10, 17, 13, 57, 1, DateTimeKind.Utc), _car.PitStopStartTime);
        Assert.AreEqual(152_000, _car.LastPitStopDurationMs);
    }

    private void Record(string json)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        _sessionContext.UpdatePitOwnership();
        _processor.Process(new TimingMessage(Backend.Shared.Consts.FLAGTRONICS_TYPE, json, 1, DateTime.UtcNow));
    }

    [TestMethod]
    public void PreRaceStaging_IsShownInThePitButNotTimed()
    {
        // The field waits in the paddock before the start, never yet seen on track.
        Hold(zone: 160, speed: 254, seconds: 60);
        Assert.IsTrue(_car.IsInPit);
        Assert.AreEqual(PitPhase.InPit, _car.PitPhase, "no box timer counting the wait");

        Hold(zone: 160, speed: 20, seconds: 3);
        Hold(zone: 128, speed: 35, seconds: 5);
        Hold(zone: 152, speed: 50, seconds: 3);
        Hold(zone: 1, speed: 70, seconds: 9);

        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);
        Assert.IsNull(_car.LastPitStopDurationMs, "the wait for the start is not a pit stop");
    }

    [TestMethod]
    public void BadGpsStretchBeforeDeparture_IsNotTimed()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);

        // No speed for a minute - a car pulling out from under a canopy - then seen moving.
        Hold(zone: 128, speed: 255, seconds: 60);
        Hold(zone: 128, speed: 30, seconds: 3);

        Assert.AreEqual(PitPhase.ExitingPit, _car.PitPhase);
        DriveOut();
        Assert.IsNull(_car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void GpsLostOnTheWayToTheBox_IsShownInThePitRatherThanEntering()
    {
        DriveIntoLane();

        Hold(zone: 128, speed: 255, seconds: 25);

        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);
        Assert.IsFalse(_car.IsEnteredPit);
        Assert.IsTrue(_car.IsInPit);
    }

    [TestMethod]
    public void VisitEndingWhileStillInTheBox_IsNotTimed()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 30);

        // The departure is never seen moving: next heard from on track, with no speed.
        Hold(zone: 1, speed: 255, seconds: 9);

        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);
        Assert.IsNull(_car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void LoneJumpAfterAQuietSpell_DoesNotLoseTheStop()
    {
        DriveIntoLane();
        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 254, seconds: 10);

        // A parked device quiet for 33 s, then one GPS jump, then stopped again for a minute.
        _time.Advance(TimeSpan.FromSeconds(33));
        Tick(zone: 128, speed: 8);
        Hold(zone: 128, speed: 254, seconds: 60);
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase, "still timed, so still shown with its clock");

        var movedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 30, seconds: 3);
        DriveOut();

        Assert.AreEqual((int)(movedAt - stoppedAt).TotalMilliseconds, _car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void SparseReportsFromABoxNearPitIn_TimeTheStopFromWhenTheCarStopped()
    {
        Hold(zone: 5, speed: 100, seconds: 3);
        Tick(zone: 128, speed: 30);
        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(2);
        Tick(zone: 128, speed: 254, seconds: 2);

        // A parked device reporting every 15 s: the visit commits well after the car stopped.
        for (int i = 0; i < 4; i++)
            Tick(zone: 128, speed: 254, seconds: 15);

        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.AreEqual(stoppedAt.UtcDateTime, _car.PitStopStartTime);
    }

    [TestMethod]
    public void SecondStopAfterAnUntimedOne_IsTimed()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);
        _time.Advance(TimeSpan.FromSeconds(60));
        Hold(zone: 128, speed: 30, seconds: 3);
        Hold(zone: 1, speed: 70, seconds: 15);
        Assert.AreEqual(PitPhase.None, _car.PitPhase);
        Assert.IsNull(_car.LastPitStopDurationMs, "precondition: the first stop was not timed");

        Hold(zone: 1, speed: 100, seconds: 60);
        DriveIntoLane();
        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 254, seconds: 20);
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.AreEqual(stoppedAt.UtcDateTime, _car.PitStopStartTime);

        var movedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 30, seconds: 3);
        DriveOut();
        Assert.AreEqual((int)(movedAt - stoppedAt).TotalMilliseconds, _car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void ReplayAfterAReset_RestoresTheBoxAndItsClock()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);
        var stopStart = _car.PitStopStartTime;
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);

        // A timing reset recreates the car with nothing set.
        _car.IsInPit = false;
        _car.PitPhase = PitPhase.None;
        _car.PitStopStartTime = null;

        var patch = _processor.ProcessCar("42");

        Assert.IsNotNull(patch);
        Assert.IsTrue(_car.IsInPit);
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.AreEqual(stopStart, _car.PitStopStartTime);
    }

    [TestMethod]
    public void ExitedHoldForACarX2Owns_IsNotPublished()
    {
        _car.SignalBars = 1;
        DriveIntoLane();
        Hold(zone: 1, speed: 70, seconds: 9);

        // X2 has the car in the pit; the untrusted device's view must not overwrite that.
        _car.IsInPit = true;
        _car.PitPhase = PitPhase.InPit;
        _time.Advance(PitPhaseTracker.ExitedHold);

        Assert.AreEqual(0, _processor.ExpireExitedPitPhases().Count);
        Assert.IsTrue(_car.IsInPit);
        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);
    }

    [TestMethod]
    public void NewSession_ForgetsTheLastSessionsStops()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);
        Hold(zone: 128, speed: 30, seconds: 3);
        DriveOut();
        Assert.IsNotNull(_car.LastPitStopDurationMs);

        _sessionContext.SessionState.SessionId++;
        _car.LastPitStopDurationMs = null;
        _car.PitPhase = PitPhase.None;
        _car.IsInPit = false;
        Tick(zone: 5, speed: 100);

        Assert.IsNull(_car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void TakenBackFromX2_ClearsTheLoopOnlyStartFinishFlag()
    {
        _car.IsPitStartFinish = true;

        Tick(zone: 5, speed: 100);

        Assert.IsFalse(_car.IsPitStartFinish);
    }

    [TestMethod]
    public void BarsDroppingOutOfThePit_HandsTheCarToX2Once()
    {
        Tick(zone: 5, speed: 100);
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("42"));

        _car.SignalBars = 1;
        _sessionContext.UpdatePitOwnership();

        CollectionAssert.AreEqual(new[] { "42" }, _sessionContext.TakeCarsHandedToX2());
        Assert.AreEqual(0, _sessionContext.TakeCarsHandedToX2().Count);
    }

    [TestMethod]
    public void ArrivalHiddenByAGap_IsShownWithoutAClockAndNotTimed()
    {
        DriveIntoLane();

        // Silent while pulling into the box, next heard from already stopped.
        _time.Advance(TimeSpan.FromSeconds(45));
        Hold(zone: 128, speed: 254, seconds: 30);
        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);

        Hold(zone: 128, speed: 30, seconds: 3);
        Assert.AreEqual(PitPhase.ExitingPit, _car.PitPhase);
        DriveOut();
        Assert.IsNull(_car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void ArrivalDuringABadGpsStretch_IsNotTimed()
    {
        DriveIntoLane();

        Hold(zone: 128, speed: 255, seconds: 40);
        Hold(zone: 128, speed: 254, seconds: 30);
        Hold(zone: 128, speed: 30, seconds: 3);
        DriveOut();

        Assert.IsNull(_car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void QuietSpellBeforeTheStopIsConfirmed_KeepsWhenItStopped()
    {
        DriveIntoLane();
        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Tick(zone: 128, speed: 254);

        // Parked devices report rarely; the stop is confirmed afresh, but began when first seen.
        Tick(zone: 128, speed: 254, seconds: 33);
        Assert.AreEqual(PitPhase.EnteredPit, _car.PitPhase, "confirmation restarts after the gap");
        Hold(zone: 128, speed: 254, seconds: 10);

        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.AreEqual(stoppedAt.UtcDateTime, _car.PitStopStartTime);
    }

    [TestMethod]
    public void QueuedOnTheEntryRoad_IsNotABoxStop()
    {
        Hold(zone: 5, speed: 100, seconds: 3);
        Hold(zone: 144, speed: 40, seconds: 3);
        Hold(zone: 144, speed: 254, seconds: 10);
        Assert.AreEqual(PitPhase.EnteredPit, _car.PitPhase);

        Hold(zone: 128, speed: 30, seconds: 5);
        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 254, seconds: 60);

        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.AreEqual(stoppedAt.UtcDateTime, _car.PitStopStartTime);
    }

    [TestMethod]
    public void HeldByTrafficInTheLane_TheRealStopReplacesIt()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 7);
        Hold(zone: 128, speed: 20, seconds: 5);
        Assert.AreEqual(PitPhase.ExitingPit, _car.PitPhase);

        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 254, seconds: 60);
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.AreEqual(stoppedAt.UtcDateTime, _car.PitStopStartTime);

        var movedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 30, seconds: 3);
        DriveOut();
        Assert.AreEqual((int)(movedAt - stoppedAt).TotalMilliseconds, _car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void LatchedPitActiveWhileWaitingForTheStart_IsNotTimed()
    {
        for (int i = 0; i < 60; i++)
            Record("""[{ "carNumber": "42", "flaggingZone": 160, "speed": 254, "pitActive": true, "lat": 27.45, "lon": -81.35 }]""");

        Assert.IsTrue(_car.IsInPit);
        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);
    }

    [TestMethod]
    public void ShortBadGpsStretchAtTheDeparture_IsNotTimed()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 30);

        Hold(zone: 128, speed: 255, seconds: 10);
        Hold(zone: 128, speed: 30, seconds: 3);

        Assert.AreEqual(PitPhase.ExitingPit, _car.PitPhase);
        DriveOut();
        Assert.IsNull(_car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void JumpsAtAParkedDevicesPace_DoNotTakeTheCarOutOfTheBox()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);

        Tick(zone: 128, speed: 8, seconds: 15);
        Tick(zone: 128, speed: 9, seconds: 15);

        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
    }

    [TestMethod]
    public void OneExitRoadReadingWhileEntering_IsNotADriveThrough()
    {
        DriveIntoLane();
        Tick(zone: 152, speed: 30);
        Hold(zone: 128, speed: 30, seconds: 2);
        Hold(zone: 128, speed: 254, seconds: 10);

        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
    }

    [TestMethod]
    public void Replay_DoesNotAdvanceThePhase()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 3);
        Assert.AreEqual(PitPhase.EnteredPit, _car.PitPhase);

        // A stopped record replayed after resets must not count as the car staying stopped.
        for (int i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _processor.ProcessCar("42");
        }

        Assert.AreEqual(PitPhase.EnteredPit, _car.PitPhase);
    }

    [TestMethod]
    public void UnderMultiloop_TheStartFinishFlagIsLeftToIt()
    {
        _sessionContext.IsMultiloopActive = true;
        _car.IsPitStartFinish = true;

        Tick(zone: 5, speed: 100);

        Assert.IsTrue(_car.IsPitStartFinish);
    }

    [TestMethod]
    public void HandedToX2ThenRenumbered_IsReportedUnderTheNewNumber()
    {
        Tick(zone: 5, speed: 100);
        _car.SignalBars = 1;
        _sessionContext.UpdatePitOwnership();

        _sessionContext.RenumberCar("42", "42x");

        CollectionAssert.AreEqual(new[] { "42x" }, _sessionContext.TakeCarsHandedToX2());
    }

    [TestMethod]
    public void TimedHoldThenABoxStopTheVisitEndsIn_ReportsNothing()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 10);
        Hold(zone: 128, speed: 20, seconds: 3);
        Hold(zone: 128, speed: 254, seconds: 30);

        // Gone from the box without a moving reading: that stop's length is unknown.
        Hold(zone: 1, speed: 255, seconds: 9);

        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);
        Assert.IsNull(_car.LastPitStopDurationMs, "the timed hold is not the visit's stop");
    }

    [TestMethod]
    public void LongerUntimedStop_KeepsTheShorterTimedOneFromBeingReported()
    {
        DriveIntoLane();
        Hold(zone: 128, speed: 254, seconds: 30);
        Hold(zone: 128, speed: 20, seconds: 3);

        // The box: arrival hidden by bad GPS, so untimed, but plainly longer than the hold.
        Hold(zone: 128, speed: 255, seconds: 40);
        Hold(zone: 128, speed: 254, seconds: 20);
        Hold(zone: 128, speed: 30, seconds: 3);
        DriveOut();

        Assert.IsNull(_car.LastPitStopDurationMs);
    }

    [TestMethod]
    public void StrayMovingReadingWhileTheVisitWasSplit_StillResumesTheStop()
    {
        DriveIntoLane();
        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 254, seconds: 20);
        Hold(zone: 18, speed: 254, seconds: 10);
        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);

        Tick(zone: 128, speed: 8);
        Hold(zone: 128, speed: 254, seconds: 10);
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.AreEqual(stoppedAt.UtcDateTime, _car.PitStopStartTime);
    }

    [TestMethod]
    public void BoxNearPitInWithAWanderingFix_KeepsWhenTheCarStopped()
    {
        Hold(zone: 5, speed: 100, seconds: 3);
        Hold(zone: 144, speed: 50, seconds: 2);
        Hold(zone: 128, speed: 30, seconds: 2);
        var stoppedAt = _time.GetUtcNow() + TimeSpan.FromSeconds(1);
        Hold(zone: 128, speed: 254, seconds: 2);

        // Before the entry is confirmed, one fix on the track zone beside the box restarts it.
        Tick(zone: 18, speed: 254);
        for (int i = 0; i < 3; i++)
            Tick(zone: 128, speed: 254, seconds: 15);

        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
        Assert.AreEqual(stoppedAt.UtcDateTime, _car.PitStopStartTime);
    }
}
