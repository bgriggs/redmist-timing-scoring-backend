using RedMist.Backend.Shared.Utilities;
using System.Globalization;

namespace RedMist.EventProcessor.Tests.Utilities;

[TestClass]
public class RaceTimeParserTests
{
    private CultureInfo? originalCulture;

    [TestInitialize]
    public void Setup() => originalCulture = CultureInfo.CurrentCulture;

    [TestCleanup]
    public void Cleanup()
    {
        if (originalCulture != null)
            CultureInfo.CurrentCulture = originalCulture;
    }

    #region Sub-24-hour values

    [TestMethod]
    public void Parse_WithMilliseconds_ReturnsComponents()
    {
        Assert.AreEqual(new TimeSpan(0, 1, 23, 45, 678), RaceTimeParser.Parse("01:23:45.678"));
    }

    [TestMethod]
    public void Parse_WithoutMilliseconds_ReturnsComponents()
    {
        Assert.AreEqual(new TimeSpan(0, 8, 15, 30, 0), RaceTimeParser.Parse("08:15:30"));
    }

    [TestMethod]
    public void Parse_SingleDigitHour_ReturnsComponents()
    {
        Assert.AreEqual(new TimeSpan(0, 1, 2, 3, 400), RaceTimeParser.Parse("1:02:03.4"));
    }

    [TestMethod]
    public void Parse_ZeroTime_ReturnsZero()
    {
        Assert.IsTrue(RaceTimeParser.TryParse("00:00:00.000", out var result));
        Assert.AreEqual(TimeSpan.Zero, result);
    }

    [TestMethod]
    public void Parse_LastMinuteBeforeTwentyFourHours_ReturnsComponents()
    {
        Assert.AreEqual(new TimeSpan(0, 23, 59, 59, 999), RaceTimeParser.Parse("23:59:59.999"));
    }

    #endregion

    #region Past 24 hours - the defect this parser exists for

    [TestMethod]
    public void Parse_TwentyFiveHours_DoesNotZero()
    {
        Assert.AreEqual(new TimeSpan(1, 1, 0, 0, 0), RaceTimeParser.Parse("25:00:00.000"));
    }

    [TestMethod]
    public void Parse_FortyEightHours_DoesNotZero()
    {
        Assert.AreEqual(new TimeSpan(2, 0, 0, 15, 989), RaceTimeParser.Parse("48:00:15.989"));
    }

    [TestMethod]
    public void Parse_ThreeDigitHours_DoesNotZero()
    {
        Assert.AreEqual(new TimeSpan(5, 4, 30, 12, 5), RaceTimeParser.Parse("124:30:12.005"));
    }

    [TestMethod]
    public void Parse_PastTwentyFourHoursWithoutMilliseconds_DoesNotZero()
    {
        Assert.AreEqual(new TimeSpan(1, 2, 3, 4, 0), RaceTimeParser.Parse("26:03:04"));
    }

    [TestMethod]
    public void Parse_HoursOrderedMonotonically_AcrossTheTwentyFourHourBoundary()
    {
        // The boundary is where the framework parse used to flip a growing clock back to zero.
        var before = RaceTimeParser.Parse("23:59:59.000");
        var after = RaceTimeParser.Parse("24:00:01.000");
        Assert.IsTrue(after > before, "The clock must keep increasing past 24 hours");
        Assert.AreEqual(TimeSpan.FromSeconds(2), after - before);
    }

    #endregion

    #region Unparseable values

    [TestMethod]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.IsFalse(RaceTimeParser.TryParse(null, out var result));
        Assert.AreEqual(TimeSpan.Zero, result);
    }

    [TestMethod]
    public void TryParse_Empty_ReturnsFalse()
    {
        Assert.IsFalse(RaceTimeParser.TryParse("", out _));
        Assert.IsFalse(RaceTimeParser.TryParse("   ", out _));
    }

    [TestMethod]
    public void TryParse_NonNumeric_ReturnsFalse()
    {
        Assert.IsFalse(RaceTimeParser.TryParse("invalid", out _));
        Assert.IsFalse(RaceTimeParser.TryParse("aa:bb:cc.ddd", out _));
        Assert.IsFalse(RaceTimeParser.TryParse("01asdf:we12we:47.872", out _));
    }

    [TestMethod]
    public void TryParse_MissingSecondColon_ReturnsFalse()
    {
        Assert.IsFalse(RaceTimeParser.TryParse("12:34", out _));
        Assert.IsFalse(RaceTimeParser.TryParse("123456", out _));
    }

    [TestMethod]
    public void TryParse_OutOfRangeHours_ReturnsFalseRatherThanThrowing()
    {
        Assert.IsFalse(RaceTimeParser.TryParse("2147483647:00:00.000", out _));
    }

    [TestMethod]
    public void Parse_Unparseable_ReturnsZero()
    {
        Assert.AreEqual(TimeSpan.Zero, RaceTimeParser.Parse("invalid"));
    }

    #endregion

    #region Culture

    [TestMethod]
    public void Parse_UnderCommaDecimalCulture_StillReadsTheFraction()
    {
        // The wire format is invariant. A culture whose decimal separator is a comma must not
        // change how "00:01:30.500" is read.
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");

        Assert.AreEqual(TimeSpan.FromSeconds(90.5), RaceTimeParser.Parse("00:01:30.500"));
        Assert.AreEqual(new TimeSpan(2, 0, 0, 15, 989), RaceTimeParser.Parse("48:00:15.989"));
    }

    #endregion
}
