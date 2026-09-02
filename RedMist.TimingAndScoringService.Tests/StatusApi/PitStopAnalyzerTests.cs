using RedMist.StatusApi.Services.Exports;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Pit stops are inferred from lap snapshots, not recorded, so these tests are the specification of
/// the inference. Several of them pin behavior that is knowingly imperfect - a phantom stop from a
/// truncated lap history, a driver change that lands a lap late - because the report is only
/// defensible if the shape of its errors is deliberate.
/// </summary>
[TestClass]
public class PitStopAnalyzerTests
{
    private static readonly DateTime SessionStart = new(2026, 5, 1, 14, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The sticky entry time every row carries before the car has pitted in this session. Real lap
    /// rows are never without one - the field describes whatever the car last did - so laps default
    /// to carrying it, and a stop is a <em>change</em> away from it.
    /// </summary>
    private static readonly DateTime Baseline = new(2026, 5, 1, 13, 0, 0, DateTimeKind.Utc);

    private static CarPosition Lap(int lapNumber, string driver = "", DateTime? pitEntry = null,
        int? pitDurationMs = null, bool lapIncludedPit = false, bool isInPit = false,
        bool noEntryTime = false) => new()
        {
            Number = "42",
            LastLapCompleted = lapNumber,
            DriverName = driver,
            PitEntryTime = noEntryTime ? null : pitEntry ?? Baseline,
            PitDurationMs = pitDurationMs,
            LapIncludedPit = lapIncludedPit,
            IsInPit = isInPit,
        };

    private static IReadOnlyList<PitStopRecord> Run(params CarPosition[] laps)
    {
        var analyzer = new PitStopAnalyzer("42");
        foreach (var lap in laps)
            analyzer.AddLap(lap);
        return analyzer.Complete();
    }

    [TestMethod]
    public void NoPitData_ProducesNoStops()
    {
        var stops = Run(Lap(1, "Alice"), Lap(2, "Alice"), Lap(3, "Alice"));

        Assert.AreEqual(0, stops.Count);
    }

    [TestMethod]
    public void SingleStop_DerivesEntryExitDurationAndDriverChange()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Alice"),
            Lap(3, "Bob", pitEntry: entry, pitDurationMs: 62_000, lapIncludedPit: true),
            Lap(4, "Bob", pitEntry: entry, pitDurationMs: 62_000));

        Assert.AreEqual(1, stops.Count);
        var stop = stops[0];
        Assert.AreEqual("42", stop.CarNumber);
        Assert.AreEqual(1, stop.StopNumber);
        Assert.AreEqual(3, stop.StartLap);
        Assert.AreEqual(entry, stop.EntryTime);
        Assert.AreEqual(entry.AddMilliseconds(62_000), stop.ExitTime);
        Assert.AreEqual(62_000, stop.DurationMs);
        Assert.AreEqual("Alice", stop.DriverBefore);
        Assert.AreEqual("Bob", stop.DriverAfter);
        Assert.IsTrue(stop.DriverChanged);
    }

    /// <summary>
    /// The pit fields are sticky: every lap after a stop keeps reporting that stop's entry time.
    /// Treating each of those as a stop would turn one pit visit into a stop per remaining lap.
    /// </summary>
    [TestMethod]
    public void StickyEntryTime_DoesNotProduceAStopPerLap()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Bob", pitEntry: entry, pitDurationMs: 60_000),
            Lap(3, "Bob", pitEntry: entry, pitDurationMs: 60_000),
            Lap(4, "Bob", pitEntry: entry, pitDurationMs: 60_000));

        Assert.AreEqual(1, stops.Count);
    }

    /// <summary>
    /// Car 4, event 382 session 88. One physical stop long enough to span the start/finish line, so
    /// the car crosses while still in the pit and crosses again after rejoining. The lap row written
    /// at the first crossing carries the elapsed time so far - 27s - and only the next lap carries
    /// the real total of 47s. Reporting the first value understated the stop by 20 seconds.
    /// </summary>
    [TestMethod]
    public void StopSpanningStartFinish_ReportsTheFinalDurationAndBothLaps()
    {
        var entry = SessionStart.AddMinutes(12);
        var stops = Run(
            Lap(5, "Alice"),
            Lap(6, "Alice", pitEntry: entry, pitDurationMs: 27_000, lapIncludedPit: true, isInPit: true),
            Lap(7, "Bob", pitEntry: entry, pitDurationMs: 47_000, lapIncludedPit: true),
            Lap(8, "Bob", pitEntry: entry, pitDurationMs: 47_000));

        Assert.AreEqual(1, stops.Count, "one physical stop, not one per lap row");
        var stop = stops[0];
        Assert.AreEqual(6, stop.StartLap);
        Assert.AreEqual(7, stop.EndLap, "the stop ended on the lap the car rejoined");
        Assert.AreEqual(47_000, stop.DurationMs, "the duration is the largest value seen, not the one at the crossing");
        Assert.AreEqual(entry.AddMilliseconds(47_000), stop.ExitTime);
        Assert.AreEqual("Alice", stop.DriverBefore);
        Assert.AreEqual("Bob", stop.DriverAfter);
        Assert.IsTrue(stop.DriverChanged);
    }

    /// <summary>
    /// Car 53, event 382 session 88, verbatim. The equipment issued a <em>fresh</em> entry time on
    /// the second crossing of a stop the car never left, which is the common prod shape: keying the
    /// merge on the entry time staying put split this into a phantom 10 second stop on lap 114 and a
    /// second stop on 115-116. It is one stop, laps 114-116, 81 seconds.
    /// </summary>
    [TestMethod]
    public void StopWhoseEntryTimeIsReissuedMidStop_IsStillOneStop()
    {
        var first = new DateTime(2026, 5, 1, 17, 11, 50, DateTimeKind.Utc);
        var second = new DateTime(2026, 5, 1, 17, 34, 9, DateTimeKind.Utc);
        var stops = Run(
            Lap(113, "Alice"),
            Lap(114, "Alice", pitEntry: first, pitDurationMs: 10_000, lapIncludedPit: true, isInPit: true),
            Lap(115, "Alice", pitEntry: second, pitDurationMs: 64_000, lapIncludedPit: true, isInPit: true),
            Lap(116, "Bob", pitEntry: second, pitDurationMs: 81_000, lapIncludedPit: true),
            Lap(117, "Bob", pitEntry: second, pitDurationMs: 81_000));

        Assert.AreEqual(1, stops.Count, "the car never left the pit, so this is one stop");
        var stop = stops[0];
        Assert.AreEqual(114, stop.StartLap);
        Assert.AreEqual(116, stop.EndLap);
        Assert.AreEqual(81_000, stop.DurationMs, "the largest duration across the merged span");
        Assert.AreEqual(first, stop.EntryTime, "the earliest plausible entry, not the reissued one");
        Assert.IsTrue(stop.EntryTimeRenumbered, "the reader should know the device renumbered it");
        Assert.AreEqual("Alice", stop.DriverBefore);
        Assert.AreEqual("Bob", stop.DriverAfter);
    }

    /// <summary>
    /// The merge is keyed on pit presence, so an ordinary stop that the equipment happens to renumber
    /// between two separate visits is still two stops - the car was out in between.
    /// </summary>
    [TestMethod]
    public void EntryTimeChangeWhileTheCarIsOut_StartsANewStop()
    {
        var first = SessionStart.AddMinutes(20);
        var second = SessionStart.AddMinutes(50);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Alice", pitEntry: first, pitDurationMs: 40_000, lapIncludedPit: true),
            Lap(3, "Alice", pitEntry: first, pitDurationMs: 40_000),
            Lap(4, "Bob", pitEntry: second, pitDurationMs: 55_000, lapIncludedPit: true),
            Lap(5, "Bob", pitEntry: second, pitDurationMs: 55_000));

        Assert.AreEqual(2, stops.Count);
        Assert.IsFalse(stops[0].EntryTimeRenumbered);
        Assert.IsFalse(stops[1].EntryTimeRenumbered);
    }

    /// <summary>
    /// A pit flag that never clears must not let one stop swallow the rest of the session.
    /// </summary>
    [TestMethod]
    public void PitFlagThatNeverClears_ClosesTheStopAtTheSpanCap()
    {
        var entry = SessionStart.AddMinutes(20);
        var laps = new List<CarPosition> { Lap(1, "Alice") };
        for (var lap = 2; lap <= 60; lap++)
            laps.Add(Lap(lap, "Alice", pitEntry: entry, pitDurationMs: 30_000, isInPit: true));

        var stops = Run([.. laps]);

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual(PitStopAnalyzer.MaxStopSpanLaps,
            stops[0].EndLap - stops[0].StartLap + 1, "the span should stop at the cap");
    }

    /// <summary>
    /// A car whose first row carries no entry time at all - a late join, or purged early laps - must
    /// not get a phantom stop on the first row that does have one. The predicate is whether an entry
    /// time was seen on an earlier lap, not whether this is the first lap.
    /// </summary>
    [TestMethod]
    public void FirstEntryTimeArrivingAfterANullRow_IsStillOnlyTheBaseline()
    {
        var sticky = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice", noEntryTime: true),
            Lap(2, "Alice", pitEntry: sticky, pitDurationMs: 30_000),
            Lap(3, "Alice", pitEntry: sticky, pitDurationMs: 30_000));

        Assert.AreEqual(0, stops.Count);
    }

    /// <summary>
    /// The same shape, from car 4 lap 19, where the understatement was worse: 8s reported for a 48s
    /// stop.
    /// </summary>
    [TestMethod]
    public void StopCaughtEarlyAtTheCrossing_TakesTheLaterDuration()
    {
        var entry = SessionStart.AddMinutes(40);
        var stops = Run(
            Lap(18, "Alice"),
            Lap(19, "Alice", pitEntry: entry, pitDurationMs: 8_000, lapIncludedPit: true, isInPit: true),
            Lap(20, "Alice", pitEntry: entry, pitDurationMs: 48_000, lapIncludedPit: true));

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual(48_000, stops[0].DurationMs);
        Assert.AreEqual(19, stops[0].StartLap);
        Assert.AreEqual(20, stops[0].EndLap);
    }

    /// <summary>
    /// An ordinary stop that fits inside one lap still reports a single lap on both ends, so the
    /// range only widens when it means something.
    /// </summary>
    [TestMethod]
    public void OrdinaryStop_StartsAndEndsOnTheSameLap()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Alice"),
            Lap(3, "Bob", pitEntry: entry, pitDurationMs: 62_000, lapIncludedPit: true),
            Lap(4, "Bob", pitEntry: entry, pitDurationMs: 62_000));

        Assert.AreEqual(3, stops[0].StartLap);
        Assert.AreEqual(3, stops[0].EndLap);
    }

    /// <summary>
    /// A repeated duration on a lap the car was long gone must not keep stretching the stop, or one
    /// stop would swallow the rest of the session.
    /// </summary>
    [TestMethod]
    public void RepeatedDurationAfterTheStop_DoesNotExtendIt()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Alice", pitEntry: entry, pitDurationMs: 30_000, lapIncludedPit: true),
            Lap(3, "Alice", pitEntry: entry, pitDurationMs: 30_000),
            Lap(4, "Alice", pitEntry: entry, pitDurationMs: 30_000),
            Lap(5, "Alice", pitEntry: entry, pitDurationMs: 30_000));

        Assert.AreEqual(2, stops[0].StartLap);
        Assert.AreEqual(2, stops[0].EndLap);
    }

    /// <summary>
    /// Equipment whose clock was never set reports a year-0001 entry time. It is the only signal that
    /// a stop happened, so it still finds the stop - but it is not a time and is not printed as one.
    /// The duration, which is the trustworthy field, is reported as normal.
    /// </summary>
    [TestMethod]
    public void ImplausibleEntryTime_FindsTheStopButReportsNoTimes()
    {
        var bogus = new DateTime(1, 4, 11, 12, 12, 45, DateTimeKind.Utc);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Bob", pitEntry: bogus, pitDurationMs: 47_000, lapIncludedPit: true),
            Lap(3, "Bob", pitEntry: bogus, pitDurationMs: 47_000));

        Assert.AreEqual(1, stops.Count);
        Assert.IsTrue(stops[0].EntryTimeUnavailable);
        Assert.IsNull(stops[0].EntryTime);
        Assert.IsNull(stops[0].ExitTime);
        Assert.AreEqual(47_000, stops[0].DurationMs, "the duration is unaffected");
        Assert.AreEqual("Bob", stops[0].DriverAfter);
    }

    [TestMethod]
    public void PlausibleEntryTime_IsNotMarkedUnavailable()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Bob", pitEntry: entry, pitDurationMs: 47_000, lapIncludedPit: true),
            Lap(3, "Bob", pitEntry: entry, pitDurationMs: 47_000));

        Assert.IsFalse(stops[0].EntryTimeUnavailable);
        Assert.AreEqual(entry, stops[0].EntryTime);
    }

    [TestMethod]
    public void TwoStops_AreNumberedAndOrdered()
    {
        var first = SessionStart.AddMinutes(20);
        var second = SessionStart.AddMinutes(70);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Bob", pitEntry: first, pitDurationMs: 60_000),
            Lap(3, "Bob", pitEntry: first, pitDurationMs: 60_000),
            Lap(4, "Carol", pitEntry: second, pitDurationMs: 45_000),
            Lap(5, "Carol", pitEntry: second, pitDurationMs: 45_000));

        Assert.AreEqual(2, stops.Count);
        Assert.AreEqual(1, stops[0].StopNumber);
        Assert.AreEqual("Alice", stops[0].DriverBefore);
        Assert.AreEqual("Bob", stops[0].DriverAfter);
        Assert.AreEqual(2, stops[1].StopNumber);
        Assert.AreEqual("Bob", stops[1].DriverBefore);
        Assert.AreEqual("Carol", stops[1].DriverAfter);
    }

    /// <summary>
    /// Failure mode: the feed gave an entry but never a duration. The stop is still worth reporting
    /// for the driver change; what is not known is left null rather than guessed at.
    /// </summary>
    [TestMethod]
    public void MissedExit_ReportsTheStopWithNoDurationOrExit()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Bob", pitEntry: entry),
            Lap(3, "Bob", pitEntry: entry));

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual(entry, stops[0].EntryTime);
        Assert.IsNull(stops[0].DurationMs);
        Assert.IsNull(stops[0].ExitTime);
        Assert.IsTrue(stops[0].DriverChanged);
    }

    /// <summary>
    /// A duration that was still counting when the car crossed the line is finalized on the next lap.
    /// </summary>
    [TestMethod]
    public void DurationArrivingALapLate_IsBackFilled()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Bob", pitEntry: entry),
            Lap(3, "Bob", pitEntry: entry, pitDurationMs: 71_500));

        Assert.AreEqual(71_500, stops[0].DurationMs);
        Assert.AreEqual(entry.AddMilliseconds(71_500), stops[0].ExitTime);
    }

    /// <summary>
    /// Failure mode: a same-driver stop and a stop where the driver feed never moved are the same
    /// row here. Both report no change, and nothing in the data distinguishes them.
    /// </summary>
    [TestMethod]
    public void DriverNeverChanges_ReportsTheStopWithNoChange()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Alice", pitEntry: entry, pitDurationMs: 30_000),
            Lap(3, "Alice", pitEntry: entry, pitDurationMs: 30_000));

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual("Alice", stops[0].DriverBefore);
        Assert.AreEqual("Alice", stops[0].DriverAfter);
        Assert.IsFalse(stops[0].DriverChanged);
    }

    /// <summary>
    /// The driver enricher needs to see the puck before the car crosses start/finish, so the change
    /// can land on the lap after the pit lap. That one is still this stop's change.
    /// </summary>
    [TestMethod]
    public void DriverChangeOneLapAfterTheStop_IsAttributedToTheStop()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Alice", pitEntry: entry, pitDurationMs: 60_000),
            Lap(3, "Bob", pitEntry: entry, pitDurationMs: 60_000));

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual("Alice", stops[0].DriverBefore);
        Assert.AreEqual("Bob", stops[0].DriverAfter);
        Assert.IsTrue(stops[0].DriverChanged);
    }

    /// <summary>
    /// Two laps later is a mid-stint change - a manual override, or the puck being re-read - and
    /// attributing it to the stop would invent a driver change that did not happen there.
    /// </summary>
    [TestMethod]
    public void DriverChangeTwoLapsAfterTheStop_IsNotAttributedToTheStop()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Alice", pitEntry: entry, pitDurationMs: 60_000),
            Lap(3, "Alice", pitEntry: entry, pitDurationMs: 60_000),
            Lap(4, "Bob", pitEntry: entry, pitDurationMs: 60_000));

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual("Alice", stops[0].DriverAfter);
        Assert.IsFalse(stops[0].DriverChanged);
    }

    /// <summary>
    /// Failure mode: a stop on the car's last recorded lap has no following lap to correct a driver
    /// update that had not arrived yet, so it reports the pre-stop driver on both sides.
    /// </summary>
    [TestMethod]
    public void StopOnTheFinalLap_CannotSeeALateDriverChange()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Alice", pitEntry: entry, pitDurationMs: 60_000));

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual("Alice", stops[0].DriverBefore);
        Assert.AreEqual("Alice", stops[0].DriverAfter);
        Assert.IsFalse(stops[0].DriverChanged);
    }

    /// <summary>
    /// Without Flagtronics there are no entry times at all, only the timing system's own pit lap
    /// flag. A stop with no times is still a stop.
    /// </summary>
    [TestMethod]
    public void NoEntryTimes_FallsBackToTheLapIncludedPitRisingEdge()
    {
        var stops = Run(
            Lap(1, "Alice", noEntryTime: true),
            Lap(2, "Alice", noEntryTime: true),
            Lap(3, "Alice", noEntryTime: true, lapIncludedPit: true),
            Lap(4, "Alice", noEntryTime: true));

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual(3, stops[0].StartLap);
        Assert.IsNull(stops[0].EntryTime);
        Assert.IsNull(stops[0].DurationMs);
    }

    [TestMethod]
    public void LapIncludedPitHeldHigh_CountsAsOneStop()
    {
        var stops = Run(
            Lap(1, "Alice", noEntryTime: true),
            Lap(2, "Alice", noEntryTime: true, lapIncludedPit: true),
            Lap(3, "Alice", noEntryTime: true, lapIncludedPit: true),
            Lap(4, "Alice", noEntryTime: true, lapIncludedPit: true));

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual(2, stops[0].StartLap);
    }

    /// <summary>
    /// Once a session has shown it reports entry times, the fallback stays off. Otherwise a lap the
    /// timing system flagged as a pit lap, on the same stop the entry time already described, would
    /// be counted twice.
    /// </summary>
    [TestMethod]
    public void EntryTimesPresent_SuppressTheLapIncludedPitFallback()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Bob", pitEntry: entry, pitDurationMs: 60_000, lapIncludedPit: true),
            Lap(3, "Bob", pitEntry: entry, pitDurationMs: 60_000),
            Lap(4, "Bob", noEntryTime: true, lapIncludedPit: true));

        Assert.AreEqual(1, stops.Count);
    }

    /// <summary>
    /// A zero duration is the field's uninitialized state, not a stop that took no time.
    /// </summary>
    [TestMethod]
    public void ZeroDuration_IsTreatedAsUnknown()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Bob", pitEntry: entry, pitDurationMs: 0),
            Lap(3, "Bob", pitEntry: entry, pitDurationMs: 0));

        Assert.AreEqual(1, stops.Count);
        Assert.IsNull(stops[0].DurationMs);
        Assert.IsNull(stops[0].ExitTime);
    }

    /// <summary>
    /// The first row a car has always carries a sticky entry time describing something that happened
    /// before it, so on its own it says nothing. Taking it as a stop gave every car in a real session
    /// a phantom stop on lap 1. It establishes the baseline instead.
    /// </summary>
    [TestMethod]
    public void FirstLapWithAStickyEntryTime_EstablishesTheBaselineRatherThanAStop()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, "Alice", pitEntry: entry, pitDurationMs: 60_000),
            Lap(2, "Alice", pitEntry: entry, pitDurationMs: 60_000));

        Assert.AreEqual(0, stops.Count);
    }

    /// <summary>
    /// Same for a car whose earlier laps are missing entirely - the first surviving row is still only
    /// a baseline. The cost is the other side of that trade: a car that genuinely pitted on its first
    /// surviving lap, with nothing on the row to prove it, is missed.
    /// </summary>
    [TestMethod]
    public void TruncatedLapHistory_TakesTheFirstSurvivingLapAsTheBaseline()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(9, "Bob", pitEntry: entry, pitDurationMs: 60_000),
            Lap(10, "Bob", pitEntry: entry, pitDurationMs: 60_000));

        Assert.AreEqual(0, stops.Count);
    }

    /// <summary>
    /// A car that really did pit on its first lap says so on the row, and that is a stop.
    /// </summary>
    [TestMethod]
    public void FirstLapShowingPitInvolvement_IsAStop()
    {
        var entry = SessionStart.AddMinutes(5);
        var stops = Run(
            Lap(1, "Alice", pitEntry: entry, pitDurationMs: 45_000, lapIncludedPit: true),
            Lap(2, "Bob", pitEntry: entry, pitDurationMs: 45_000));

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual(1, stops[0].StartLap);
    }

    [TestMethod]
    public void DriverNames_AreTrimmedAndComparedCaseInsensitively()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(1, " Alice "),
            Lap(2, "alice", pitEntry: entry, pitDurationMs: 60_000),
            Lap(3, "alice", pitEntry: entry, pitDurationMs: 60_000));

        Assert.AreEqual("Alice", stops[0].DriverBefore);
        Assert.IsFalse(stops[0].DriverChanged);
    }
}
