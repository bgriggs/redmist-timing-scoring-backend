using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.Flagtronics;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.Flagtronics;

[TestClass]
public class TelemetrySignalTrackerTests
{
    private TelemetrySignalTracker _tracker = null!;
    private SessionContext _sessionContext = null!;
    private FakeTimeProvider _time = null!;

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

        _sessionContext = new SessionContext(config, dbContextFactory, loggerFactory.Object,
            new Mock<ICarLapHistoryService>().Object);
        _sessionContext.UpdateCars([new CarPosition { Number = "42", TransponderId = 42 }]);

        _time = new FakeTimeProvider();
        _tracker = new TelemetrySignalTracker(loggerFactory.Object, _sessionContext, _time);
    }

    /// <summary>
    /// Feeds <paramref name="count"/> ticks three seconds apart, matching the observed live
    /// update rate, of which <paramref name="faulted"/> carry a fault.
    /// </summary>
    private void Feed(int count, int faulted = 0, string car = "42")
    {
        for (int i = 0; i < count; i++)
        {
            _tracker.RecordTick(car, i < faulted);
            _time.Advance(TimeSpan.FromSeconds(3));
        }
    }

    private int? Bars(string car = "42")
    {
        _tracker.Process();
        return _sessionContext.GetCarByNumber(car)?.SignalBars;
    }

    /// <summary>
    /// Evaluates either side of the confirm window, so a changed bar count is published rather
    /// than left pending.
    /// </summary>
    private int? SettledBars(string car = "42")
    {
        _tracker.Process();
        _time.Advance(TimeSpan.FromSeconds(11));
        _tracker.Process();
        return _sessionContext.GetCarByNumber(car)?.SignalBars;
    }

    [TestMethod]
    public void CarNeverSeen_LeavesSignalBarsNull()
    {
        _tracker.Process();

        // Null means no in-car device, which is not the same as zero bars.
        Assert.IsNull(_sessionContext.GetCarByNumber("42")!.SignalBars);
    }

    [TestMethod]
    public void CleanReporting_ShowsFullBars()
    {
        Feed(10);
        Assert.AreEqual(CarPosition.MaxSignalBars, Bars());
    }

    [TestMethod]
    public void FirstFewTicks_ReportFullBarsRatherThanASampleOfOne()
    {
        // One faulted tick out of one is not evidence the link is bad.
        _tracker.RecordTick("42", faulted: true);
        Assert.AreEqual(CarPosition.MaxSignalBars, Bars());
    }

    [TestMethod]
    public void SlowReportingBrokenDevice_StillDropsToZeroBars()
    {
        // Regression: warm-up used to be gated on sample count, which a car reporting slower
        // than the window divided by that count could never reach - so a device sending nothing
        // usable, slowly, showed full bars indefinitely.
        for (int i = 0; i < 10; i++)
        {
            _tracker.RecordTick("42", faulted: true);
            _time.Advance(TimeSpan.FromSeconds(25));   // slow, but inside StaleAfter
        }

        Assert.AreEqual(CarPosition.MinSignalBars, Bars());
    }

    [TestMethod]
    public void SlowButCleanDevice_KeepsFullBars()
    {
        for (int i = 0; i < 10; i++)
        {
            _tracker.RecordTick("42", faulted: false);
            _time.Advance(TimeSpan.FromSeconds(25));
        }

        Assert.AreEqual(CarPosition.MaxSignalBars, Bars());
    }

    [TestMethod]
    public void BarCountMustHoldBeforeItIsPublished()
    {
        Feed(30);
        Assert.AreEqual(CarPosition.MaxSignalBars, Bars());

        // Go dark. The stale reading is not published until it has persisted.
        _time.Advance(TimeSpan.FromSeconds(50));
        Assert.AreEqual(CarPosition.MaxSignalBars, Bars(), "a change should not publish on first sight");

        _time.Advance(TimeSpan.FromSeconds(15));
        Assert.AreEqual(CarPosition.MinSignalBars, Bars());
    }

    [TestMethod]
    public void BarCountThatFlipsBackBeforeConfirming_NeverPublishes()
    {
        Feed(30);
        Assert.AreEqual(CarPosition.MaxSignalBars, Bars());

        // A gap long enough to read as stale, but the car returns before the change confirms.
        _time.Advance(TimeSpan.FromSeconds(50));
        _tracker.Process();
        Feed(10);

        Assert.AreEqual(CarPosition.MaxSignalBars, Bars());
    }

    [TestMethod]
    public void CarStopsReporting_DropsToZeroBars()
    {
        Feed(10);
        Assert.AreEqual(CarPosition.MaxSignalBars, Bars());

        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(CarPosition.MinSignalBars, SettledBars());
    }

    [TestMethod]
    public void ReportingButNothingUsable_DropsToZeroBars()
    {
        // A car whose GPS has dropped out still sends records; every one of them is faulted.
        Feed(10, faulted: 10);
        Assert.AreEqual(CarPosition.MinSignalBars, Bars());
    }

    [TestMethod]
    public void DegradingDevice_ShowsMiddleBars()
    {
        // Roughly the fault rate car 92 ran at during its worst hour in production.
        Feed(10, faulted: 3);
        var bars = Bars();

        Assert.IsTrue(bars is > CarPosition.MinSignalBars and < CarPosition.MaxSignalBars,
            $"expected a degraded but non-zero reading, got {bars}");
    }

    [TestMethod]
    public void FaultsAgeOutOfTheWindow()
    {
        Feed(10, faulted: 10);
        Assert.AreEqual(CarPosition.MinSignalBars, Bars());

        // A clean minute later the old faults no longer count against the car.
        Feed(20);
        Assert.AreEqual(CarPosition.MaxSignalBars, SettledBars());
    }

    [TestMethod]
    public void Process_OnlyPatchesWhenTheBarCountChanges()
    {
        Feed(10);
        Assert.IsNotNull(_tracker.Process(), "first evaluation should publish the value");
        Assert.IsNull(_tracker.Process(), "unchanged value should not generate a patch");
    }

    [TestMethod]
    public void HasTelemetrySource_TracksTheFeedRatherThanLatching()
    {
        Assert.IsFalse(_sessionContext.SessionState.HasTelemetrySource);

        Feed(4);
        _tracker.Process();
        Assert.IsTrue(_sessionContext.SessionState.HasTelemetrySource);

        // Unlike the pit-source gate, this clears once the feed stops.
        _time.Advance(TimeSpan.FromMinutes(3));
        _tracker.Process();
        Assert.IsFalse(_sessionContext.SessionState.HasTelemetrySource);
    }

    [TestMethod]
    public void OneCarGoingQuiet_DoesNotClearTheSessionSource()
    {
        _sessionContext.UpdateCars(
        [
            new CarPosition { Number = "42", TransponderId = 42 },
            new CarPosition { Number = "43", TransponderId = 43 },
        ]);

        Feed(6, car: "42");
        Feed(6, car: "43");
        _tracker.Process();

        // Car 42 has been quiet long enough to lose its bars while 43 keeps reporting.
        for (int i = 0; i < 20; i++)
        {
            _tracker.RecordTick("43", faulted: false);
            _time.Advance(TimeSpan.FromSeconds(3));
        }

        _tracker.Process();
        _time.Advance(TimeSpan.FromSeconds(11));
        _tracker.Process();
        Assert.AreEqual(CarPosition.MinSignalBars, _sessionContext.GetCarByNumber("42")!.SignalBars);
        Assert.AreEqual(CarPosition.MaxSignalBars, _sessionContext.GetCarByNumber("43")!.SignalBars);
        Assert.IsTrue(_sessionContext.SessionState.HasTelemetrySource);
    }

    [TestMethod]
    public void SessionChange_ClearsAccumulatedState()
    {
        Feed(10, faulted: 10);
        Assert.AreEqual(CarPosition.MinSignalBars, Bars());

        _sessionContext.SessionState.SessionId = 99;
        _tracker.Process();

        // State from the previous session must not colour the new one.
        Feed(4);
        Assert.AreEqual(CarPosition.MaxSignalBars, SettledBars());
    }
}
