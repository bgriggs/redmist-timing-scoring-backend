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

    // ---- GPS health: cadence against the field, and the field against the specified rate ----

    /// <summary>
    /// Registers a field and feeds each car at its own interval for <paramref name="seconds"/>, so
    /// the cars differ in how often they report rather than in how good their data is.
    /// </summary>
    private void FeedFieldAtIntervals(int seconds, params (string Car, double IntervalSeconds)[] cars)
    {
        _sessionContext.UpdateCars([.. cars.Select(c => new CarPosition { Number = c.Car })]);

        var next = cars.ToDictionary(c => c.Car, _ => 0.0);
        for (double t = 0; t < seconds; t += 0.5)
        {
            foreach (var (car, interval) in cars)
            {
                if (t + 1e-9 < next[car])
                    continue;
                _tracker.RecordTick(car, faulted: false);
                next[car] = t + interval;
            }
            _time.Advance(TimeSpan.FromSeconds(0.5));
        }
    }

    private int? Health(string car)
    {
        _tracker.Process();
        _time.Advance(TimeSpan.FromSeconds(11));
        _tracker.Process();
        return _sessionContext.GetCarByNumber(car)?.GpsHealth;
    }

    [TestMethod]
    public void CarKeepingUpWithTheField_ShowsFullGpsHealth()
    {
        FeedFieldAtIntervals(90, ("1", 1.0), ("2", 1.0), ("3", 1.0), ("4", 1.0));

        Assert.AreEqual(CarPosition.MaxGpsHealth, Health("1"));
    }

    [TestMethod]
    public void CarReportingFarSlowerThanTheField_ShowsLowGpsHealth()
    {
        // Every car is sending clean data; only the cadence separates them.
        FeedFieldAtIntervals(90, ("1", 1.0), ("2", 1.0), ("3", 1.0), ("4", 6.0));

        Assert.AreEqual(CarPosition.MaxGpsHealth, Health("1"));
        var slow = Health("4");
        Assert.IsNotNull(slow);
        Assert.IsTrue(slow < 3, $"a car at a sixth of the field's rate should rate poorly, got {slow}");
    }

    [TestMethod]
    public void SlowFieldWithNoStragglers_StillRatesEachCarWell()
    {
        // The whole field at a third of the specified rate: no car is worse than its neighbours,
        // so per-car health stays high and only the source grade carries the bad news.
        FeedFieldAtIntervals(90, ("1", 3.0), ("2", 3.0), ("3", 3.0), ("4", 3.0));

        Assert.AreEqual(CarPosition.MaxGpsHealth, Health("1"));
        Assert.IsTrue(_sessionContext.SessionState.GpsSourceHealth < 4,
            $"a field at a third of rate should grade low, got {_sessionContext.SessionState.GpsSourceHealth}");
    }

    [TestMethod]
    public void FieldReportingAtTheSpecifiedRate_GradesTheSourceFull()
    {
        FeedFieldAtIntervals(90, ("1", 1.0), ("2", 1.0), ("3", 1.0), ("4", 1.0));

        _tracker.Process();
        Assert.AreEqual(CarPosition.MaxGpsHealth, _sessionContext.SessionState.GpsSourceHealth);
    }

    [TestMethod]
    public void TooFewCarsToDescribeAField_LeavesTheSourceGradeUnset()
    {
        // Two cars are not a field; grading them against each other would rate both full marks.
        FeedFieldAtIntervals(90, ("1", 1.0), ("2", 1.0));

        _tracker.Process();
        Assert.IsNull(_sessionContext.SessionState.GpsSourceHealth);
    }

    [TestMethod]
    public void SourceGoingQuiet_ClearsTheFlagThatGatesTheGrade()
    {
        FeedFieldAtIntervals(90, ("1", 1.0), ("2", 1.0), ("3", 1.0), ("4", 1.0));
        _tracker.Process();
        Assert.IsNotNull(_sessionContext.SessionState.GpsSourceHealth);

        // Nothing reporting for longer than the source is given to be quiet.
        _time.Advance(TimeSpan.FromSeconds(120));
        _tracker.Process();

        // The grade cannot be taken back: a patch carries "no change" as null, so there is no way
        // to send "there is no longer a grade". HasTelemetrySource is what withdraws the display,
        // and the stale number sits behind it until a source returns to move it.
        Assert.IsFalse(_sessionContext.SessionState.HasTelemetrySource);
    }

    [TestMethod]
    public void FieldJustArrived_DoesNotGradeTheSourceUntilItHasBeenWatched()
    {
        // A rate measured over the first moments is one record divided by almost nothing, which
        // would otherwise grade the source full marks - the one value its contract says should
        // not appear - and publish it immediately, since the first value skips the confirm window.
        _sessionContext.UpdateCars([.. new[] { "1", "2", "3", "4" }.Select(n => new CarPosition { Number = n })]);
        foreach (var car in new[] { "1", "2", "3", "4" })
            _tracker.RecordTick(car, faulted: false);

        // Live, the records and the sweep that follows them take their own readings of the clock,
        // so the gap is a fraction of a second rather than none. That fraction is the divisor.
        _time.Advance(TimeSpan.FromMilliseconds(50));
        _tracker.Process();

        Assert.IsNull(_sessionContext.SessionState.GpsSourceHealth);
    }

    [TestMethod]
    public void CarInThePits_IsNotGradedAgainstAFieldThatExcludesIt()
    {
        FeedFieldAtIntervals(90, ("1", 1.0), ("2", 1.0), ("3", 1.0), ("4", 1.0));
        Assert.AreEqual(CarPosition.MaxGpsHealth, Health("4"));

        // The fleet rate deliberately leaves pit cars out, so it does not describe one. Rather
        // than mark the car down against a reference that excludes it, the last grade stands.
        _sessionContext.GetCarByNumber("4")!.IsInPit = true;
        for (int i = 0; i < 60; i++)
        {
            foreach (var car in new[] { "1", "2", "3" })
                _tracker.RecordTick(car, faulted: false);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.AreEqual(CarPosition.MaxGpsHealth, Health("4"));
    }

    [TestMethod]
    public void FieldEmptyingBelowAUsableSize_HoldsEachCarsGradeRatherThanRaisingIt()
    {
        // A slow car among a healthy field rates low.
        FeedFieldAtIntervals(90, ("1", 1.0), ("2", 1.0), ("3", 1.0), ("4", 6.0));
        var whileFieldPresent = Health("4");
        Assert.IsTrue(whileFieldPresent < 3, $"expected a low rating to start from, got {whileFieldPresent}");

        // The field leaves the track. Nothing new has been learned about car 4, so its grade must
        // not jump to full marks just because there is no longer anything to compare it against.
        foreach (var car in new[] { "1", "2", "3" })
            _sessionContext.GetCarByNumber(car)!.IsInPit = true;
        for (int i = 0; i < 60; i++)
        {
            foreach (var car in new[] { "1", "2", "3", "4" })
                _tracker.RecordTick(car, faulted: false);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.AreEqual(whileFieldPresent, Health("4"));
    }

    [TestMethod]
    public void CarSendingFaultedData_RatesNoBetterThanItsFaultRate()
    {
        // Keeping up with the field cannot rescue a car whose readings are unusable: the health
        // figure takes the worse of the two.
        FeedFieldAtIntervals(90, ("1", 1.0), ("2", 1.0), ("3", 1.0));
        for (int i = 0; i < 40; i++)
        {
            _tracker.RecordTick("1", faulted: true);
            _tracker.RecordTick("2", faulted: false);
            _tracker.RecordTick("3", faulted: false);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var faulted = Health("1");
        Assert.IsNotNull(faulted);
        Assert.IsTrue(faulted <= _sessionContext.GetCarByNumber("1")!.SignalBars,
            "health must not exceed the fault-rate view");
        Assert.IsTrue(faulted < CarPosition.MaxGpsHealth, $"expected a reduced rating, got {faulted}");
    }
}
