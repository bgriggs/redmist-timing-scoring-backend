using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.X2;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.X2;

/// <summary>
/// A car whose Flagtronics device keeps transmitting with its GPS frozen holds full signal bars,
/// so it keeps ownership of its pit state while showing the wrong place. X2 loop passings are the
/// independent evidence that hands such a car back to X2.
/// </summary>
[TestClass]
public class FlagtronicsPitContradictionTests
{
    private SessionContext _sessionContext = null!;
    private PitProcessor _pitProcessor = null!;
    private FakeTimeProvider _time = null!;
    private CarPosition _car = null!;

    private const double Lat = 27.4549;
    private const double Lon = -81.3502;

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
        var dbContextFactory = new TestDbContextFactory(optionsBuilder.Options);

        _time = new FakeTimeProvider();
        _sessionContext = new SessionContext(config, dbContextFactory, loggerFactory.Object,
            new Mock<ICarLapHistoryService>().Object, _time);
        _sessionContext.UpdateCars(
            [
            new CarPosition { Number = "77", TransponderId = 77, LastLapCompleted = 10,
                SignalBars = CarPosition.MaxSignalBars, Latitude = Lat, Longitude = Lon },
            ]);
        _car = _sessionContext.GetCarByNumber("77")!;

        _pitProcessor = new PitProcessor(new Mock<IDbContextFactory<TsContext>>().Object, loggerFactory.Object, _sessionContext);

        _sessionContext.UpdatePitOwnership();
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"), "precondition: Flagtronics owns the car");
    }

    /// <summary>Passing serializes with short names: t = transponder, l = loop, p = in pit.</summary>
    private Task Pass(uint loopId, bool inPit) =>
        _pitProcessor.Process(new TimingMessage(Backend.Shared.Consts.X2PASS_TYPE,
            $$"""[{ "t": 77, "l": {{loopId}}, "p": {{(inPit ? "true" : "false")}} }]""", 1, DateTime.UtcNow));

    /// <summary>Moves the clock in pipeline-sized steps, sweeping ownership as the pipeline does.</summary>
    private List<string> Advance(TimeSpan by, bool moveGps = false)
    {
        var demoted = new List<string>();
        var step = TimeSpan.FromSeconds(5);
        for (var elapsed = TimeSpan.Zero; elapsed < by; elapsed += step)
        {
            _time.Advance(step);
            if (moveGps)
                _car.Latitude += 0.0001;
            demoted.AddRange(_sessionContext.UpdatePitOwnership());
        }
        return demoted;
    }

    [TestMethod]
    public async Task FrozenGpsOnTrack_CrossingPitLoop_HandsCarToX2InPit()
    {
        // GPS frozen on track for a while before the stop.
        Advance(TimeSpan.FromSeconds(30));

        await Pass(loopId: 1, inPit: true);
        Assert.IsFalse(_car.IsInPit, "Flagtronics still shows the car on track");

        var demoted = Advance(SessionContext.PitContradictionGrace + TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { "77" }, demoted);
        Assert.IsFalse(_sessionContext.IsFlagtronicsPitTrusted("77"));
        var patch = _pitProcessor.ProcessCar("77");
        Assert.IsNotNull(patch);
        Assert.IsTrue(_car.IsInPit, "X2 shows the stop");
    }

    [TestMethod]
    public async Task MovingGps_PitLoopCrossRead_KeepsFlagtronics()
    {
        // A strong transponder on track read by the pit loop beside it, from a live device.
        await Pass(loopId: 1, inPit: true);
        var demoted = Advance(SessionContext.PitContradictionGrace + TimeSpan.FromSeconds(5), moveGps: true);

        Assert.AreEqual(0, demoted.Count);
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"));
    }

    [TestMethod]
    public async Task PitEntry_DeviceCommitsStopWithinGrace_KeepsFlagtronics()
    {
        Advance(TimeSpan.FromSeconds(30));
        await Pass(loopId: 1, inPit: true);

        // The device confirms the stop a few seconds after the car crosses the loop.
        Advance(TimeSpan.FromSeconds(10));
        _car.IsInPit = true;
        var demoted = Advance(SessionContext.PitContradictionGrace);

        Assert.AreEqual(0, demoted.Count);
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"));
    }

    [TestMethod]
    public async Task PitExit_LoopCrossedJustAfterDeviceLeftPit_KeepsFlagtronics()
    {
        _car.IsInPit = true;
        Advance(TimeSpan.FromSeconds(60));

        // The device moves the car back on track, then it crosses the pit-out loop.
        _car.IsInPit = false;
        Advance(TimeSpan.FromSeconds(10));
        await Pass(loopId: 2, inPit: true);
        var demoted = Advance(SessionContext.PitContradictionGrace + TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, demoted.Count);
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"));
    }

    [TestMethod]
    public async Task StuckInPit_TwoTrackCrossings_HandsCarToX2OnTrack()
    {
        _car.IsInPit = true;
        Advance(TimeSpan.FromSeconds(30));

        await Pass(loopId: 0, inPit: false);
        Assert.AreEqual(0, Advance(TimeSpan.FromSeconds(150)).Count, "one crossing is not enough");

        await Pass(loopId: 0, inPit: false);
        var demoted = Advance(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { "77" }, demoted);
        Assert.IsFalse(_sessionContext.IsFlagtronicsPitTrusted("77"), "hand over despite the in-pit hold");
        Assert.IsNotNull(_pitProcessor.ProcessCar("77", includeUntypedPassings: true));
        Assert.IsFalse(_car.IsInPit, "X2 shows the car on track");
    }

    [TestMethod]
    public async Task FrozenGpsOnTrack_ShortStop_TrackCrossingDoesNotClearEvidence()
    {
        Advance(TimeSpan.FromSeconds(30));

        // A drive-through: pit loop, then back on track over the S/F within the grace. The
        // device, frozen on track, matches the track loop only because it never moved.
        await Pass(loopId: 1, inPit: true);
        Advance(TimeSpan.FromSeconds(10));
        await Pass(loopId: 0, inPit: false);
        var demoted = Advance(SessionContext.PitContradictionGrace);

        CollectionAssert.AreEqual(new[] { "77" }, demoted);
    }

    [TestMethod]
    public async Task StuckInPit_LaterPitStopDoesNotClearEvidence()
    {
        _car.IsInPit = true;
        Advance(TimeSpan.FromSeconds(30));

        await Pass(loopId: 0, inPit: false);
        Advance(TimeSpan.FromSeconds(60));

        // The pit loop agrees with the stuck device, which proves nothing about the device.
        await Pass(loopId: 1, inPit: true);
        Advance(TimeSpan.FromSeconds(90));
        await Pass(loopId: 0, inPit: false);
        var demoted = Advance(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { "77" }, demoted);
    }

    [TestMethod]
    public async Task InPit_TwoDifferentTrackLoopsCrossReadInOneStop_KeepsFlagtronics()
    {
        _car.IsInPit = true;
        Advance(TimeSpan.FromSeconds(30));

        // Pit stall between two track loops beside the pit lane: one is cross-read on the way in,
        // the other on the way out, minutes apart.
        await Pass(loopId: 0, inPit: false);
        Advance(TimeSpan.FromMinutes(2));
        await Pass(loopId: 5, inPit: false);
        var demoted = Advance(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, demoted.Count);
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"));
    }

    [TestMethod]
    public async Task InPit_SingleTrackLoopCrossRead_KeepsFlagtronics()
    {
        _car.IsInPit = true;
        Advance(TimeSpan.FromSeconds(30));

        // A strong transponder in the pit lane read once by the track loop beside it.
        await Pass(loopId: 0, inPit: false);
        var demoted = Advance(TimeSpan.FromMinutes(5));

        Assert.AreEqual(0, demoted.Count);
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"));
    }

    [TestMethod]
    public async Task InPit_DeviceLeavesPitAfterTrackCrossing_KeepsFlagtronics()
    {
        _car.IsInPit = true;
        Advance(TimeSpan.FromSeconds(30));

        await Pass(loopId: 0, inPit: false);
        Advance(TimeSpan.FromSeconds(5));
        _car.IsInPit = false;
        Advance(TimeSpan.FromSeconds(150));

        _car.IsInPit = true;
        await Pass(loopId: 0, inPit: false);
        var demoted = Advance(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, demoted.Count, "the second crossing starts a fresh contradiction");
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"));
    }

    [TestMethod]
    public async Task DemotedCar_StaysOnX2_WhenBarsStayFull()
    {
        Advance(TimeSpan.FromSeconds(30));
        await Pass(loopId: 1, inPit: true);
        Advance(SessionContext.PitContradictionGrace + TimeSpan.FromSeconds(5));
        Assert.IsFalse(_sessionContext.IsFlagtronicsPitTrusted("77"));

        _pitProcessor.ProcessCar("77");
        _car.IsInPit = false;
        Advance(TimeSpan.FromMinutes(2));

        Assert.IsFalse(_sessionContext.IsFlagtronicsPitTrusted("77"), "full bars must not hand the car back");
    }

    private async Task DemoteFrozenCar()
    {
        Advance(TimeSpan.FromSeconds(30));
        await Pass(loopId: 1, inPit: true);
        Advance(SessionContext.PitContradictionGrace + TimeSpan.FromSeconds(5));
        Assert.IsFalse(_sessionContext.IsFlagtronicsPitTrusted("77"), "precondition: car handed to X2");
        _pitProcessor.ProcessCar("77");
        _car.IsInPit = false;
    }

    [TestMethod]
    public async Task DemotedCar_ReturnsToFlagtronics_AfterHealthyRecoveryWindow()
    {
        await DemoteFrozenCar();

        Advance(SessionContext.FlagtronicsRecoveryWindow - TimeSpan.FromMinutes(1), moveGps: true);
        Assert.IsFalse(_sessionContext.IsFlagtronicsPitTrusted("77"), "not before the window has run");

        Advance(TimeSpan.FromMinutes(2), moveGps: true);
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"), "a recovered device takes the car back");
    }

    [TestMethod]
    public async Task DemotedCar_FreezeDuringWindow_RestartsIt()
    {
        await DemoteFrozenCar();

        Advance(TimeSpan.FromMinutes(20), moveGps: true);
        Advance(SessionContext.GpsFrozenAfter + TimeSpan.FromSeconds(10));
        Advance(TimeSpan.FromMinutes(20), moveGps: true);
        Assert.IsFalse(_sessionContext.IsFlagtronicsPitTrusted("77"), "the stall restarted the window");

        Advance(TimeSpan.FromMinutes(11), moveGps: true);
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"));
    }

    [TestMethod]
    public async Task DemotedCar_LowBarsAtWindowEnd_WaitsForBars()
    {
        await DemoteFrozenCar();

        // A brief dip mid-window does not restart it; bars only gate the handover itself.
        Advance(TimeSpan.FromMinutes(10), moveGps: true);
        _car.SignalBars = SessionContext.MIN_TRUSTED_PIT_SIGNAL_BARS - 1;
        Advance(TimeSpan.FromSeconds(10), moveGps: true);
        _car.SignalBars = CarPosition.MaxSignalBars;
        Advance(TimeSpan.FromMinutes(10), moveGps: true);

        _car.SignalBars = SessionContext.MIN_TRUSTED_PIT_SIGNAL_BARS - 1;
        Advance(TimeSpan.FromMinutes(15), moveGps: true);
        Assert.IsFalse(_sessionContext.IsFlagtronicsPitTrusted("77"), "bars too low to hand back");

        _car.SignalBars = CarPosition.MaxSignalBars;
        Advance(TimeSpan.FromSeconds(5), moveGps: true);
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"));
    }

    [TestMethod]
    public async Task DemotedCar_FreezesWhileHandoverWaitsForPitExit_StaysOnX2()
    {
        await DemoteFrozenCar();
        _car.IsInPit = true;
        Advance(SessionContext.FlagtronicsRecoveryWindow + TimeSpan.FromMinutes(1), moveGps: true);

        // The window has run, but the car is in the pit, and its GPS freezes during the stop.
        Advance(TimeSpan.FromMinutes(2));
        _car.IsInPit = false;
        Advance(TimeSpan.FromSeconds(5));

        Assert.IsFalse(_sessionContext.IsFlagtronicsPitTrusted("77"), "a device that froze again is not given back");
    }

    [TestMethod]
    public async Task RedemotedCar_WaitsTwiceAsLong_AndIsCaughtAgain()
    {
        await DemoteFrozenCar();
        Advance(SessionContext.FlagtronicsRecoveryWindow + TimeSpan.FromMinutes(1), moveGps: true);
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"), "precondition: handed back");

        // Freezes again on track, and a pit loop catches it out a second time.
        Advance(TimeSpan.FromMinutes(2));
        await Pass(loopId: 1, inPit: true);
        Advance(SessionContext.PitContradictionGrace + TimeSpan.FromSeconds(5));
        Assert.IsFalse(_sessionContext.IsFlagtronicsPitTrusted("77"), "loop evidence is accepted again");
        _pitProcessor.ProcessCar("77");
        _car.IsInPit = false;

        Advance(SessionContext.FlagtronicsRecoveryWindow + TimeSpan.FromMinutes(1), moveGps: true);
        Assert.IsFalse(_sessionContext.IsFlagtronicsPitTrusted("77"), "the second window is longer");

        Advance(SessionContext.FlagtronicsRecoveryWindow, moveGps: true);
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"));
    }

    [TestMethod]
    public async Task DemotedCar_InPitOnX2_WaitsForPitExitBeforeReturning()
    {
        await DemoteFrozenCar();
        _car.IsInPit = true;

        Advance(SessionContext.FlagtronicsRecoveryWindow + TimeSpan.FromMinutes(1), moveGps: true);
        Assert.IsFalse(_sessionContext.IsFlagtronicsPitTrusted("77"), "no handover mid-stop");

        _car.IsInPit = false;
        Advance(TimeSpan.FromSeconds(5), moveGps: true);
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"));
    }
}
