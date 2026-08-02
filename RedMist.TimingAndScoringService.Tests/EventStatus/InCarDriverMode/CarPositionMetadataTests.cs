using RedMist.EventProcessor.EventStatus.InCarDriverMode;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.InCarDriverMode;

/// <summary>
/// Covers the gap and gain/loss strings shown in driver mode. The driver's car is the reference:
/// gap is the driver's total time minus the other car's, so it is negative for a car behind, and
/// gain/loss is the other car's last lap minus the driver's, so it is negative when the driver is
/// losing time. Both are formatted by magnitude, with gain/loss carrying an explicit minus sign.
/// </summary>
[TestClass]
public class CarPositionMetadataTests
{
    private static CarPosition Car(string number, int lapCompleted, string totalTime, string lastLapTime)
    {
        return new CarPosition
        {
            Number = number,
            LastLapCompleted = lapCompleted,
            TotalTime = totalTime,
            LastLapTime = lastLapTime,
        };
    }

    /// <summary>
    /// Drives the same path the processor does: the driver is set on the car set first, then the
    /// other car is applied.
    /// </summary>
    private static CarPositionMetadata Apply(CarPosition driver, CarPosition? other)
    {
        var metadata = new CarPositionMetadata();
        metadata.UpdateDriver(driver);
        metadata.Update(other);
        return metadata;
    }

    #region Gap - same lap

    [TestMethod]
    public void Gap_CarAhead_SameLap_Test()
    {
        // The car ahead has the smaller total time, so the gap is positive
        var driver = Car("1", 10, "00:52:00.000", "00:02:00.000");
        var ahead = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, ahead);

        Assert.AreEqual("2:00.000", metadata.Gap);
    }

    [TestMethod]
    public void Gap_CarAhead_SameLap_UnderAMinute_Test()
    {
        var driver = Car("1", 10, "00:50:03.250", "00:02:00.000");
        var ahead = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, ahead);

        Assert.AreEqual("3.250", metadata.Gap);
    }

    [TestMethod]
    public void Gap_CarBehind_SameLap_KeepsFullMagnitude_Test()
    {
        // The car behind has the larger total time, so the gap is negative. It is reported unsigned
        // since the slot it goes in already says which car it refers to, but the magnitude must be
        // whole - this formatted as "15.000" while negative values fell through to the seconds format.
        var driver = Car("1", 10, "00:50:00.000", "00:02:00.000");
        var behind = Car("2", 10, "00:51:15.000", "00:02:00.000");

        var metadata = Apply(driver, behind);

        Assert.AreEqual("1:15.000", metadata.Gap);
    }

    [TestMethod]
    public void Gap_CarBehind_SameLap_OverAnHour_Test()
    {
        var driver = Car("1", 10, "00:50:00.000", "00:02:00.000");
        var behind = Car("2", 10, "01:50:05.250", "00:02:00.000");

        var metadata = Apply(driver, behind);

        Assert.AreEqual("1:00:05.250", metadata.Gap);
    }

    [TestMethod]
    public void Gap_SameLap_SameTotalTime_Test()
    {
        var driver = Car("1", 10, "00:50:00.000", "00:02:00.000");
        var other = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, other);

        Assert.AreEqual("0.000", metadata.Gap);
    }

    [TestMethod]
    public void Gap_SameLap_MissingTotalTime_IsEmpty_Test()
    {
        var driver = Car("1", 10, "00:50:00.000", "00:02:00.000");
        var other = Car("2", 10, string.Empty, "00:02:00.000");

        var metadata = Apply(driver, other);

        Assert.AreEqual(string.Empty, metadata.Gap);
    }

    [TestMethod]
    public void Gap_SameLap_UnparsableTotalTime_IsEmpty_Test()
    {
        var driver = Car("1", 10, "00:50:00.000", "00:02:00.000");
        var other = Car("2", 10, "not a time", "00:02:00.000");

        var metadata = Apply(driver, other);

        Assert.AreEqual(string.Empty, metadata.Gap);
    }

    [TestMethod]
    public void Gap_SameLap_MissingDriverTotalTime_IsEmpty_Test()
    {
        // The guard covers both sides, not just the compared car
        var driver = Car("1", 10, string.Empty, "00:02:00.000");
        var other = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, other);

        Assert.AreEqual(string.Empty, metadata.Gap);
    }

    [TestMethod]
    public void Gap_SameLap_ZeroTotalTime_IsTreatedAsMissing_Test()
    {
        // ParseRMTime returns default for a zero time, so a total time of zero cannot be told apart
        // from a missing one. A car that has not run is the only way to get here, where an empty gap
        // is what is wanted anyway.
        var driver = Car("1", 10, "00:00:00.000", "00:02:00.000");
        var other = Car("2", 10, "00:00:01.000", "00:02:00.000");

        var metadata = Apply(driver, other);

        Assert.AreEqual(string.Empty, metadata.Gap);
    }

    [TestMethod]
    public void Gap_CarBehind_SameLap_ExactlyOneMinute_Test()
    {
        // The worst of the pre-fix renderings: the seconds component of -1:00 is zero, so a minute
        // down the road showed as "0.000"
        var driver = Car("1", 10, "00:50:00.000", "00:02:00.000");
        var behind = Car("2", 10, "00:51:00.000", "00:02:00.000");

        var metadata = Apply(driver, behind);

        Assert.AreEqual("1:00.000", metadata.Gap);
    }

    [TestMethod]
    public void Gap_CarBehind_SameLap_ExactlyOneHour_Test()
    {
        var driver = Car("1", 10, "00:50:00.000", "00:02:00.000");
        var behind = Car("2", 10, "01:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, behind);

        Assert.AreEqual("1:00:00.000", metadata.Gap);
    }

    #endregion

    #region Gap - different laps

    [TestMethod]
    public void Gap_CarAheadByOneLap_Test()
    {
        var driver = Car("1", 10, "00:50:00.000", "00:02:00.000");
        var ahead = Car("2", 11, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, ahead);

        Assert.AreEqual("1 lap", metadata.Gap);
    }

    [TestMethod]
    public void Gap_CarAheadByMultipleLaps_Test()
    {
        var driver = Car("1", 10, "00:50:00.000", "00:02:00.000");
        var ahead = Car("2", 13, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, ahead);

        Assert.AreEqual("3 laps", metadata.Gap);
    }

    [TestMethod]
    public void Gap_CarOnFewerLaps_IsEmpty_Test()
    {
        // Known limitation rather than an undefined case. This branch was written for the cars ahead,
        // where fewer laps means stale data, but it also serves the car behind, where being a lap
        // down is normal. The result is that a car behind shows a gap until it goes a lap down and
        // then shows nothing.
        var driver = Car("1", 12, "00:50:00.000", "00:02:00.000");
        var behind = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, behind);

        Assert.AreEqual(string.Empty, metadata.Gap);
    }

    #endregion

    #region GainLoss

    [TestMethod]
    public void GainLoss_DriverFaster_IsPositiveAndUnsigned_Test()
    {
        // The other car's last lap was slower than the driver's, so the driver gained
        var driver = Car("1", 10, "00:50:00.000", "00:02:00.000");
        var other = Car("2", 10, "00:50:00.000", "00:02:05.000");

        var metadata = Apply(driver, other);

        Assert.AreEqual("5.000", metadata.GainLoss);
    }

    [TestMethod]
    public void GainLoss_DriverSlower_IsSigned_Test()
    {
        var driver = Car("1", 10, "00:50:00.000", "00:02:05.000");
        var other = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, other);

        Assert.AreEqual("-5.000", metadata.GainLoss);
    }

    [TestMethod]
    public void GainLoss_DriverSlowerByOverAMinute_KeepsFullMagnitude_Test()
    {
        // This formatted as "-15.000" while negative values fell through to the seconds format
        var driver = Car("1", 10, "00:50:00.000", "00:02:15.000");
        var other = Car("2", 10, "00:50:00.000", "00:01:00.000");

        var metadata = Apply(driver, other);

        Assert.AreEqual("-1:15.000", metadata.GainLoss);
    }

    [TestMethod]
    public void GainLoss_EqualLapTimes_IsZero_Test()
    {
        var driver = Car("1", 10, "00:50:00.000", "00:02:00.000");
        var other = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, other);

        Assert.AreEqual("0.000", metadata.GainLoss);
    }

    [TestMethod]
    public void GainLoss_MissingLapTime_IsEmpty_Test()
    {
        var driver = Car("1", 10, "00:50:00.000", "00:02:00.000");
        var other = Car("2", 10, "00:50:00.000", string.Empty);

        var metadata = Apply(driver, other);

        Assert.AreEqual(string.Empty, metadata.GainLoss);
    }

    [TestMethod]
    public void GainLoss_MissingDriverLapTime_IsEmpty_Test()
    {
        // The guard covers both sides, not just the compared car
        var driver = Car("1", 10, "00:50:00.000", string.Empty);
        var other = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, other);

        Assert.AreEqual(string.Empty, metadata.GainLoss);
    }

    [TestMethod]
    public void GainLoss_DriverSlowerByExactlyOneMinute_Test()
    {
        // The seconds component of -1:00 is zero, so this rendered as "-0.000" before the fix
        var driver = Car("1", 10, "00:50:00.000", "00:03:00.000");
        var other = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, other);

        Assert.AreEqual("-1:00.000", metadata.GainLoss);
    }

    [TestMethod]
    public void GainLoss_IsSetOnDifferentLaps_Test()
    {
        // Gain/loss compares last lap times and does not depend on both cars being on the same lap
        var driver = Car("1", 10, "00:50:00.000", "00:02:05.000");
        var ahead = Car("2", 11, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, ahead);

        Assert.AreEqual("1 lap", metadata.Gap);
        Assert.AreEqual("-5.000", metadata.GainLoss);
    }

    #endregion

    #region Update ordering and clearing

    [TestMethod]
    public void GapAndGainLoss_WhenCarArrivesBeforeDriver_Test()
    {
        // The reverse order of Apply - the other car is applied first and the values are filled in
        // once the driver arrives
        var driver = Car("1", 10, "00:52:00.000", "00:02:05.000");
        var ahead = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var metadata = new CarPositionMetadata();
        metadata.Update(ahead);

        Assert.AreEqual(string.Empty, metadata.Gap, "There is no driver to compare against yet");
        Assert.AreEqual(string.Empty, metadata.GainLoss);

        metadata.UpdateDriver(driver);

        Assert.AreEqual("2:00.000", metadata.Gap);
        Assert.AreEqual("-5.000", metadata.GainLoss);
    }

    [TestMethod]
    public void GapAndGainLoss_ClearedWhenCarGoesAway_Test()
    {
        var driver = Car("1", 10, "00:52:00.000", "00:02:05.000");
        var ahead = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, ahead);
        Assert.AreEqual("2:00.000", metadata.Gap);
        Assert.AreEqual("-5.000", metadata.GainLoss);

        metadata.Update(null);

        Assert.AreEqual(string.Empty, metadata.Gap);
        Assert.AreEqual(string.Empty, metadata.GainLoss);
        Assert.AreEqual(string.Empty, metadata.Number);
    }

    [TestMethod]
    public void GapAndGainLoss_RecalculatedOnDriverUpdate_Test()
    {
        var driver = Car("1", 10, "00:52:00.000", "00:02:05.000");
        var ahead = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var metadata = Apply(driver, ahead);
        Assert.AreEqual("2:00.000", metadata.Gap);

        // The driver closes on the car ahead
        metadata.UpdateDriver(Car("1", 10, "00:50:30.000", "00:01:55.000"));

        Assert.AreEqual("30.000", metadata.Gap);
        Assert.AreEqual("5.000", metadata.GainLoss);
    }

    #endregion

    #region Through a car set

    [TestMethod]
    public void CarSet_DriversCar_HasNoGapOrGainLoss_Test()
    {
        // The car set only ever calls Update on the driver's own slot, never UpdateDriver, so that
        // slot has nothing to compare against and its gap and gain/loss stay empty. It still ships
        // in the payload, so pin it rather than leave it to be discovered.
        var carSet = new CarSet();
        var driver = Car("1", 10, "00:52:00.000", "00:02:05.000");

        carSet.UpdateDriver(driver);
        carSet.CarAhead.Update(Car("2", 10, "00:50:00.000", "00:02:00.000"));

        Assert.AreEqual(string.Empty, carSet.DriversCar.Gap);
        Assert.AreEqual(string.Empty, carSet.DriversCar.GainLoss);
        Assert.AreEqual("1", carSet.DriversCar.Number, "The driver's own slot still carries identity");

        Assert.AreEqual("2:00.000", carSet.CarAhead.Gap, "The compared slots are populated as usual");
        Assert.AreEqual("-5.000", carSet.CarAhead.GainLoss);
    }

    [TestMethod]
    public void CarSet_Payload_CarriesGapAndGainLossInSlotOrder_Test()
    {
        // The gap is unsigned, so the slot order is what tells the driver which car it refers to
        var carSet = new CarSet();
        var driver = Car("1", 10, "00:52:00.000", "00:02:00.000");

        carSet.UpdateDriver(driver);
        carSet.CarAhead.Update(Car("2", 10, "00:50:00.000", "00:02:00.000"));
        carSet.CarBehind.Update(Car("4", 10, "00:53:15.000", "00:02:00.000"));

        var payload = carSet.GetPayloadPartial();

        Assert.AreEqual(4, payload.Cars.Count);
        Assert.AreEqual("2:00.000", payload.Cars[0]?.Gap, "Car ahead, which is a positive gap");
        Assert.AreEqual("1", payload.Cars[2]?.Number, "The driver's own car is the third slot");
        Assert.AreEqual("1:15.000", payload.Cars[3]?.Gap, "Car behind, which is a negative gap reported unsigned");
    }

    #endregion

    #region Reported status

    [TestMethod]
    public void GetCarStatus_CarriesGapAndGainLoss_Test()
    {
        var driver = Car("1", 10, "00:52:00.000", "00:02:05.000");
        var ahead = Car("2", 10, "00:50:00.000", "00:02:00.000");

        var status = Apply(driver, ahead).GetCarStatus();

        Assert.IsNotNull(status);
        Assert.AreEqual("2", status.Number);
        Assert.AreEqual("2:00.000", status.Gap);
        Assert.AreEqual("-5.000", status.GainLoss);
        Assert.AreEqual("00:02:00.000", status.LastLap);
    }

    #endregion
}
