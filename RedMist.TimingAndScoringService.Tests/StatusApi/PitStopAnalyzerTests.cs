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

    private static CarPosition Lap(int lapNumber, string driver = "", DateTime? pitEntry = null,
        int? pitDurationMs = null, bool lapIncludedPit = false) => new()
        {
            Number = "42",
            LastLapCompleted = lapNumber,
            DriverName = driver,
            PitEntryTime = pitEntry,
            PitDurationMs = pitDurationMs,
            LapIncludedPit = lapIncludedPit,
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
        Assert.AreEqual(3, stop.Lap);
        Assert.AreEqual(entry, stop.EntryTimeUtc);
        Assert.AreEqual(entry.AddMilliseconds(62_000), stop.ExitTimeUtc);
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
        Assert.AreEqual(entry, stops[0].EntryTimeUtc);
        Assert.IsNull(stops[0].DurationMs);
        Assert.IsNull(stops[0].ExitTimeUtc);
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
        Assert.AreEqual(entry.AddMilliseconds(71_500), stops[0].ExitTimeUtc);
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
            Lap(1, "Alice"),
            Lap(2, "Alice"),
            Lap(3, "Alice", lapIncludedPit: true),
            Lap(4, "Alice"));

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual(3, stops[0].Lap);
        Assert.IsNull(stops[0].EntryTimeUtc);
        Assert.IsNull(stops[0].DurationMs);
    }

    [TestMethod]
    public void LapIncludedPitHeldHigh_CountsAsOneStop()
    {
        var stops = Run(
            Lap(1, "Alice"),
            Lap(2, "Alice", lapIncludedPit: true),
            Lap(3, "Alice", lapIncludedPit: true),
            Lap(4, "Alice", lapIncludedPit: true));

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual(2, stops[0].Lap);
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
            Lap(4, "Bob", lapIncludedPit: true));

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
        Assert.IsNull(stops[0].ExitTimeUtc);
    }

    /// <summary>
    /// Failure mode, pinned rather than fixed: when a car's earlier laps are missing the first
    /// surviving row still carries the sticky entry time of a stop that happened before it, and that
    /// is reported as a stop on that lap with an unknown driver before it. Distinguishing it from a
    /// real stop would need lap history that, by definition, is not there.
    /// </summary>
    [TestMethod]
    public void TruncatedLapHistory_ReportsAPhantomStopOnTheFirstSurvivingLap()
    {
        var entry = SessionStart.AddMinutes(20);
        var stops = Run(
            Lap(9, "Bob", pitEntry: entry, pitDurationMs: 60_000),
            Lap(10, "Bob", pitEntry: entry, pitDurationMs: 60_000));

        Assert.AreEqual(1, stops.Count);
        Assert.AreEqual(9, stops[0].Lap);
        Assert.AreEqual(string.Empty, stops[0].DriverBefore);
        Assert.IsFalse(stops[0].DriverChanged, "an unknown driver on one side is not a change");
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
