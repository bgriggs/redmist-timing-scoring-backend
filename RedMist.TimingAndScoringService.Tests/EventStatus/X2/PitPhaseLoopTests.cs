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
using RedMist.TimingCommon.Models.Configuration;
using ConfigurationEvent = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.EventProcessor.Tests.EventStatus.X2;

/// <summary>
/// The pit visit sequence for a car whose pit state comes from timing loops, which cannot see a
/// stop: entered at the pit-in loop, a generic in-pit past the pit start/finish, exited at the
/// pit-exit loop.
/// </summary>
[TestClass]
public class PitPhaseLoopTests
{
    private const uint PitInLoop = 1;
    private const uint PitSfLoop = 2;
    private const uint PitExitLoop = 3;
    private const uint TrackLoop = 4;

    private SessionContext _sessionContext = null!;
    private PitProcessor _processor = null!;
    private FakeTimeProvider _time = null!;
    private CarPosition _car = null!;

    private async Task Setup(params LoopMetadata[] loops)
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        var dbContextFactory = new TestDbContextFactory(optionsBuilder.Options);
        await using (var db = await dbContextFactory.CreateDbContextAsync())
        {
            db.Events.Add(new ConfigurationEvent { Id = 1, LoopsMetadata = [.. loops] });
            await db.SaveChangesAsync();
        }

        _time = new FakeTimeProvider();
        _sessionContext = new SessionContext(config, dbContextFactory, loggerFactory.Object,
            new Mock<ICarLapHistoryService>().Object, _time);
        // No in-car device, so the loops own the car's pit state.
        _sessionContext.UpdateCars([new CarPosition { Number = "77", TransponderId = 77, LastLapCompleted = 10 }]);
        _sessionContext.UpdatePitOwnership();
        _car = _sessionContext.GetCarByNumber("77")!;

        _processor = new PitProcessor(dbContextFactory, loggerFactory.Object, _sessionContext, _time);
        await _processor.Initialize(1);
    }

    private Task FullLoops() => Setup(
        new LoopMetadata { Id = PitInLoop, Type = LoopType.PitIn, Name = "PitIn" },
        new LoopMetadata { Id = PitSfLoop, Type = LoopType.PitStartFinish, Name = "PitSF" },
        new LoopMetadata { Id = PitExitLoop, Type = LoopType.PitExit, Name = "PitOut" },
        new LoopMetadata { Id = TrackLoop, Type = LoopType.Other, Name = "T5" });

    /// <summary>Passing serializes with short names: t = transponder, l = loop, p = in pit.</summary>
    private Task Pass(uint loopId, bool inPit) =>
        _processor.Process(new TimingMessage(Backend.Shared.Consts.X2PASS_TYPE,
            $$"""[{ "t": 77, "l": {{loopId}}, "p": {{(inPit ? "true" : "false")}} }]""", 1, DateTime.UtcNow));

    [TestMethod]
    public async Task PitInThenPitSfThenPitExit_RunsEnteredInPitExited()
    {
        await FullLoops();

        await Pass(PitInLoop, inPit: true);
        Assert.AreEqual(PitPhase.EnteredPit, _car.PitPhase);

        await Pass(PitSfLoop, inPit: true);
        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);

        await Pass(PitExitLoop, inPit: false);
        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);
        Assert.IsTrue(_car.IsExitedPit);
    }

    [TestMethod]
    public async Task Exited_ClearsAfterTheHold()
    {
        await FullLoops();
        await Pass(PitInLoop, inPit: true);
        await Pass(PitExitLoop, inPit: false);

        _time.Advance(PitProcessor.ExitedHold - TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, _processor.ExpireExitedPitPhases().Count);
        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);

        _time.Advance(TimeSpan.FromSeconds(1));
        var patches = _processor.ExpireExitedPitPhases();

        Assert.AreEqual(1, patches.Count);
        Assert.AreEqual(PitPhase.None, _car.PitPhase);
        Assert.IsFalse(_car.IsExitedPit);
        Assert.AreEqual(false, patches[0].IsInPit, "resent for clients that only clear the badge on it");
        Assert.AreEqual(0, _processor.ExpireExitedPitPhases().Count);
    }

    [TestMethod]
    public async Task ExitLoopFlaggedInPit_AgreesWithTheInPitFlagAfterTheHold()
    {
        await FullLoops();
        await Pass(PitInLoop, inPit: true);
        await Pass(PitExitLoop, inPit: true);
        Assert.AreEqual(PitPhase.ExitedPit, _car.PitPhase);

        _time.Advance(PitProcessor.ExitedHold);
        _processor.ExpireExitedPitPhases();

        // X2 still holds the car in the pit, so the phase does too rather than reading None.
        Assert.IsTrue(_car.IsInPit);
        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);
        Assert.IsFalse(_car.IsExitedPit);
    }

    [TestMethod]
    public async Task RoutineReapply_DoesNotRestoreAnExpiredExit()
    {
        await FullLoops();
        await Pass(PitInLoop, inPit: true);
        await Pass(PitExitLoop, inPit: false);
        _time.Advance(PitProcessor.ExitedHold);
        _processor.ExpireExitedPitPhases();

        _processor.ProcessCar("77");

        Assert.AreEqual(PitPhase.None, _car.PitPhase);
    }

    [TestMethod]
    public async Task PitSfOnlyEvent_ShowsGenericPitThenNone()
    {
        await Setup(
            new LoopMetadata { Id = PitSfLoop, Type = LoopType.PitStartFinish, Name = "PitSF" },
            new LoopMetadata { Id = TrackLoop, Type = LoopType.Other, Name = "T5" });

        await Pass(PitSfLoop, inPit: true);
        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);
        Assert.IsTrue(_car.IsInPit);

        await Pass(TrackLoop, inPit: false);
        Assert.AreEqual(PitPhase.None, _car.PitPhase);
        Assert.IsFalse(_car.IsInPit);
    }

    [TestMethod]
    public async Task UntypedInPitLoop_ShowsGenericPit()
    {
        await Setup();

        await Pass(9, inPit: true);

        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);
    }

    [TestMethod]
    public async Task PitSfOnlyEvent_UnflaggedLoop_StillShowsGenericPit()
    {
        await Setup(
            new LoopMetadata { Id = PitSfLoop, Type = LoopType.PitStartFinish, Name = "PitSF" },
            new LoopMetadata { Id = TrackLoop, Type = LoopType.Other, Name = "T5" });

        await Pass(PitSfLoop, inPit: false);

        Assert.IsTrue(_car.IsInPit);
        Assert.AreEqual(PitPhase.InPit, _car.PitPhase);
    }

    [TestMethod]
    public async Task ResentPitExit_DoesNotShowTheCarAsJustExited()
    {
        await FullLoops();

        await _processor.Process(new TimingMessage(Backend.Shared.Consts.X2PASS_TYPE,
            $$"""[{ "t": 77, "l": {{PitExitLoop}}, "p": false, "r": true }]""", 1, DateTime.UtcNow));

        Assert.AreNotEqual(PitPhase.ExitedPit, _car.PitPhase);
        Assert.IsFalse(_car.IsExitedPit);
    }

    [TestMethod]
    public async Task PitInThenPitSf_ResendsInPitButDoesNotRetagTheLap()
    {
        await FullLoops();
        await Pass(PitInLoop, inPit: true);
        var lapsAfterEntry = _processor.GetCarLapsWithPitStops()["77"].ToArray();

        _car.LastLapCompleted = 11;
        var patch = (await _processor.Process(new TimingMessage(Backend.Shared.Consts.X2PASS_TYPE,
            $$"""[{ "t": 77, "l": {{PitSfLoop}}, "p": true }]""", 1, DateTime.UtcNow)))!.CarPatches.Single();

        Assert.AreEqual(false, patch.IsEnteredPit);
        Assert.AreEqual(true, patch.IsInPit, "resent for clients that only move the badge on it");
        CollectionAssert.AreEquivalent(lapsAfterEntry, _processor.GetCarLapsWithPitStops()["77"].ToArray());
    }

    [TestMethod]
    public async Task HandedOverFromFlagtronicsWithNoPassings_WithdrawsItsPhase()
    {
        await FullLoops();
        _car.IsExitedPit = true;
        _car.PitPhase = PitPhase.ExitedPit;

        var patch = _processor.TakeOver("77");

        Assert.IsNotNull(patch);
        Assert.AreEqual(PitPhase.None, _car.PitPhase);
        Assert.IsFalse(_car.IsExitedPit);
        Assert.AreEqual(false, patch.IsInPit);
    }

    [TestMethod]
    public async Task HandedOverFromFlagtronicsMidStop_WithNoPassings_WithdrawsThePhaseButNotThePit()
    {
        await FullLoops();
        _car.IsInPit = true;
        _car.PitPhase = PitPhase.InPitBox;

        _processor.TakeOver("77");

        // Nothing would maintain a phase from here, so none is left; the in-pit flag stays as the
        // car's last owner left it, for whoever speaks for the car next.
        Assert.IsTrue(_car.IsInPit);
        Assert.AreEqual(PitPhase.None, _car.PitPhase);
    }

    [TestMethod]
    public async Task HandedOverWhileTheLoopsStillHaveItInThePitLane_DoesNotPutItBack()
    {
        // The handover was held while the car was in the pit; by the time it happens the car has
        // left, and the last passing - pit S/F - describes the lane it is no longer in.
        await FullLoops();
        await Pass(PitSfLoop, inPit: true);
        _car.IsInPit = false;
        _car.IsExitedPit = true;
        _car.PitPhase = PitPhase.ExitedPit;

        _processor.TakeOver("77");

        Assert.IsFalse(_car.IsInPit);
        Assert.AreEqual(PitPhase.None, _car.PitPhase);
        Assert.IsFalse(_car.IsExitedPit);
    }

    [TestMethod]
    public async Task DemotedForBadData_PublishesTheLoopViewAtOnce()
    {
        await FullLoops();
        await Pass(PitInLoop, inPit: true);
        _car.IsInPit = false;
        _car.IsEnteredPit = false;
        _car.PitPhase = PitPhase.None;

        _processor.TakeOver("77", demoted: true);

        Assert.IsTrue(_car.IsInPit);
        Assert.IsTrue(_car.IsEnteredPit);
        Assert.AreEqual(PitPhase.EnteredPit, _car.PitPhase);
    }

    [TestMethod]
    public async Task OnlyARealPitEntry_NotifiesTheLapProcessor()
    {
        await FullLoops();
        var notified = new List<string>();
        _processor.NotifyLapProcessorOfPitMessages = cars => { notified.AddRange(cars); return Task.CompletedTask; };

        await Pass(PitInLoop, inPit: true);
        await Pass(PitSfLoop, inPit: true);

        CollectionAssert.AreEqual(new[] { "77" }, notified, "the in-pit flag resent at pit S/F is not a new entry");
    }

    [TestMethod]
    public async Task TakeOver_LeavesACarFlagtronicsStillOwnsAlone()
    {
        await FullLoops();
        _car.SignalBars = CarPosition.MaxSignalBars;
        _sessionContext.UpdatePitOwnership();
        Assert.IsTrue(_sessionContext.IsFlagtronicsPitTrusted("77"), "precondition");
        _car.PitPhase = PitPhase.InPitBox;

        Assert.IsNull(_processor.TakeOver("77"));
        Assert.AreEqual(PitPhase.InPitBox, _car.PitPhase);
    }

    [TestMethod]
    public async Task UnderMultiloop_LoopStateIsNotPublished()
    {
        await FullLoops();
        await Pass(PitInLoop, inPit: true);
        _sessionContext.IsMultiloopActive = true;

        Assert.IsNull(_processor.ProcessCar("77"));
    }
}
