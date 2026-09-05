using RedMist.EventProcessor.EventStatus.SessionMonitoring.Metrics;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.SessionMonitoring;

/// <summary>
/// The race metrics an event with no Multiloop feed would otherwise never get. Most events are
/// RMonitor only, so in practice these fields are null everywhere until they are derived - and a
/// derived number that contradicts what the timing screen showed during the race is worse than the
/// null it replaced, so most of what is tested here is the deriver declining to answer.
/// </summary>
[TestClass]
public class SessionMetricsDeriverTests
{
    private static readonly DateTime Start = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    private readonly SessionMetricsDeriver deriver = new();

    #region Caution counting

    /// <summary>
    /// A waving yellow is a corner worker warning the cars about something at their station. The
    /// race is still green everywhere else, and counting it would report cautions that never
    /// happened.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_DoesNotCountAWavingYellowAsAFullCourseCaution()
    {
        var state = StateWith(
            Flag(Flags.Green, 0, 600),
            Flag(Flags.WavingYellow, 600, 660),
            Flag(Flags.Green, 660, 1200),
            Flag(Flags.Yellow, 1200, 1500),
            Flag(Flags.Green, 1500, 1800));

        deriver.ApplyFlagMetrics(state, eventLapDistance: null);

        Assert.AreEqual(1, state.NumberOfYellows);
        Assert.AreEqual(300_000, state.YellowTimeMs, "Only the full-course yellow counts towards yellow time.");
    }

    /// <summary>
    /// The flag log is not stored in order, and one caution is regularly written as several
    /// consecutive entries for the same flag. Collapsing them is what stops a single caution being
    /// reported as three.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_CollapsesConsecutiveEntriesOfTheSameFlag()
    {
        var state = StateWith(
            Flag(Flags.Yellow, 1200, 1300),
            Flag(Flags.Green, 0, 1200),
            Flag(Flags.Yellow, 1300, 1400),
            Flag(Flags.Yellow, 1400, 1500),
            Flag(Flags.Green, 1500, 1800));

        deriver.ApplyFlagMetrics(state, eventLapDistance: null);

        Assert.AreEqual(1, state.NumberOfYellows, "Three consecutive yellow entries are one caution.");
        Assert.AreEqual(300_000, state.YellowTimeMs);
        Assert.AreEqual(1_500_000, state.GreenTimeMs);
    }

    [TestMethod]
    public void ApplyFlagMetrics_CountsSeparateCautionsSeparately()
    {
        var state = StateWith(
            Flag(Flags.Green, 0, 600),
            Flag(Flags.Yellow, 600, 900),
            Flag(Flags.Green, 900, 1500),
            Flag(Flags.Yellow, 1500, 1800),
            Flag(Flags.Green, 1800, 2400));

        deriver.ApplyFlagMetrics(state, eventLapDistance: null);

        Assert.AreEqual(2, state.NumberOfYellows);
        Assert.AreEqual(600_000, state.YellowTimeMs);
    }

    [TestMethod]
    public void ApplyFlagMetrics_TotalsRedFlagTimeSeparately()
    {
        var state = StateWith(
            Flag(Flags.Green, 0, 600),
            Flag(Flags.Red, 600, 2400),
            Flag(Flags.Green, 2400, 3000));

        deriver.ApplyFlagMetrics(state, eventLapDistance: null);

        Assert.AreEqual(1_800_000, state.RedTimeMs);
        Assert.AreEqual(0, state.NumberOfYellows);
        Assert.AreEqual(1_200_000, state.GreenTimeMs);
    }

    /// <summary>
    /// Every finished session in production has exactly one open flag period: the checkered flag is
    /// never followed by another, so nothing ever closes it.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_ClosesAFinalPeriodWithNoEndTimeAtTheEndOfTheSession()
    {
        var state = StateWith(
            Flag(Flags.Green, 0, 600),
            Flag(Flags.Yellow, 600, null));
        state.RunningRaceTime = "00:20:00";

        deriver.ApplyFlagMetrics(state, eventLapDistance: null);

        Assert.AreEqual(1, state.NumberOfYellows, "An open final period is counted, not dropped.");
        Assert.AreEqual(600_000, state.YellowTimeMs,
            "The open period runs from its start to the end of the session, not to zero.");
    }

    /// <summary>
    /// With no race clock there is nothing to measure the open period against, so it closes at the
    /// last timestamp in the log. It is still counted - a caution that ran to the end of the session
    /// happened.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_WithNoRaceClock_StillCountsAnOpenFinalPeriod()
    {
        var state = StateWith(
            Flag(Flags.Green, 0, 600),
            Flag(Flags.Yellow, 600, null));
        state.RunningRaceTime = string.Empty;

        deriver.ApplyFlagMetrics(state, eventLapDistance: null);

        Assert.AreEqual(1, state.NumberOfYellows);
        Assert.AreEqual(0, state.YellowTimeMs);
    }

    [TestMethod]
    public void ApplyFlagMetrics_PrefersTheSessionEndTimeForClosingAnOpenPeriod()
    {
        var state = StateWith(
            Flag(Flags.Green, 0, 600),
            Flag(Flags.Yellow, 600, null));
        state.RunningRaceTime = "00:20:00";
        state.SessionEndTime = Start.AddSeconds(900);

        deriver.ApplyFlagMetrics(state, eventLapDistance: null);

        Assert.AreEqual(300_000, state.YellowTimeMs);
    }

    [TestMethod]
    public void ApplyFlagMetrics_WithNoFlagsAtAll_LeavesTheCountersAlone()
    {
        var state = StateWith();

        deriver.ApplyFlagMetrics(state, eventLapDistance: null);

        Assert.IsNull(state.NumberOfYellows);
        Assert.IsNull(state.GreenTimeMs);
        Assert.IsNull(state.YellowTimeMs);
        Assert.IsNull(state.RedTimeMs);
    }

    /// <summary>
    /// A code-35 neutralizes the whole field, so it is a full-course caution whatever colour it is
    /// shown in. It also stands in for the yellow rather than accompanying it - the flag processor
    /// drops a caution whose start time a purple override has taken over - so two production races
    /// that ran six and eight full-course cautions record no yellow flag at all.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_CountsAFullCoursePurpleAsACaution()
    {
        var state = StateWith(
            Flag(Flags.Green, 0, 600),
            Flag(Flags.Purple35, 600, 900),
            Flag(Flags.Green, 900, 1500),
            Flag(Flags.Purple60, 1500, 1800),
            Flag(Flags.Green, 1800, 2400));

        deriver.ApplyFlagMetrics(state, eventLapDistance: null);

        Assert.AreEqual(2, state.NumberOfYellows);
        Assert.AreEqual(600_000, state.YellowTimeMs);
        Assert.AreEqual(1_800_000, state.GreenTimeMs);
    }

    /// <summary>
    /// A caution that is shown yellow for a few seconds and then runs on under a purple speed limit
    /// is one caution, which is how it reaches the flag log at every event that uses both.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_TreatsAYellowRunningOnUnderPurpleAsOneCaution()
    {
        var state = StateWith(
            Flag(Flags.Green, 0, 600),
            Flag(Flags.Yellow, 600, 608),
            Flag(Flags.Purple35, 608, 1020),
            Flag(Flags.Green, 1020, 1800));

        deriver.ApplyFlagMetrics(state, eventLapDistance: null);

        Assert.AreEqual(1, state.NumberOfYellows, "The yellow and the purple it ran into are one caution.");
        Assert.AreEqual(420_000, state.YellowTimeMs, "Caution time covers the whole neutralized period.");
    }

    [TestMethod]
    public void TryApplyLapMetrics_CountsALeaderLapUnderPurpleAsAYellowLap()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 3)];

        var log = new FakeLapLog(
            Lap("1", 1, Flags.Green, overall: 1, inClass: 1),
            Lap("1", 2, Flags.Purple35, overall: 1, inClass: 1),
            Lap("1", 3, Flags.Green, overall: 1, inClass: 1));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log));

        Assert.AreEqual(2, state.GreenLaps);
        Assert.AreEqual(1, state.YellowLaps);
    }

    #endregion

    #region Not overwriting what the feed supplied

    /// <summary>
    /// A Multiloop feed is the authority on these, and it is what the viewers watched during the
    /// race. The check is per field: a feed can supply some and not others.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_LeavesValuesTheFeedAlreadySupplied()
    {
        var state = StateWith(
            Flag(Flags.Green, 0, 600),
            Flag(Flags.Yellow, 600, 900),
            Flag(Flags.Green, 900, 1500));
        state.NumberOfYellows = 4;
        state.YellowTimeMs = 999;
        state.AverageRaceSpeed = "81.63";
        state.RunningRaceTime = "00:25:00";
        state.CarPositions = [Car("1", overall: 1, laps: 20)];

        deriver.ApplyFlagMetrics(state, "2.5");

        Assert.AreEqual(4, state.NumberOfYellows, "Multiloop said four cautions; the derived two must not replace it.");
        Assert.AreEqual(999, state.YellowTimeMs);
        Assert.AreEqual("81.63", state.AverageRaceSpeed);
        Assert.AreEqual(1_200_000, state.GreenTimeMs, "The fields the feed did not supply are still filled in.");
        Assert.AreEqual(0, state.RedTimeMs);
    }

    [TestMethod]
    public void TryApplyLapMetrics_LeavesLeadChangesAndLapsLedTheFeedAlreadySupplied()
    {
        var state = StateWith();
        state.LeadChanges = 7;
        state.GreenLaps = 30;
        state.YellowLaps = 2;
        state.CarPositions = [
            Car("1", overall: 1, laps: 32, lapsLedOverall: 25, lapsLedInClass: 25),
            Car("2", overall: 2, laps: 32, lapsLedOverall: 7, lapsLedInClass: 7)];

        var log = new FakeLapLog(
            Lap("2", 1, Flags.Green, overall: 1, inClass: 1),
            Lap("2", 2, Flags.Green, overall: 1, inClass: 1));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log));

        Assert.AreEqual(7, state.LeadChanges);
        Assert.AreEqual(30, state.GreenLaps);
        Assert.AreEqual(25, state.CarPositions[0].LapsLedOverall);
        Assert.AreEqual(7, state.CarPositions[1].LapsLedOverall);
        Assert.IsFalse(log.LapsWereRead, "Nothing was missing, so the lap log was never opened.");
    }

    /// <summary>
    /// Laps led are written as a set or not at all. A feed that supplied them for some cars and not
    /// others still supplied them, and filling the gaps here would put two different measurements
    /// in one column.
    /// </summary>
    [TestMethod]
    public void TryApplyLapMetrics_WhenTheFeedSuppliedLapsLedForOnlySomeCars_LeavesThemAllAlone()
    {
        var state = StateWith();
        state.CarPositions = [
            Car("1", overall: 1, laps: 2, lapsLedOverall: 2),
            Car("2", overall: 2, laps: 2)];

        var log = new FakeLapLog(
            Lap("1", 1, Flags.Green, overall: 1, inClass: 1),
            Lap("1", 2, Flags.Green, overall: 1, inClass: 1),
            Lap("2", 1, Flags.Green, overall: 2, inClass: 2),
            Lap("2", 2, Flags.Green, overall: 2, inClass: 2));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log));

        Assert.AreEqual(2, state.CarPositions[0].LapsLedOverall);
        Assert.IsNull(state.CarPositions[1].LapsLedOverall, "The gap is left rather than filled from another source.");
        Assert.AreEqual(0, state.LeadChanges, "The fields the feed did not supply are still filled in.");
    }

    #endregion

    #region Lead changes and laps led

    [TestMethod]
    public void TryApplyLapMetrics_CountsALeadChangeEachTimeTheCarInFirstChanges()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 3, cls: "GTO"), Car("2", overall: 2, laps: 3, cls: "GTO")];

        var log = new FakeLapLog(
            Lap("1", 1, Flags.Green, overall: 1, inClass: 1),
            Lap("2", 1, Flags.Green, overall: 2, inClass: 2),
            Lap("2", 2, Flags.Green, overall: 1, inClass: 1),
            Lap("1", 2, Flags.Green, overall: 2, inClass: 2),
            Lap("1", 3, Flags.Green, overall: 1, inClass: 1),
            Lap("2", 3, Flags.Green, overall: 2, inClass: 2));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log));

        Assert.AreEqual(2, state.LeadChanges);
        Assert.AreEqual(2, state.CarPositions[0].LapsLedOverall);
        Assert.AreEqual(1, state.CarPositions[1].LapsLedOverall);
    }

    /// <summary>
    /// A leader who gives the place up and takes it straight back changes the lead twice, which is
    /// how lead changes are counted in racing.
    /// </summary>
    [TestMethod]
    public void TryApplyLapMetrics_CountsTheLeadChangingBackAsASecondChange()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 2), Car("2", overall: 2, laps: 1)];

        var log = new FakeLapLog(
            Lap("1", 1, Flags.Green, overall: 1, inClass: 1),
            Lap("2", 1, Flags.Green, overall: 1, inClass: 1),
            Lap("1", 2, Flags.Green, overall: 1, inClass: 1));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log));

        Assert.AreEqual(2, state.LeadChanges);
    }

    [TestMethod]
    public void TryApplyLapMetrics_CountsLapsLedInEachClassSeparately()
    {
        var state = StateWith();
        state.CarPositions = [
            Car("1", overall: 1, laps: 2, cls: "GTO"),
            Car("2", overall: 2, laps: 2, cls: "GTO"),
            Car("9", overall: 3, laps: 2, cls: "GTU")];

        var log = new FakeLapLog(
            Lap("1", 1, Flags.Green, overall: 1, inClass: 1),
            Lap("2", 1, Flags.Green, overall: 2, inClass: 2),
            Lap("9", 1, Flags.Green, overall: 3, inClass: 1),
            Lap("1", 2, Flags.Green, overall: 1, inClass: 1),
            Lap("2", 2, Flags.Green, overall: 2, inClass: 2),
            Lap("9", 2, Flags.Green, overall: 3, inClass: 1));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log));

        Assert.AreEqual(2, state.CarPositions[0].LapsLedInClass);
        Assert.AreEqual(0, state.CarPositions[1].LapsLedInClass, "A car that never led its class led none of it.");
        Assert.AreEqual(2, state.CarPositions[2].LapsLedInClass,
            "The class leader led its class throughout, even though it was never the overall leader.");
        Assert.AreEqual(0, state.CarPositions[2].LapsLedOverall);
    }

    /// <summary>
    /// Green and yellow laps describe the race rather than any one car, so they are the leader's
    /// laps - which is what Multiloop reports.
    /// </summary>
    [TestMethod]
    public void TryApplyLapMetrics_CountsTheLeaderLapsRunUnderEachFlag()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 4), Car("2", overall: 2, laps: 4)];

        var log = new FakeLapLog(
            Lap("1", 1, Flags.Green, overall: 1, inClass: 1),
            Lap("2", 1, Flags.Green, overall: 2, inClass: 2),
            Lap("1", 2, Flags.Yellow, overall: 1, inClass: 1),
            Lap("2", 2, Flags.Yellow, overall: 2, inClass: 2),
            Lap("1", 3, Flags.Yellow, overall: 1, inClass: 1),
            Lap("1", 4, Flags.Checkered, overall: 1, inClass: 1));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log));

        Assert.AreEqual(1, state.GreenLaps, "Only the leader's laps count, not the whole field's.");
        Assert.AreEqual(2, state.YellowLaps);
    }

    #endregion

    #region Degenerate sessions

    [TestMethod]
    public void TryApplyLapMetrics_WithNoCars_DoesNotThrowAndSettles()
    {
        var state = StateWith();

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, new FakeLapLog()));

        Assert.IsNull(state.LeadChanges);
    }

    /// <summary>
    /// A session where nobody completed a lap has nothing to derive and nothing to wait for, so it
    /// must settle rather than being retried until its deadline.
    /// </summary>
    [TestMethod]
    public void TryApplyLapMetrics_WhereNobodyCompletedALap_Settles()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 0), Car("2", overall: 2, laps: 0)];

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, new FakeLapLog()));

        Assert.IsNull(state.LeadChanges);
    }

    [TestMethod]
    public void TryApplyLapMetrics_WithASingleCarInASingleClass_DoesNotThrow()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 2, cls: "GTO")];

        var log = new FakeLapLog(
            Lap("1", 1, Flags.Green, overall: 1, inClass: 1),
            Lap("1", 2, Flags.Green, overall: 1, inClass: 1));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log));

        Assert.AreEqual(0, state.LeadChanges, "One car never handed the lead over.");
        Assert.AreEqual(2, state.CarPositions[0].LapsLedOverall);
        Assert.AreEqual(2, state.CarPositions[0].LapsLedInClass);
    }

    [TestMethod]
    public void TryApplyLapMetrics_WithACarThatHasNoNumber_SkipsIt()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 1), Car(null, overall: 2, laps: 1)];

        var log = new FakeLapLog(Lap("1", 1, Flags.Green, overall: 1, inClass: 1));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log));

        Assert.AreEqual(1, state.CarPositions[0].LapsLedOverall);
        Assert.IsNull(state.CarPositions[1].LapsLedOverall);
    }

    #endregion

    #region Lap log coverage

    /// <summary>
    /// The lap log is written by a different service consuming a Redis stream, so at the moment a
    /// session is finalized its tail may not have reached the database. Deriving from a short log
    /// would undercount the end of the race, which is the part that decides the winner.
    /// </summary>
    [TestMethod]
    public void TryApplyLapMetrics_WhenTheLogHasNotReachedTheLeaderLastLap_WritesNothing()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 4), Car("2", overall: 2, laps: 4)];

        var log = new FakeLapLog(
            Lap("1", 1, Flags.Green, overall: 1, inClass: 1),
            Lap("2", 1, Flags.Green, overall: 2, inClass: 2),
            Lap("1", 2, Flags.Green, overall: 1, inClass: 1),
            Lap("2", 2, Flags.Green, overall: 2, inClass: 2),
            Lap("1", 3, Flags.Green, overall: 1, inClass: 1),
            Lap("2", 3, Flags.Green, overall: 2, inClass: 2));

        Assert.IsFalse(deriver.TryApplyLapMetrics(state, log), "The log stops a lap short of the finish.");

        Assert.IsNull(state.LeadChanges);
        Assert.IsNull(state.CarPositions[0].LapsLedOverall);
    }

    /// <summary>
    /// The scratch run a timing system announces at every run change takes a copy of the outgoing
    /// session's field, but has a lap log of its own holding about one lap per car. Its last lap
    /// number matches, so only the coverage check catches it.
    /// </summary>
    [TestMethod]
    public void TryApplyLapMetrics_WhenTheLogHoldsOnlyTheLastLapOfEachCar_WritesNothing()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 10), Car("2", overall: 2, laps: 10)];

        var log = new FakeLapLog(
            Lap("1", 10, Flags.Checkered, overall: 1, inClass: 1),
            Lap("2", 10, Flags.Checkered, overall: 2, inClass: 2));

        Assert.IsFalse(deriver.TryApplyLapMetrics(state, log),
            "Two rows cannot answer who led twenty laps, even though both cars reached their last lap.");

        Assert.IsNull(state.LeadChanges);
    }

    /// <summary>
    /// The log routinely runs a lap or two ahead of the finishing snapshot, which is complete rather
    /// than short.
    /// </summary>
    [TestMethod]
    public void TryApplyLapMetrics_WhenTheLogRunsPastTheSnapshot_StillDerives()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 2)];

        var log = new FakeLapLog(
            Lap("1", 1, Flags.Green, overall: 1, inClass: 1),
            Lap("1", 2, Flags.Green, overall: 1, inClass: 1),
            Lap("1", 3, Flags.Green, overall: 1, inClass: 1));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log));

        Assert.AreEqual(3, state.CarPositions[0].LapsLedOverall);
    }

    /// <summary>
    /// The event logger reads its stream without ever reclaiming what it did not acknowledge, so an
    /// outage loses that window outright. The tail then arrives and the leader's last lap looks
    /// present, which is why the leader's own row count is checked and not just how far it got.
    /// </summary>
    [TestMethod]
    public void TryApplyLapMetrics_WhenTheLogIsMissingLapsFromTheMiddle_WritesNothing()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 10), Car("2", overall: 2, laps: 10)];

        // The leader reached lap 10 and half the field's laps are there, but four of its own are not.
        var laps = new List<LoggedLap>();
        foreach (var lap in new[] { 1, 2, 3, 4, 5, 10 })
            laps.Add(Lap("1", lap, Flags.Green, overall: 1, inClass: 1));
        for (var lap = 1; lap <= 10; lap++)
            laps.Add(Lap("2", lap, Flags.Green, overall: 2, inClass: 2));

        Assert.IsFalse(deriver.TryApplyLapMetrics(state, new FakeLapLog([.. laps])));

        Assert.IsNull(state.LeadChanges);
    }

    /// <summary>
    /// A log whose positions never resolved would otherwise be written out as a race that nobody led
    /// and in which nobody led a lap, which reads as a fact rather than as the absence of one.
    /// </summary>
    [TestMethod]
    public void TryApplyLapMetrics_WhenNoLoggedLapHasALeader_WritesNothing()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 2)];

        var log = new FakeLapLog(
            Lap("1", 1, Flags.Green, overall: 0, inClass: 0),
            Lap("1", 2, Flags.Green, overall: 0, inClass: 0));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log), "Another attempt would read the same rows.");

        Assert.IsNull(state.LeadChanges);
        Assert.IsNull(state.GreenLaps);
        Assert.IsNull(state.CarPositions[0].LapsLedOverall);
    }

    /// <summary>
    /// The flag on a logged lap is copied from the session's current flag, which is Unknown until the
    /// first one arrives. Zero green laps beside a green time running into hours would contradict
    /// itself.
    /// </summary>
    [TestMethod]
    public void TryApplyLapMetrics_WhenNoLoggedLapCarriesAFlag_LeavesTheLapCountsNull()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 2)];

        var log = new FakeLapLog(
            Lap("1", 1, Flags.Unknown, overall: 1, inClass: 1),
            Lap("1", 2, Flags.Unknown, overall: 1, inClass: 1));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log));

        Assert.IsNull(state.GreenLaps);
        Assert.IsNull(state.YellowLaps);
        Assert.AreEqual(0, state.LeadChanges, "Who led is still known.");
        Assert.AreEqual(2, state.CarPositions[0].LapsLedOverall);
    }

    /// <summary>
    /// A car that never appears in the log at all led nothing, which is a fact about the race rather
    /// than a gap in the data.
    /// </summary>
    [TestMethod]
    public void TryApplyLapMetrics_ForACarWithNoLoggedLaps_RecordsZeroLapsLed()
    {
        var state = StateWith();
        state.CarPositions = [Car("1", overall: 1, laps: 2), Car("77", overall: 2, laps: 0)];

        var log = new FakeLapLog(
            Lap("1", 1, Flags.Green, overall: 1, inClass: 1),
            Lap("1", 2, Flags.Green, overall: 1, inClass: 1));

        Assert.IsTrue(deriver.TryApplyLapMetrics(state, log));

        Assert.AreEqual(0, state.CarPositions[1].LapsLedOverall);
        Assert.AreEqual(0, state.CarPositions[1].LapsLedInClass);
    }

    #endregion

    #region Average race speed

    [TestMethod]
    public void ApplyFlagMetrics_DerivesTheAverageSpeedFromTheWinnerDistanceAndTheRaceClock()
    {
        var state = StateWith();
        state.RunningRaceTime = "02:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 100), Car("2", overall: 2, laps: 99)];

        deriver.ApplyFlagMetrics(state, "2.50");

        // 100 laps of 2.5 miles in two hours.
        Assert.AreEqual("125.00", state.AverageRaceSpeed);
    }

    /// <summary>
    /// The lap distance is free text the organizer types in, and about a fifth of the populated
    /// values carry a unit.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_ReadsALapDistanceWithAUnitSuffix()
    {
        var state = StateWith();
        state.RunningRaceTime = "01:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 50)];

        deriver.ApplyFlagMetrics(state, "1.474 mi");

        Assert.AreEqual("73.70", state.AverageRaceSpeed);
    }

    [TestMethod]
    public void ApplyFlagMetrics_ReadsALapDistanceSpelledOut()
    {
        var state = StateWith();
        state.RunningRaceTime = "01:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 30)];

        deriver.ApplyFlagMetrics(state, "2.87 miles");

        Assert.AreEqual("86.10", state.AverageRaceSpeed);
    }

    /// <summary>
    /// The organizer's lap distance is the only acceptable source. Track maps are not: one
    /// production event carries a 1747m map for a 4088m circuit, and the length guard only rejects
    /// maps that are too long.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_WithNoLapDistance_LeavesTheSpeedNull()
    {
        var state = StateWith();
        state.RunningRaceTime = "02:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 100)];

        deriver.ApplyFlagMetrics(state, string.Empty);

        Assert.IsNull(state.AverageRaceSpeed);
    }

    [TestMethod]
    public void ApplyFlagMetrics_WithAnUnreadableLapDistance_LeavesTheSpeedNull()
    {
        var state = StateWith();
        state.RunningRaceTime = "02:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 100)];

        deriver.ApplyFlagMetrics(state, "about three miles");

        Assert.IsNull(state.AverageRaceSpeed);
    }

    /// <summary>
    /// Stopping at the first character that is not part of a number would read a comma decimal
    /// separator as the end of the value, turning "1,474" into one mile - a third short, and still
    /// inside the plausible band.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_WithACommaDecimalSeparatorInTheLapDistance_LeavesTheSpeedNull()
    {
        var state = StateWith();
        state.RunningRaceTime = "01:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 50)];

        deriver.ApplyFlagMetrics(state, "1,474 mi");

        Assert.IsNull(state.AverageRaceSpeed);
    }

    [TestMethod]
    public void ApplyFlagMetrics_WithANonPositiveLapDistance_LeavesTheSpeedNull()
    {
        var state = StateWith();
        state.RunningRaceTime = "02:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 100)];

        deriver.ApplyFlagMetrics(state, "0");

        Assert.IsNull(state.AverageRaceSpeed);
    }

    /// <summary>
    /// One production event carries "238" as its lap distance, which is not miles. Nothing can tell
    /// that from the value itself, so the answer is checked instead.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_WhenTheAnswerIsImplausiblyFast_LeavesTheSpeedNull()
    {
        var state = StateWith();
        state.RunningRaceTime = "08:32:36";
        state.CarPositions = [Car("1", overall: 1, laps: 168)];

        deriver.ApplyFlagMetrics(state, "238");

        Assert.IsNull(state.AverageRaceSpeed);
    }

    [TestMethod]
    public void ApplyFlagMetrics_WhenTheAnswerIsImplausiblySlow_LeavesTheSpeedNull()
    {
        var state = StateWith();
        state.RunningRaceTime = "08:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 2)];

        deriver.ApplyFlagMetrics(state, "1.474");

        Assert.IsNull(state.AverageRaceSpeed);
    }

    /// <summary>
    /// The session's race clock keeps counting after the feed stops and is only stopped when the
    /// session is finalized, which can be hours later - one production session reports eleven and a
    /// half hours for a six hour race. The winner's own total time stops when the winner does.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_MeasuresTheRaceByTheWinnerTotalTimeRatherThanTheRaceClock()
    {
        var state = StateWith();
        state.RunningRaceTime = "04:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 100, totalTime: "02:00:00.000")];

        deriver.ApplyFlagMetrics(state, "2.50");

        Assert.AreEqual("125.00", state.AverageRaceSpeed,
            "The race lasted two hours; the clock ran on for another two after it ended.");
    }

    [TestMethod]
    public void ApplyFlagMetrics_WithNoTotalTimeOnTheWinner_FallsBackToTheRaceClock()
    {
        var state = StateWith();
        state.RunningRaceTime = "02:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 100)];

        deriver.ApplyFlagMetrics(state, "2.50");

        Assert.AreEqual("125.00", state.AverageRaceSpeed);
    }

    /// <summary>
    /// A race clock that ran on past the end of the session would otherwise stretch the last flag
    /// period to fill the gap - in one production session turning a forty second red flag into five
    /// and a half hours of one.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_ClosesAnOpenFinalPeriodByTheWinnerTotalTimeRatherThanTheRaceClock()
    {
        var state = StateWith(
            Flag(Flags.Green, 0, 1200),
            Flag(Flags.Red, 1200, null));
        state.RunningRaceTime = "04:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 20, totalTime: "00:21:00.000")];

        deriver.ApplyFlagMetrics(state, eventLapDistance: null);

        Assert.AreEqual(60_000, state.RedTimeMs, "The race ran a minute past the red flag, not two and a half hours.");
    }

    [TestMethod]
    public void ApplyFlagMetrics_WithNoRaceClock_LeavesTheSpeedNull()
    {
        var state = StateWith();
        state.RunningRaceTime = "00:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 100)];

        deriver.ApplyFlagMetrics(state, "2.5");

        Assert.IsNull(state.AverageRaceSpeed);
    }

    [TestMethod]
    public void ApplyFlagMetrics_WithNoCars_LeavesTheSpeedNull()
    {
        var state = StateWith();
        state.RunningRaceTime = "02:00:00";

        deriver.ApplyFlagMetrics(state, "2.5");

        Assert.IsNull(state.AverageRaceSpeed);
    }

    /// <summary>
    /// An endurance race clock passes twenty four hours, and the framework's exact parsers turn the
    /// rest of the race into zeros when it does.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_WithARaceClockPastTwentyFourHours_StillDerivesTheSpeed()
    {
        var state = StateWith();
        state.RunningRaceTime = "25:00:00";
        state.CarPositions = [Car("1", overall: 1, laps: 500)];

        deriver.ApplyFlagMetrics(state, "2.5");

        Assert.AreEqual("50.00", state.AverageRaceSpeed);
    }

    /// <summary>
    /// The winner is whoever the timing system put first. Falling back to the longest distance
    /// covered only matters for a session that ended without a first place at all.
    /// </summary>
    [TestMethod]
    public void ApplyFlagMetrics_WithNoCarInFirst_MeasuresTheCarThatWentFurthest()
    {
        var state = StateWith();
        state.RunningRaceTime = "02:00:00";
        state.CarPositions = [Car("1", overall: 0, laps: 80), Car("2", overall: 0, laps: 100)];

        deriver.ApplyFlagMetrics(state, "2.50");

        Assert.AreEqual("125.00", state.AverageRaceSpeed);
    }

    #endregion

    #region Helpers

    private static SessionState StateWith(params FlagDuration[] flags) => new()
    {
        EventId = 1,
        SessionId = 5,
        FlagDurations = [.. flags],
    };

    private static FlagDuration Flag(Flags flag, int fromSeconds, int? toSeconds) => new()
    {
        Flag = flag,
        StartTime = Start.AddSeconds(fromSeconds),
        EndTime = toSeconds is null ? null : Start.AddSeconds(toSeconds.Value),
    };

    private static CarPosition Car(string? number, int overall, int laps, string cls = "GTO",
        int? lapsLedOverall = null, int? lapsLedInClass = null, string? totalTime = null) => new()
        {
            Number = number,
            Class = cls,
            OverallPosition = overall,
            LastLapCompleted = laps,
            LapsLedOverall = lapsLedOverall,
            LapsLedInClass = lapsLedInClass,
            TotalTime = totalTime,
        };

    private static LoggedLap Lap(string car, int lapNumber, Flags flag, int overall, int inClass)
        => new(car, lapNumber, flag, overall, inClass);

    /// <summary>
    /// A lap log held in order, which is what the database reader hands over. Records whether the
    /// laps themselves were read, so a test can show the deriver did not go near them.
    /// </summary>
    private sealed class FakeLapLog(params LoggedLap[] laps) : ISessionLapLog
    {
        public bool LapsWereRead { get; private set; }

        public IReadOnlyDictionary<string, LoggedCarSummary> ReadSummary()
            => laps.GroupBy(l => l.CarNumber)
                   .ToDictionary(g => g.Key, g => new LoggedCarSummary(g.Count(), g.Max(l => l.LapNumber)));

        public IEnumerable<LoggedLap> ReadLaps()
        {
            LapsWereRead = true;
            return laps;
        }
    }

    #endregion
}
