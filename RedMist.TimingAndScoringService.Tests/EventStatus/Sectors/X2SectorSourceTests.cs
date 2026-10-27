using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.Sectors;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Sectors;

/// <summary>
/// Simulated X2 passings over a three-sector lap: start/finish on loop 1, sector 1 ending at loop 2,
/// sector 2 at loop 3, sector 3 back at loop 1. Loop 9 is pit in, loop 4 is an extra track loop that
/// bounds no sector.
/// </summary>
[TestClass]
public class X2SectorSourceTests
{
    private static readonly DateTime Start = new(2026, 10, 26, 14, 0, 0, DateTimeKind.Utc);

    private SessionContext _sessionContext = null!;
    private X2SectorSource _source = null!;

    private static List<LoopMetadata> Loops() =>
    [
        new LoopMetadata { Id = 1, Type = LoopType.StartFinish, Name = "SF" },
        new LoopMetadata { Id = 2, Type = LoopType.Other, Name = "T4" },
        new LoopMetadata { Id = 3, Type = LoopType.Other, Name = "T9" },
        new LoopMetadata { Id = 4, Type = LoopType.Other, Name = "Speed" },
        new LoopMetadata { Id = 9, Type = LoopType.PitIn, Name = "Pit in" },
    ];

    private static SectorConfiguration ThreeSectors() => new()
    {
        StartFinishLoopId = 1,
        Sectors =
        [
            new SectorDefinition { EndLoopId = 2, LengthMeters = 1200 },
            new SectorDefinition { EndLoopId = 3, Label = "Mid" },
            new SectorDefinition(),
        ],
    };

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();
        var options = new DbContextOptionsBuilder<TsContext>().UseInMemoryDatabase($"X2Sectors_{Guid.NewGuid()}").Options;
        var dbFactory = new TestDbContextFactory(options);
        _sessionContext = new SessionContext(configuration, dbFactory, loggerFactory.Object,
            new InMemoryCarLapHistoryService(null!), new FakeTimeProvider());
        _sessionContext.SessionState.SessionId = 1;
        _sessionContext.UpdateCars(
        [
            new CarPosition { Number = "7", TransponderId = 1007 },
            new CarPosition { Number = "8", TransponderId = 1008 },
        ]);

        _source = new X2SectorSource(dbFactory, loggerFactory.Object, _sessionContext);
        _source.Apply(ThreeSectors(), Loops());
    }

    private static Passing Pass(uint transponder, uint loop, double seconds, bool inPit = false) =>
        new() { TransponderId = transponder, LoopId = loop, TimestampUtc = Start.AddSeconds(seconds), IsInPit = inPit };

    [TestMethod]
    public void Layout_ComesFromTheSetup()
    {
        Assert.IsTrue(_source.IsConfigured);
        CollectionAssert.AreEqual(new[] { "S1", "Mid", "S3" }, _source.Layout.Select(s => s.Label).ToArray());
        Assert.AreEqual(1200, _source.Layout[0].LengthMeters);
        Assert.IsNull(_source.Layout[1].LengthMeters);
    }

    [TestMethod]
    public void ConsecutiveLoops_MakeSectors_AroundTheLap()
    {
        var crossings = _source.Process(
        [
            Pass(1007, 1, 0),
            Pass(1007, 2, 30.5),
            Pass(1007, 4, 40), // not a boundary
            Pass(1007, 3, 75.25),
            Pass(1007, 1, 108),
        ]);

        CollectionAssert.AreEqual(new[]
        {
            new SectorCrossing("7", 1, 30_500),
            new SectorCrossing("7", 2, 44_750),
            new SectorCrossing("7", 3, 32_750),
        }, crossings);
    }

    [TestMethod]
    public void FirstPassing_StartsTheCount_WithNoSector()
    {
        Assert.AreEqual(0, _source.Process([Pass(1007, 2, 0)]).Count);
        Assert.AreEqual(1, _source.Process([Pass(1007, 3, 45)]).Count, "Carried across batches");
    }

    [TestMethod]
    public void MissedLoop_GivesNoTimeForTheSectorsEitherSide()
    {
        var crossings = _source.Process(
        [
            Pass(1007, 1, 0),
            // loop 2 not read
            Pass(1007, 3, 75),
            Pass(1007, 1, 108),
        ]);

        CollectionAssert.AreEqual(new[] { new SectorCrossing("7", 3, 33_000) }, crossings);
    }

    [TestMethod]
    public void PitLane_MarksTheSectorInvalid_AndOnlyThatOne()
    {
        var crossings = _source.Process(
        [
            Pass(1007, 3, 0),
            Pass(1007, 9, 20, inPit: true),
            Pass(1007, 1, 80),
            Pass(1007, 2, 110),
        ]);

        Assert.AreEqual(2, crossings.Count);
        Assert.IsTrue(crossings[0].IsInvalid, "S3 ran through the pit lane");
        Assert.IsFalse(crossings[1].IsInvalid);
    }

    [TestMethod]
    public void DoubleReadAndResend_AreIgnored()
    {
        var crossings = _source.Process(
        [
            Pass(1007, 1, 0),
            Pass(1007, 1, 0.3), // the same pass read twice
            Pass(1007, 2, 30),
        ]);
        crossings.AddRange(_source.Process([Pass(1007, 1, 0), Pass(1007, 2, 30)])); // resent

        CollectionAssert.AreEqual(new[] { new SectorCrossing("7", 1, 30_000) }, crossings);
    }

    [TestMethod]
    public void BatchOutOfOrder_IsTakenInTimeOrder()
    {
        var crossings = _source.Process([Pass(1007, 2, 30), Pass(1007, 1, 0)]);

        CollectionAssert.AreEqual(new[] { new SectorCrossing("7", 1, 30_000) }, crossings);
    }

    [TestMethod]
    public void UnknownTransponder_GivesNoSector()
    {
        Assert.AreEqual(0, _source.Process([Pass(5555, 1, 0), Pass(5555, 2, 30)]).Count);
    }

    [TestMethod]
    public void StoppedCar_GivesNoSector()
    {
        var crossings = _source.Process([Pass(1007, 1, 0), Pass(1007, 2, X2SectorSource.MaxSectorTime.TotalSeconds + 1)]);

        Assert.AreEqual(0, crossings.Count);
    }

    [TestMethod]
    public void Cars_AreTrackedSeparately()
    {
        var crossings = _source.Process([Pass(1007, 1, 0), Pass(1008, 1, 1), Pass(1008, 2, 30), Pass(1007, 2, 31)]);

        CollectionAssert.AreEquivalent(new[] { new SectorCrossing("8", 1, 29_000), new SectorCrossing("7", 1, 31_000) }, crossings);
    }

    [TestMethod]
    public void NewSession_ForgetsWhereCarsWere()
    {
        _source.Process([Pass(1007, 1, 0)]);
        _sessionContext.SessionState.SessionId = 2;

        Assert.AreEqual(0, _source.Process([Pass(1007, 2, 30)]).Count);
    }

    [TestMethod]
    public void MovedBoundary_ForgetsWhereCarsWere()
    {
        _source.Process([Pass(1007, 1, 0)]);
        var moved = ThreeSectors();
        moved.Sectors[0].EndLoopId = 4;
        _source.Apply(moved, Loops());

        Assert.AreEqual(0, _source.Process([Pass(1007, 4, 30)]).Count);
    }

    [TestMethod]
    public void Apply_SaysWhetherTheSectorsThemselvesChanged()
    {
        var relabeled = ThreeSectors();
        relabeled.Sectors[0].Label = "Front";
        relabeled.Sectors[0].LengthMeters = 1300;
        Assert.IsFalse(_source.Apply(relabeled, Loops()), "Labels and lengths only");

        var moved = ThreeSectors();
        moved.Sectors[0].EndLoopId = 4;
        Assert.IsTrue(_source.Apply(moved, Loops()), "A boundary moved");

        Assert.IsTrue(_source.Apply(new SectorConfiguration(), Loops()), "Sectors removed");
        Assert.IsFalse(_source.Apply(new SectorConfiguration(), Loops()), "Still none");
    }

    [TestMethod]
    public void LabelOnlyChange_KeepsWhereCarsWere()
    {
        _source.Process([Pass(1007, 1, 0)]);
        var relabeled = ThreeSectors();
        relabeled.Sectors[0].Label = "Front";
        _source.Apply(relabeled, Loops());

        Assert.AreEqual(1, _source.Process([Pass(1007, 2, 30)]).Count);
    }

    [TestMethod]
    public void StalePitPassing_DoesNotSpoilTheNextSector()
    {
        _source.Process([Pass(1007, 1, 0), Pass(1007, 2, 30)]);

        // A pit passing from before the car's last loop, arriving late.
        var crossings = _source.Process([Pass(1007, 9, -50, inPit: true), Pass(1007, 3, 75)]);

        Assert.IsFalse(crossings.Single().IsInvalid);
    }

    [TestMethod]
    public void WholeLapOfLoopsMissed_GivesNoSector()
    {
        GivenLaps(lastLap: "00:01:48.000", bestLap: "00:01:47.000");

        // Read at start/finish, then nothing for a lap, then at the end of sector 1 a lap later.
        var crossings = _source.Process([Pass(1007, 1, 0), Pass(1007, 2, 138)]);

        Assert.AreEqual(0, crossings.Count);
    }

    [TestMethod]
    public void SlowSector_UnderALapLong_StillCounts()
    {
        GivenLaps(lastLap: "00:01:48.000", bestLap: "00:01:47.000");

        var crossings = _source.Process([Pass(1007, 1, 0), Pass(1007, 2, 100)]);

        Assert.AreEqual(100_000, crossings.Single().TimeMs);
    }

    [TestMethod]
    public void DecoderClockSetBack_StartsTheCarAfresh()
    {
        _source.Process([Pass(1007, 1, 3600)]);

        // An hour earlier by the decoder's clock: every passing would otherwise be ignored.
        var crossings = _source.Process([Pass(1007, 1, 0), Pass(1007, 2, 30)]);

        CollectionAssert.AreEqual(new[] { new SectorCrossing("7", 1, 30_000) }, crossings);
    }

    [TestMethod]
    public void OneSectorSetup_TimesTheLap()
    {
        _source.Apply(new SectorConfiguration { StartFinishLoopId = 1, Sectors = [new SectorDefinition()] }, Loops());
        GivenLaps(lastLap: "00:01:48.000", bestLap: "00:01:47.000");

        // A lap slower than the last still counts; one where the line was missed does not.
        var crossings = _source.Process([Pass(1007, 1, 0), Pass(1007, 2, 30), Pass(1007, 1, 115), Pass(1007, 1, 335)]);

        CollectionAssert.AreEqual(new[] { new SectorCrossing("7", 1, 115_000) }, crossings);
    }

    [TestMethod]
    public void SetupThatNoLongerFitsTheLoops_IsSetAside()
    {
        Assert.IsFalse(_source.Apply(ThreeSectors(), Loops().Where(l => l.Id != 3).ToList()), "Set aside is not a change of sectors");

        Assert.IsFalse(_source.IsConfigured);
        Assert.AreEqual(0, _source.Process([Pass(1007, 1, 0), Pass(1007, 2, 30)]).Count);
    }

    [TestMethod]
    public void SetupSetAsideAndRestored_KeepsItsTimes()
    {
        _source.Process([Pass(1007, 1, 0)]);
        _source.Apply(ThreeSectors(), Loops().Where(l => l.Id != 3).ToList());

        Assert.IsFalse(_source.Apply(ThreeSectors(), Loops()), "The same sectors as before it was set aside");
        Assert.AreEqual(1, _source.Process([Pass(1007, 2, 30)]).Count, "Progress kept");
    }

    [TestMethod]
    public void OldResend_NeverRestartsTheCar()
    {
        _source.Process([Pass(1007, 1, 1800), Pass(1007, 2, 1830)]);

        // A whole lap resent from half an hour earlier, after a reconnect.
        var resent = new[] { Pass(1007, 1, 0), Pass(1007, 2, 30), Pass(1007, 3, 75) };
        foreach (var pass in resent)
            pass.IsResend = true;

        Assert.AreEqual(0, _source.Process(resent).Count);
        Assert.AreEqual(1, _source.Process([Pass(1007, 3, 1875)]).Count, "The live sequence carries on");
    }

    [TestMethod]
    public void ShortFirstLap_DoesNotRefuseRealSectors()
    {
        var car = _sessionContext.GetCarByNumber("7")!;
        car.LastLapCompleted = 1;
        car.LastLapTime = "00:00:06.500"; // a partial lap from a grid behind the line

        Assert.AreEqual(1, _source.Process([Pass(1007, 1, 0), Pass(1007, 2, 30)]).Count);
    }

    [TestMethod]
    public void BestSetOnAShortFirstLap_IsNotTheLimit()
    {
        var car = _sessionContext.GetCarByNumber("7")!;
        car.LastLapCompleted = 3;
        car.LastLapTime = "00:01:48.000";
        car.BestLap = 1;
        car.BestTime = "00:00:06.500";

        Assert.AreEqual(1, _source.Process([Pass(1007, 1, 0), Pass(1007, 2, 30)]).Count);
    }

    [TestMethod]
    public void PitLapAsTheLastLap_StillCatchesAMissedLap()
    {
        GivenLaps(lastLap: "00:04:00.000", bestLap: "00:01:47.000");

        // A lap and a typical third of a lap after the last read: under the 4:00 pit lap, over the best.
        var crossings = _source.Process([Pass(1007, 1, 0), Pass(1007, 2, 143)]);

        Assert.AreEqual(0, crossings.Count);
    }

    [TestMethod]
    public void ResumedSession_GettingItsNumber_KeepsWhereCarsWere()
    {
        _sessionContext.SessionState.SessionId = 0;
        _source.Process([Pass(1007, 1, 0)]);
        _sessionContext.SessionState.SessionId = 7;

        Assert.AreEqual(1, _source.Process([Pass(1007, 2, 30)]).Count);
    }

    [TestMethod]
    public async Task FailedRead_IsRetried_AndReportsTheChangeItFinds()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
        var options = new DbContextOptionsBuilder<TsContext>().UseInMemoryDatabase($"X2Retry_{Guid.NewGuid()}").Options;
        await using (var db = new TsContext(options))
        {
            db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event { Id = 1, Name = "Retry", LoopsMetadata = Loops() });
            db.EventSectorConfigurations.Add(new EventSectorConfiguration { EventId = 1, Configuration = ThreeSectors() });
            await db.SaveChangesAsync();
        }
        var factory = new FailingOnceFactory(options);
        var clock = new FakeTimeProvider();
        var source = new X2SectorSource(factory, loggerFactory.Object, _sessionContext, clock);

        Assert.IsFalse(await source.EnsureLoadedAsync());
        Assert.IsFalse(source.IsConfigured);

        Assert.IsFalse(await source.EnsureLoadedAsync(), "Not again straight away");
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.IsTrue(await source.EnsureLoadedAsync(), "The retry finds the setup, a change from none");
        Assert.IsTrue(source.IsConfigured);
    }

    private void GivenLaps(string lastLap, string bestLap)
    {
        var car = _sessionContext.GetCarByNumber("7")!;
        car.LastLapCompleted = 3;
        car.LastLapTime = lastLap;
        car.BestLap = 2;
        car.BestTime = bestLap;
    }

    private sealed class FailingOnceFactory(DbContextOptions<TsContext> options) : IDbContextFactory<TsContext>
    {
        private bool failed;

        public TsContext CreateDbContext()
        {
            if (!failed)
            {
                failed = true;
                throw new InvalidOperationException("database unavailable");
            }
            return new TsContext(options);
        }
    }

    [TestMethod]
    public void EmptySetup_TurnsSectorsOff()
    {
        _source.Apply(new SectorConfiguration(), Loops());

        Assert.IsFalse(_source.IsConfigured);
        Assert.AreEqual(0, _source.Layout.Count);
    }

    // ---- setup validation ---------------------------------------------------------------

    [TestMethod]
    public void Validate_AcceptsAGoodSetup_AndAnEmptyOne()
    {
        Assert.IsNull(ThreeSectors().Validate(Loops()));
        Assert.IsNull(new SectorConfiguration().Validate(Loops()));
    }

    [TestMethod]
    [DataRow("no start/finish")]
    [DataRow("pit start/finish")]
    [DataRow("missing end loop")]
    [DataRow("unknown end loop")]
    [DataRow("pit end loop")]
    [DataRow("loop used twice")]
    [DataRow("start/finish reused")]
    [DataRow("last sector with a loop")]
    [DataRow("long label")]
    [DataRow("negative length")]
    [DataRow("too many sectors")]
    [DataRow("missing sector")]
    public void Validate_RefusesABadSetup(string problem)
    {
        var setup = ThreeSectors();
        switch (problem)
        {
            case "no start/finish": setup.StartFinishLoopId = null; break;
            case "pit start/finish": setup.StartFinishLoopId = 9; break;
            case "missing end loop": setup.Sectors[0].EndLoopId = null; break;
            case "unknown end loop": setup.Sectors[0].EndLoopId = 77; break;
            case "pit end loop": setup.Sectors[0].EndLoopId = 9; break;
            case "loop used twice": setup.Sectors[1].EndLoopId = 2; break;
            case "start/finish reused": setup.Sectors[0].EndLoopId = 1; break;
            case "last sector with a loop": setup.Sectors[2].EndLoopId = 4; break;
            case "long label": setup.Sectors[0].Label = "Sector one"; break;
            case "negative length": setup.Sectors[0].LengthMeters = -5; break;
            case "missing sector": setup.Sectors[1] = null!; break;
            case "too many sectors": setup.Sectors = [.. Enumerable.Range(0, 9).Select(_ => new SectorDefinition())]; break;
        }

        Assert.IsNotNull(setup.Validate(Loops()), problem);
    }
}
