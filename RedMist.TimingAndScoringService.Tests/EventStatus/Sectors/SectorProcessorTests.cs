using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.Sectors;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Sectors;

[TestClass]
public class SectorProcessorTests
{
    private SessionContext _sessionContext = null!;
    private SectorProcessor _processor = null!;

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();
        var options = new DbContextOptionsBuilder<TsContext>().UseInMemoryDatabase($"Sectors_{Guid.NewGuid()}").Options;
        _sessionContext = new SessionContext(configuration, new TestDbContextFactory(options), loggerFactory.Object,
            new InMemoryCarLapHistoryService(null!), new FakeTimeProvider());
        _sessionContext.SessionState.SessionId = 1;
        _sessionContext.UpdateCars(
        [
            new CarPosition { Number = "1", Class = "GT", LastLapCompleted = 3 },
            new CarPosition { Number = "2", Class = "GT", LastLapCompleted = 3 },
            new CarPosition { Number = "3", Class = "Proto", LastLapCompleted = 3 },
        ]);

        _processor = new SectorProcessor(loggerFactory.Object, _sessionContext);
    }

    private CarPosition Car(string number) => _sessionContext.GetCarByNumber(number)!;

    private SectorTime Sector(string number, int index) => Car(number).Sectors.Single(s => s.Index == index);

    private PatchUpdates? Cross(params SectorCrossing[] crossings) => _processor.Process(crossings, SectorSource.External);

    [TestMethod]
    public void FirstCrossing_PublishesLayoutAndAllThreeFlags()
    {
        var updates = Cross(new SectorCrossing("1", 1, 30_000));

        Assert.IsNotNull(updates);
        Assert.AreEqual(1, _sessionContext.SessionState.SectorLayout.Count);
        Assert.AreEqual(SectorSource.External, _sessionContext.SessionState.SectorSource);
        Assert.AreEqual(1, updates.SessionPatches.Count);

        var sector = Sector("1", 1);
        Assert.AreEqual(4, sector.Lap, "Partway round the lap after the three completed");
        Assert.IsTrue(sector.IsPersonalBest && sector.IsClassBest && sector.IsOverallBest);
        Assert.AreEqual("1", updates.CarPatches.Single().Number);
    }

    [TestMethod]
    public void LayoutGrows_ToTheHighestSectorSeen()
    {
        Cross(new SectorCrossing("1", 1, 30_000));
        Cross(new SectorCrossing("1", 2, 40_000), new SectorCrossing("1", 3, 35_000));

        CollectionAssert.AreEqual(new[] { "S1", "S2", "S3" }, _sessionContext.SessionState.SectorLayout.Select(s => s.Label).ToArray());
    }

    [TestMethod]
    public void FasterCar_TakesOverallBest_AndTheFormerHolderIsRepublished()
    {
        Cross(new SectorCrossing("1", 1, 30_000));

        var updates = Cross(new SectorCrossing("3", 1, 29_000));

        Assert.IsNotNull(updates);
        CollectionAssert.AreEquivalent(new[] { "1", "3" }, updates.CarPatches.Select(p => p.Number).ToArray());
        Assert.IsTrue(Sector("3", 1).IsOverallBest);
        Assert.IsFalse(Sector("1", 1).IsOverallBest);
        // Still the best in its own class, which car 3 is not in.
        Assert.IsTrue(Sector("1", 1).IsClassBest);
        Assert.IsTrue(Sector("1", 1).IsPersonalBest);
    }

    [TestMethod]
    public void SlowerClassmate_GetsOnlyPersonalBest()
    {
        Cross(new SectorCrossing("1", 1, 30_000));
        Cross(new SectorCrossing("2", 1, 31_000));

        var sector = Sector("2", 1);
        Assert.IsTrue(sector.IsPersonalBest);
        Assert.IsFalse(sector.IsClassBest);
        Assert.IsFalse(sector.IsOverallBest);
    }

    [TestMethod]
    public void EqualTime_DoesNotTakeTheBest()
    {
        Cross(new SectorCrossing("1", 1, 30_000));
        Cross(new SectorCrossing("2", 1, 30_000));

        Assert.IsTrue(Sector("1", 1).IsOverallBest);
        Assert.IsFalse(Sector("2", 1).IsOverallBest);
        Assert.IsFalse(Sector("2", 1).IsClassBest);
    }

    [TestMethod]
    public void InvalidTime_IsShownButNeverABest()
    {
        Cross(new SectorCrossing("1", 1, 25_000, IsInvalid: true));

        var sector = Sector("1", 1);
        Assert.AreEqual(25_000, sector.TimeMs);
        Assert.IsTrue(sector.IsInvalid);
        Assert.IsFalse(sector.IsPersonalBest || sector.IsClassBest || sector.IsOverallBest);

        Cross(new SectorCrossing("2", 1, 30_000));
        Assert.IsTrue(Sector("2", 1).IsOverallBest);
    }

    [TestMethod]
    public void SlowerLap_LosesPersonalBestColor_ButKeepsTheTime()
    {
        Cross(new SectorCrossing("1", 1, 30_000));
        Cross(new SectorCrossing("1", 2, 40_000));
        Car("1").LastLapCompleted = 4;
        Cross(new SectorCrossing("1", 1, 31_000)); // next lap, slower

        var sector = Sector("1", 1);
        Assert.AreEqual(31_000, sector.TimeMs);
        Assert.AreEqual(5, sector.Lap);
        Assert.IsFalse(sector.IsPersonalBest || sector.IsOverallBest);
        // The best still stands, held by a time no longer on screen.
        Cross(new SectorCrossing("2", 1, 30_500));
        Assert.IsFalse(Sector("2", 1).IsOverallBest);
    }

    [TestMethod]
    public void ResentList_IsNotTakenAsANewLap()
    {
        Cross(new SectorCrossing("1", 1, 30_000));
        Cross(new SectorCrossing("1", 1, 30_000), new SectorCrossing("1", 2, 40_000));

        // The feed sends S1 again with S2; S1 must stay on lap 4.
        var updates = Cross(new SectorCrossing("1", 1, 30_000), new SectorCrossing("1", 2, 40_000));

        Assert.IsNull(updates);
        Assert.AreEqual(4, Sector("1", 1).Lap);
        Assert.AreEqual(4, Sector("1", 2).Lap);
    }

    [TestMethod]
    public void LastSector_MayBelongToTheLapJustCompleted()
    {
        Cross(new SectorCrossing("1", 1, 30_000), new SectorCrossing("1", 2, 40_000));
        Cross(new SectorCrossing("1", 3, 35_000));
        // The timing system counts the lap before the feed reports the final sector.
        Car("1").LastLapCompleted = 4;
        Cross(new SectorCrossing("2", 3, 36_000));

        Assert.AreEqual(4, Sector("1", 3).Lap);
        // Car 2 has completed 3 and its first sector seen is the last one, so it closes lap 3 or the
        // lap after; with nothing else to go on it is taken to have just closed lap 3.
        Assert.AreEqual(3, Sector("2", 3).Lap);
    }

    /// <summary>
    /// A source that resends a sector with a corrected time, or that the car skips sectors in, must
    /// not run the lap count ahead of the timing system's.
    /// </summary>
    [TestMethod]
    public void InferredLap_NeverRunsAheadOfTheTimingSystem()
    {
        Cross(new SectorCrossing("1", 1, 30_000));
        Cross(new SectorCrossing("1", 1, 30_100)); // corrected
        Cross(new SectorCrossing("1", 1, 30_050)); // and again

        Assert.AreEqual(4, Sector("1", 1).Lap);
    }

    [TestMethod]
    public void LapFromTheSource_IsTrusted()
    {
        Cross(new SectorCrossing("1", 2, 40_000, Lap: 9));

        Assert.AreEqual(9, Sector("1", 2).Lap);
    }

    [TestMethod]
    public void UnusableCrossings_AreIgnored()
    {
        var updates = Cross(new SectorCrossing("1", 9, 30_000), new SectorCrossing("1", 1, 0), new SectorCrossing("", 1, 30_000));

        Assert.IsNull(updates);
        Assert.AreEqual(0, _sessionContext.SessionState.SectorLayout.Count, "A bad sector number must not size the layout");
        Assert.AreEqual(0, Car("1").Sectors.Count);
    }

    [TestMethod]
    public void NewSession_ClearsBests()
    {
        Cross(new SectorCrossing("1", 1, 30_000));
        _sessionContext.SessionState.SessionId = 2;

        Cross(new SectorCrossing("2", 1, 31_000));

        Assert.IsTrue(Sector("2", 1).IsOverallBest, "The previous session's best no longer counts");
    }

    [TestMethod]
    public void ApplyCarValues_RestoresSectorsOnRebuiltCars()
    {
        Cross(new SectorCrossing("1", 1, 30_000));
        var rebuilt = new CarPosition { Number = "1", Class = "GT" };

        var patches = _processor.ApplyCarValues([rebuilt]);

        Assert.AreEqual(30_000, rebuilt.Sectors.Single().TimeMs);
        Assert.IsTrue(rebuilt.Sectors.Single().IsOverallBest);
        Assert.AreEqual("1", patches.Single().Number);
    }

    [TestMethod]
    public void ApplyCarValues_LeavesCarsThatStillHaveSectors()
    {
        Cross(new SectorCrossing("1", 1, 30_000));

        Assert.AreEqual(0, _processor.ApplyCarValues([Car("1")]).Count);
    }

    [TestMethod]
    public void TimeInvalidatedAfterTheFact_StopsCountingForEveryone()
    {
        Cross(new SectorCrossing("1", 1, 25_000));
        Cross(new SectorCrossing("1", 1, 25_000, IsInvalid: true)); // deleted, same lap

        Assert.IsFalse(Sector("1", 1).IsOverallBest);
        Cross(new SectorCrossing("2", 1, 30_000));
        Assert.IsTrue(Sector("2", 1).IsOverallBest, "A deleted time must not block purple");
    }

    [TestMethod]
    public void CorrectedTimeOnTheSameLap_ReplacesTheOriginal()
    {
        Cross(new SectorCrossing("1", 1, 30_000, Lap: 4));
        Cross(new SectorCrossing("1", 1, 30_400, Lap: 4));

        var sector = Sector("1", 1);
        Assert.AreEqual(30_400, sector.TimeMs);
        Assert.IsTrue(sector.IsPersonalBest && sector.IsOverallBest, "The corrected time is the only time on that lap");
    }

    [TestMethod]
    public void ClassBest_FollowsTheCarsCurrentClass()
    {
        Car("3").Class = null;
        Cross(new SectorCrossing("3", 1, 29_000)); // fastest, class not yet known
        Cross(new SectorCrossing("1", 1, 30_000));
        Car("3").Class = "GT";

        Cross(new SectorCrossing("2", 1, 31_000));

        Assert.IsTrue(Sector("3", 1).IsOverallBest && Sector("3", 1).IsClassBest, "Nesting holds after the class arrives");
        Assert.IsFalse(Sector("1", 1).IsClassBest);
    }

    [TestMethod]
    public void CarUnknownToTheSession_TakesNoFieldBest()
    {
        Cross(new SectorCrossing("99", 1, 20_000));
        Cross(new SectorCrossing("1", 1, 30_000));

        Assert.IsTrue(Sector("1", 1).IsOverallBest);
    }

    [TestMethod]
    public void ResumedSession_GettingItsNumber_KeepsTheTimes()
    {
        _sessionContext.SessionState.SessionId = 0;
        Cross(new SectorCrossing("1", 1, 30_000));
        _sessionContext.SessionState.SessionId = 7;

        var updates = Cross(new SectorCrossing("2", 1, 31_000));

        Assert.IsTrue(Sector("1", 1).IsOverallBest);
        Assert.IsFalse(Sector("2", 1).IsOverallBest);
        Assert.IsFalse(updates!.CarPatches.Any(p => p.Number == "1" && p.Sectors!.Count == 0));
    }

    [TestMethod]
    public void NewSession_ClearsTheCarsAndTellsClients()
    {
        Cross(new SectorCrossing("1", 1, 30_000));
        _sessionContext.SessionState.SessionId = 2;

        var updates = Cross(new SectorCrossing("2", 1, 31_000));

        Assert.AreEqual(0, Car("1").Sectors.Count);
        Assert.IsTrue(updates!.CarPatches.Any(p => p.Number == "1" && p.Sectors!.Count == 0));
    }

    [TestMethod]
    public void SameTimeAfterCompletingItsLap_IsANewLap()
    {
        Cross(new SectorCrossing("1", 1, 30_000));
        // The car has completed lap 4, and lap 5 too without this sector being seen in between.
        Car("1").LastLapCompleted = 5;

        Cross(new SectorCrossing("1", 1, 30_000));

        Assert.AreEqual(6, Sector("1", 1).Lap);
    }

    /// <summary>
    /// A source that sends the whole list: the batch carrying the last sector also repeats the first
    /// two, and arrives with the lap count already ticked. Those repeats must stay on the lap just
    /// completed, keep their colors, and leave the last sector on that lap too.
    /// </summary>
    [TestMethod]
    public void WholeListResent_AsTheLapTicks_StaysOnTheLapJustCompleted()
    {
        Cross(new SectorCrossing("1", 1, 30_000));
        Cross(new SectorCrossing("1", 1, 30_000), new SectorCrossing("1", 2, 40_000));
        Car("1").LastLapCompleted = 4;

        Cross(new SectorCrossing("1", 1, 30_000), new SectorCrossing("1", 2, 40_000), new SectorCrossing("1", 3, 35_000));

        foreach (var index in new[] { 1, 2, 3 })
        {
            var sector = Sector("1", index);
            Assert.AreEqual(4, sector.Lap, $"S{index}");
            Assert.IsTrue(sector.IsOverallBest && sector.IsPersonalBest, $"S{index} keeps its colors");
        }

        // And the next lap's first sector starts lap 5.
        Cross(new SectorCrossing("1", 1, 31_000));
        Assert.AreEqual(5, Sector("1", 1).Lap);
    }

    [TestMethod]
    public void ApplyCarValues_ReturnsTheClearingPatches_WhenTheSessionChanged()
    {
        Cross(new SectorCrossing("1", 1, 30_000));
        _sessionContext.SessionState.SessionId = 2;

        var patches = _processor.ApplyCarValues(_sessionContext.SessionState.CarPositions);

        Assert.AreEqual(0, Car("1").Sectors.Count);
        Assert.IsTrue(patches.Any(p => p.Number == "1" && p.Sectors!.Count == 0), "Clients must be told, or they keep the old session's times");
    }

    [TestMethod]
    public void ExternalSource_TakesSectorsOutOfThePatch()
    {
        var patch = new CarPositionPatch
        {
            Number = "1",
            Sectors = [new SectorTime { Index = 1, Lap = 4, TimeMs = 30_000, IsOverallBest = true }, new SectorTime { Index = 2, TimeMs = 40_000, IsInvalid = true }],
        };

        var crossings = ExternalSectorSource.TakeCrossings(patch);

        Assert.IsNull(patch.Sectors, "The processor publishes the car's sectors, not the source");
        Assert.AreEqual(new SectorCrossing("1", 1, 30_000, 4), crossings[0]);
        Assert.AreEqual(new SectorCrossing("1", 2, 40_000, null, true), crossings[1]);
    }

    [TestMethod]
    public void ExternalSource_ReadsCompletedSectionsNamedAsSectors()
    {
        var patch = new CarPositionPatch
        {
            Number = "1",
            CompletedSections =
            [
                new CompletedSection { Number = "1", SectionId = "S1", LastSectionTimeMs = 30_000, LastLap = 3 },
                new CompletedSection { Number = "1", SectionId = "S2", LastSectionTimeMs = 40_000, LastLap = 3 },
                new CompletedSection { Number = "1", SectionId = "Spd", LastSectionTimeMs = 5_000 },
                new CompletedSection { Number = "1", SectionId = "S9", LastSectionTimeMs = 5_000 },
            ],
        };

        var crossings = ExternalSectorSource.TakeCrossings(patch);

        Assert.IsNotNull(patch.CompletedSections, "Left in place for readers of that field");
        CollectionAssert.AreEqual(new[] { 1, 2 }, crossings.Select(c => c.Index).ToArray());
        Assert.IsTrue(crossings.All(c => c.Lap == null), "Its lap is not per sector, so it is worked out instead");
    }
}
