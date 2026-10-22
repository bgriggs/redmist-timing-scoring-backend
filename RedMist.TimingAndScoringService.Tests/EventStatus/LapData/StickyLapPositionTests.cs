using RedMist.EventProcessor.EventStatus.LapData;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.LapData;

[TestClass]
public class StickyLapPositionTests
{
    private const double Length = 4000;
    private const double Offset = 120;
    private const double MaxSpeed = 90;
    private static readonly DateTime T0 = new(2026, 10, 18, 17, 0, 0, DateTimeKind.Utc);

    private StickyLapPosition _sticky = null!;

    [TestInitialize]
    public void Setup() => _sticky = new StickyLapPosition();

    private double Apply(double meters, int lap, double seconds, double length = Length, double offset = Offset) =>
        _sticky.Apply(meters, lap, length, offset, MaxSpeed, T0.AddSeconds(seconds));

    #region Forward

    [TestMethod]
    public void FirstReading_IsTakenAsItComes()
    {
        Assert.AreEqual(1500, Apply(1500, lap: 3, seconds: 0));
    }

    [TestMethod]
    public void MovingForward_IsAlwaysAccepted()
    {
        Apply(1500, lap: 3, seconds: 0);

        Assert.AreEqual(1560, Apply(1560, lap: 3, seconds: 1));
        Assert.AreEqual(1900, Apply(1900, lap: 3, seconds: 2));
    }

    [TestMethod]
    public void CrossingTheLineByGpsBeforeTheLapTicks_IsForward()
    {
        // 98% to 1% is a short step forward across the line, not most of a lap backwards.
        Apply(3920, lap: 3, seconds: 0);

        Assert.AreEqual(40, Apply(40, lap: 3, seconds: 1));
    }

    #endregion

    #region Backward

    [TestMethod]
    public void SmallStepBack_IsHeld()
    {
        Apply(1500, lap: 3, seconds: 0);

        Assert.AreEqual(1500, Apply(1470, lap: 3, seconds: 1));
        Assert.AreEqual(1500, Apply(1450, lap: 3, seconds: 2));
    }

    [TestMethod]
    public void SmallStepBack_IsHeldIndefinitely()
    {
        // A stopped car's fix wanders around where it sits. It never went backwards, so the bar
        // should not either, however long it waits.
        Apply(1500, lap: 3, seconds: 0);

        for (int s = 1; s <= 30; s++)
            Assert.AreEqual(1500, Apply(1500 - (s % 2 == 0 ? 50 : 10), lap: 3, seconds: s));
    }

    [TestMethod]
    public void HeldPoint_IsReleasedOnceTheCarPassesIt()
    {
        Apply(1500, lap: 3, seconds: 0);
        Apply(1470, lap: 3, seconds: 1);

        Assert.AreEqual(1510, Apply(1510, lap: 3, seconds: 2));
    }

    [TestMethod]
    public void LargeStepBack_IsHeldUntilConfirmed()
    {
        Apply(2000, lap: 3, seconds: 0);

        for (int s = 1; s < 6; s++)
            Assert.AreEqual(2000, Apply(1800, lap: 3, seconds: s), $"Still inside the confirmation window at {s}s");
    }

    [TestMethod]
    public void LargeStepBack_SustainedFiveSeconds_IsAccepted()
    {
        // The held point was a bad reading ahead of the car - or the car really did go backwards.
        // Either way, five seconds of readings agreeing is enough to believe them.
        Apply(2000, lap: 3, seconds: 0);
        for (int s = 1; s < 6; s++)
            Apply(1800, lap: 3, seconds: s);

        Assert.AreEqual(1800, Apply(1800, lap: 3, seconds: 6));
    }

    [TestMethod]
    public void LargeStepBack_InterruptedByAReadingNearTheHold_RestartsTheClock()
    {
        Apply(2000, lap: 3, seconds: 0);
        Apply(1800, lap: 3, seconds: 1);
        Apply(1800, lap: 3, seconds: 3);
        Apply(1990, lap: 3, seconds: 4); // agrees with the hold again

        Assert.AreEqual(2000, Apply(1800, lap: 3, seconds: 5));
        Assert.AreEqual(2000, Apply(1800, lap: 3, seconds: 9), "Only four seconds since the clock restarted");
        Assert.AreEqual(1800, Apply(1800, lap: 3, seconds: 10));
    }

    [TestMethod]
    public void ForwardGlitch_IsCorrectedOnceTheReadingsKeepDisagreeing()
    {
        // A reading far ahead of where the car can be - a snap onto the wrong part of the track -
        // is held like any other forward move, and corrected the same way as a car going backwards.
        Apply(1000, lap: 3, seconds: 0);
        Assert.AreEqual(1600, Apply(1600, lap: 3, seconds: 1));

        for (int s = 2; s < 7; s++)
            Assert.AreEqual(1600, Apply(1000 + s * 10, lap: 3, seconds: s));

        Assert.AreEqual(1070, Apply(1070, lap: 3, seconds: 7));
    }

    #endregion

    #region Start/finish

    [TestMethod]
    public void LapTicksBeforeTheGpsCrosses_HoldMovesToTheLine()
    {
        // The timing loop has the car over the line while its GPS still reads 98%. Carrying 98% into
        // the new lap and then snapping to 0 is the backwards jump this is here to stop.
        Apply(3880, lap: 3, seconds: 0);

        Assert.AreEqual(0, Apply(3920, lap: 4, seconds: 1));
        Assert.AreEqual(0, Apply(3970, lap: 4, seconds: 2));
        Assert.AreEqual(30, Apply(30, lap: 4, seconds: 3));
    }

    [TestMethod]
    public void LapTicksWithTheReadingInsideTheWindow_MovesToTheLine_EvenWhenTheHoldIsNot()
    {
        // At 70 m/s the hold is a second behind the reading that carries the tick. The tick is judged
        // on where the car has now been seen, 140 m short, not where it was a second ago, 210 m short.
        Apply(3720, lap: 3, seconds: 0);
        Apply(3790, lap: 3, seconds: 1);

        Assert.AreEqual(0, Apply(3860, lap: 4, seconds: 2));
        Assert.AreEqual(0, Apply(3930, lap: 4, seconds: 3));
        Assert.AreEqual(0, Apply(3995, lap: 4, seconds: 4));
        Assert.AreEqual(60, Apply(60, lap: 4, seconds: 5));
    }

    [TestMethod]
    public void StoppedJustShortOfTheLineAfterTheTick_StaysOnTheLine()
    {
        // A red flag catches the car 120 m short of the line, just after the timing loop counted it
        // across. It is not behind the line the tick put it on, so no backward clock runs, however
        // long it sits there.
        Apply(3850, lap: 3, seconds: 0);
        Assert.AreEqual(0, Apply(3880, lap: 4, seconds: 1));

        for (int s = 2; s <= 12; s++)
            Assert.AreEqual(0, Apply(3880 + s % 2, lap: 4, seconds: s), $"Put back on the old lap at {s}s");
    }

    [TestMethod]
    public void StoppedJustShortOfTheLineAfterTheTick_SweepDoesNotPutItBack()
    {
        Apply(3850, lap: 3, seconds: 0);
        Apply(3880, lap: 4, seconds: 1);

        for (int s = 2; s <= 12; s++)
        {
            _sticky.KeepAlive(T0.AddSeconds(s));
            Assert.IsNull(_sticky.TryRelease(Length, Offset, T0.AddSeconds(s)));
        }
        Assert.AreEqual(0, _sticky.HeldMeters);
    }

    [TestMethod]
    public void CrawlingToTheLineAfterTheTick_StaysOnTheLineUntilPastIt()
    {
        Apply(3850, lap: 3, seconds: 0);
        Apply(3880, lap: 4, seconds: 1);

        for (int s = 2; s <= 12; s++)
            Assert.AreEqual(0, Apply(3880 + s * 10, lap: 4, seconds: s));

        Assert.AreEqual(5, Apply(5, lap: 4, seconds: 13));
    }

    [TestMethod]
    public void SecondTickWhileStoppedShortOfTheLine_StaysOnTheLine()
    {
        // Scoring credits another lap while the car still sits on the approach.
        Apply(3850, lap: 5, seconds: 0);
        Assert.AreEqual(0, Apply(3900, lap: 6, seconds: 1));
        Assert.AreEqual(0, Apply(3910, lap: 7, seconds: 2));

        for (int s = 3; s <= 10; s++)
            Assert.AreEqual(0, Apply(3910, lap: 7, seconds: s));
        Assert.IsNull(_sticky.TryRelease(Length, Offset, T0.AddSeconds(20)));
    }

    [TestMethod]
    public void TickJudgedOnTheHold_ReadingJustBehindIt_StaysOnTheLine()
    {
        // The hold (195 m short) puts the tick inside the window; the reading carrying it sits 35 m
        // further back, outside the window but within noise of the hold.
        Apply(3805, lap: 3, seconds: 0);
        Assert.AreEqual(0, Apply(3770, lap: 4, seconds: 1));

        for (int s = 2; s <= 10; s++)
            Assert.AreEqual(0, Apply(3770, lap: 4, seconds: s));
        Assert.IsNull(_sticky.TryRelease(Length, Offset, T0.AddSeconds(20)));
    }

    [TestMethod]
    public void GlitchJustPastTheLineWhileStoppedOnTheApproach_DoesNotPutItBack()
    {
        Apply(3850, lap: 3, seconds: 0);
        Apply(3880, lap: 4, seconds: 1);
        Assert.AreEqual(10, Apply(10, lap: 4, seconds: 2));

        for (int s = 3; s <= 12; s++)
            Assert.AreEqual(10, Apply(3880, lap: 4, seconds: s), $"Put back on the old lap at {s}s");
    }

    [TestMethod]
    public void ClearlyPastTheLine_ApproachReadingsAreBehindAgain()
    {
        // Once the car is well into the new lap, a reading back on the approach is disagreement like
        // any other: either the car really did go back or the hold is wrong.
        Apply(3850, lap: 3, seconds: 0);
        Apply(3880, lap: 4, seconds: 1);
        Assert.AreEqual(300, Apply(300, lap: 4, seconds: 2));

        for (int s = 3; s < 8; s++)
            Assert.AreEqual(300, Apply(3880, lap: 4, seconds: s));
        Assert.AreEqual(3880, Apply(3880, lap: 4, seconds: 8));
    }

    [TestMethod]
    public void AfterTheTick_ReadingsFarBehindTheWindow_StillCorrect()
    {
        // Holding the line only covers the approach the tick counted as crossed. Readings well
        // before it are disagreement like any other, and give way after the usual five seconds.
        Apply(3850, lap: 3, seconds: 0);
        Apply(3880, lap: 4, seconds: 1);

        for (int s = 2; s < 7; s++)
            Assert.AreEqual(0, Apply(3500, lap: 4, seconds: s));

        Assert.AreEqual(3500, Apply(3500, lap: 4, seconds: 7));
    }

    [TestMethod]
    public void LapTicksAfterTheGpsCrossed_KeepsTheHold()
    {
        Apply(3950, lap: 3, seconds: 0);
        Assert.AreEqual(20, Apply(20, lap: 3, seconds: 1));

        // Already past the line by GPS; the lap count catching up changes nothing.
        Assert.AreEqual(20, Apply(10, lap: 4, seconds: 2));
        Assert.AreEqual(80, Apply(80, lap: 4, seconds: 3));
    }

    [TestMethod]
    public void LapCountRisesMidLap_LeavesTheHoldWhereTheGpsPutIt()
    {
        // Scoring credits a lap while the car is at 70%. That is not a crossing, and the car is
        // still where its GPS says.
        Apply(2800, lap: 3, seconds: 0);

        Assert.AreEqual(2850, Apply(2850, lap: 4, seconds: 1));
        Assert.AreEqual(2900, Apply(2900, lap: 4, seconds: 2));
    }

    [TestMethod]
    public void LapTicksOutsideTheCrossingWindow_LeavesTheHold()
    {
        // 300 m short of the line on a 4 km lap is further than real crossings scatter (200 m here).
        Apply(3700, lap: 3, seconds: 0);

        Assert.AreEqual(3720, Apply(3720, lap: 4, seconds: 1));
    }

    [TestMethod]
    public void LapCountDropsAndRecovers_NeverShowsTheLine()
    {
        // A lap count knocked down and restored - a replayed or out-of-order record - with the car
        // three quarters round. Neither change is a crossing.
        Apply(3000, lap: 5, seconds: 0);

        Assert.AreEqual(3050, Apply(3050, lap: 0, seconds: 1));
        Assert.AreEqual(3100, Apply(3100, lap: 5, seconds: 2));
    }

    [TestMethod]
    public void LapCountCorrectedDownwards_StartsAgain()
    {
        Apply(2000, lap: 5, seconds: 0);

        Assert.AreEqual(1500, Apply(1500, lap: 4, seconds: 1));
    }

    #endregion

    #region Starting again

    [TestMethod]
    public void Reset_TakesTheNextReadingAsItComes()
    {
        Apply(2000, lap: 3, seconds: 0);
        _sticky.Reset();

        Assert.IsNull(_sticky.HeldMeters);
        Assert.AreEqual(500, Apply(500, lap: 3, seconds: 1));
    }

    [TestMethod]
    public void LineMoved_StartsAgain()
    {
        // A revised start/finish line shifts every position, so the hold no longer means anything.
        Apply(2000, lap: 3, seconds: 0);

        Assert.AreEqual(1500, Apply(1500, lap: 3, seconds: 1, offset: Offset + 300));
    }

    [TestMethod]
    public void MapReplaced_StartsAgain()
    {
        Apply(2000, lap: 3, seconds: 0);

        Assert.AreEqual(1500, Apply(1500, lap: 3, seconds: 1, length: Length + 50));
    }

    [TestMethod]
    public void GoneLongEnoughToBeAnywhere_StartsAgain()
    {
        // At 90 m/s a car covers half of a 4 km lap in about 22 seconds, after which a reading
        // behind the hold could just as well be one ahead of it.
        Apply(2000, lap: 3, seconds: 0);

        Assert.AreEqual(1500, Apply(1500, lap: 3, seconds: 23));
    }

    [TestMethod]
    public void KeepAlive_StoppedCarKeepsItsHoldPastTheAgeLimit()
    {
        // A car parked with a steady fix sends no readings, but it is still being reported. Its hold
        // should still stand when it moves off.
        Apply(2000, lap: 3, seconds: 0);
        for (int s = 5; s <= 40; s += 5)
            _sticky.KeepAlive(T0.AddSeconds(s));

        Assert.AreEqual(2000, Apply(1980, lap: 3, seconds: 41));
    }

    [TestMethod]
    public void KeepAlive_WithNothingHeld_DoesNothing()
    {
        _sticky.KeepAlive(T0);

        Assert.IsNull(_sticky.HeldMeters);
        Assert.AreEqual(500, Apply(500, lap: 3, seconds: 1));
    }

    #endregion

    #region Completing a correction without a reading

    [TestMethod]
    public void TryRelease_CompletesAConfirmedCorrection()
    {
        // The car stopped while the hold was wrongly ahead of it, so no further reading will arrive
        // to complete the correction.
        Apply(2000, lap: 3, seconds: 0);
        Apply(1800, lap: 3, seconds: 1);

        Assert.IsNull(_sticky.TryRelease(Length, Offset, T0.AddSeconds(5)), "Only four seconds behind");
        Assert.AreEqual(1800, _sticky.TryRelease(Length, Offset, T0.AddSeconds(6)));
        Assert.AreEqual(1800, _sticky.HeldMeters);
    }

    [TestMethod]
    public void TryRelease_NothingPending_ReturnsNull()
    {
        Apply(2000, lap: 3, seconds: 0);
        Apply(1980, lap: 3, seconds: 1);

        Assert.IsNull(_sticky.TryRelease(Length, Offset, T0.AddSeconds(30)));
        Assert.AreEqual(2000, _sticky.HeldMeters);
    }

    [TestMethod]
    public void TryRelease_AfterTheLineMoved_ReturnsNull()
    {
        Apply(2000, lap: 3, seconds: 0);
        Apply(1800, lap: 3, seconds: 1);

        Assert.IsNull(_sticky.TryRelease(Length, Offset + 300, T0.AddSeconds(10)));
    }

    #endregion

    #region Unusable input

    [TestMethod]
    public void UnusableLapLength_PassesTheReadingThrough()
    {
        Assert.AreEqual(500, Apply(500, lap: 3, seconds: 0, length: 0));
        Assert.IsNull(_sticky.HeldMeters);
    }

    #endregion

    [TestMethod]
    [DataRow(100, 200, 100)]
    [DataRow(200, 100, -100)]
    [DataRow(3950, 50, 100)]
    [DataRow(50, 3950, -100)]
    [DataRow(0, 2000, 2000)]
    [DataRow(2000, 0, 2000)]
    public void SignedLapDelta_TakesTheShorterWayRound_AndReadsExactlyHalfAsForward(double from, double to, double expected)
    {
        Assert.AreEqual(expected, StickyLapPosition.SignedLapDelta(from, to, Length), 1e-9);
    }
}
