using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.Multiloop;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.Multiloop;

/// <summary>
/// After a reset the pipeline rebuilds the field from the timing source and then re-applies
/// whatever Multiloop already knows about each car. That re-application is the only thing putting
/// pit counts, start positions and section times back, so a car it skips silently loses them for
/// the rest of the session.
/// </summary>
[TestClass]
public class MultiloopApplyCarValuesTests
{
    private const char Delim = EventProcessor.EventStatus.Multiloop.Consts.DELIM;

    private MultiloopProcessor _processor = null!;
    private SessionContext _context = null!;

    [TestInitialize]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        var loggerFactory = new DebugLoggerFactory();
        _context = new SessionContext(configuration, new TestDbContextFactory(optionsBuilder.Options), loggerFactory,
            new Mock<ICarLapHistoryService>().Object);
        _processor = new MultiloopProcessor(loggerFactory, _context);
    }

    /// <summary>Values are hexadecimal in the Multiloop protocol.</summary>
    private static string CompletedLap(string number, string currentStatus = "Active", string pitStopCountHex = "3",
        string lastLapPittedHex = "A", string startPositionHex = "5", string lapsLedHex = "B")
        => string.Join(Delim,
            "$C", "U", "80004", "Q1", "C", number, "8", "4", "83DDF", "1CB83", "T", "1CB83", "4", "2E6A", "0", "649",
            "0", "C", "1CB83", currentStatus, "G", pitStopCountHex, lastLapPittedHex, startPositionHex, lapsLedHex);

    private static string LineCrossing(string number, string crossingStatus)
        => string.Join(Delim, "$L", "N", "EF325", "Q1", number, "5", "SF", "A", "9B82E", "G", crossingStatus);

    private static string CompletedSection(string number, string section)
        => string.Join(Delim, "$S", "N", "F3170000", "Q1", number, "EF317", section, "2DF3C0E", "7C07", "5");

    private void Feed(string data) => _processor.Process(new TimingMessage(Backend.Shared.Consts.MULTILOOP_TYPE, data, 1, DateTime.UtcNow));

    [TestMethod]
    public void ApplyCarValues_RestoresPitCountsStartPositionAndLapsLed()
    {
        Feed(CompletedLap("7", pitStopCountHex: "3", lastLapPittedHex: "A", startPositionHex: "5", lapsLedHex: "B"));

        var car = new CarPosition { Number = "7" };
        _processor.ApplyCarValues([car]);

        Assert.AreEqual(3, car.PitStopCount);
        Assert.AreEqual(10, car.LastLapPitted, "0xA");
        Assert.AreEqual(5, car.OverallStartingPosition);
        Assert.AreEqual(11, car.LapsLedOverall, "0xB");
    }

    /// <summary>
    /// The status field is shown in a fixed-width column; anything longer is cut rather than
    /// allowed to push the rest of the row out.
    /// </summary>
    [TestMethod]
    public void ApplyCarValues_TruncatesAnOverlongStatusToTwelveCharacters()
    {
        Feed(CompletedLap("7", currentStatus: "RunningStronglyIndeed"));

        var car = new CarPosition { Number = "7" };
        _processor.ApplyCarValues([car]);

        Assert.AreEqual("RunningStron", car.CurrentStatus);
    }

    /// <summary>
    /// An empty status from the feed is applied, not ignored: it clears whatever the car was last
    /// showing rather than leaving a stale "Pit" or "Off" up on screen.
    /// </summary>
    [TestMethod]
    public void ApplyCarValues_WithAnEmptyStatus_OverwritesTheStaleStatus()
    {
        Feed(CompletedLap("7", currentStatus: ""));

        var car = new CarPosition { Number = "7", CurrentStatus = "Stale" };
        _processor.ApplyCarValues([car]);

        Assert.AreEqual(string.Empty, car.CurrentStatus);
    }

    /// <summary>
    /// The pit start/finish crossing is the only thing Multiloop says about a car being in the pit,
    /// so the in-pit flag follows it in both directions.
    /// </summary>
    [TestMethod]
    public void ApplyCarValues_TracksThePitStartFinishCrossing()
    {
        Feed(LineCrossing("7", "P"));
        var car = new CarPosition { Number = "7" };
        _processor.ApplyCarValues([car]);
        Assert.IsTrue(car.IsPitStartFinish);
        Assert.IsTrue(car.IsInPit);

        Feed(LineCrossing("7", "T"));
        _processor.ApplyCarValues([car]);
        Assert.IsFalse(car.IsPitStartFinish);
        Assert.IsFalse(car.IsInPit);
    }

    [TestMethod]
    public void ApplyCarValues_RestoresCompletedSections()
    {
        Feed(CompletedSection("7", "S1"));
        Feed(CompletedSection("7", "S2"));

        var car = new CarPosition { Number = "7" };
        _processor.ApplyCarValues([car]);

        Assert.HasCount(2, car.CompletedSections);
    }

    /// <summary>
    /// Cars Multiloop has said nothing about must be left exactly as the timing source left them.
    /// </summary>
    [TestMethod]
    public void ApplyCarValues_LeavesCarsWithNoMultiloopDataUntouched()
    {
        Feed(CompletedLap("7"));

        var other = new CarPosition { Number = "99", PitStopCount = 1, CurrentStatus = "Active", OverallStartingPosition = 12 };
        _processor.ApplyCarValues([other]);

        Assert.AreEqual(1, other.PitStopCount);
        Assert.AreEqual("Active", other.CurrentStatus);
        Assert.AreEqual(12, other.OverallStartingPosition);
    }

    /// <summary>
    /// A car with no number cannot be matched to anything and must not take the sweep down with it.
    /// </summary>
    [TestMethod]
    public void ApplyCarValues_SkipsCarsWithoutANumber()
    {
        Feed(CompletedLap("7"));

        var unnumbered = new CarPosition { Number = string.Empty, PitStopCount = 4 };
        var numbered = new CarPosition { Number = "7" };
        _processor.ApplyCarValues([unnumbered, numbered]);

        Assert.AreEqual(4, unnumbered.PitStopCount);
        Assert.AreEqual(3, numbered.PitStopCount, "The car after the unnumbered one still has to be reached.");
    }
}
