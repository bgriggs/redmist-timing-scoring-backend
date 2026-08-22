using RedMist.Backend.Shared;
using RedMist.Database.Models;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Covers the historical-data queries on EventsControllerBase: lap history, flag history, session
/// listing (and its cache), and competitor metadata. The lap endpoints re-deserialize JSON that was
/// written by a different service, so the tolerance of a bad row is part of the contract.
/// </summary>
[TestClass]
public class EventsControllerBaseLapAndSessionTests
{
    private EventsControllerHarness _h = null!;

    [TestInitialize]
    public void Setup() => _h = new EventsControllerHarness();

    [TestCleanup]
    public void Cleanup() => _h.Dispose();

    #region LoadCarLaps

    [TestMethod]
    public async Task LoadCarLaps_FiltersByEventSessionAndCar()
    {
        _h.AddLap(1, 10, "42", 1);
        _h.AddLap(1, 10, "43", 1);   // other car
        _h.AddLap(1, 11, "42", 1);   // other session
        _h.AddLap(2, 10, "42", 1);   // other event
        await _h.Db.SaveChangesAsync();

        var laps = await _h.Controller.LoadCarLaps(1, 10, "42");

        Assert.AreEqual(1, laps.Count);
        Assert.AreEqual("42", laps[0].Number);
        Assert.AreEqual("1", laps[0].EventId);
    }

    /// <summary>
    /// Lap 0 is the synthetic "car seen but no lap completed" row the processor writes; returning it
    /// would show a phantom first lap in the app.
    /// </summary>
    [TestMethod]
    public async Task LoadCarLaps_ExcludesLapNumberZeroAndBelow()
    {
        _h.AddLap(1, 10, "42", 0);
        _h.AddLap(1, 10, "42", -1);
        _h.AddLap(1, 10, "42", 1);
        await _h.Db.SaveChangesAsync();

        var laps = await _h.Controller.LoadCarLaps(1, 10, "42");

        Assert.AreEqual(1, laps.Count);
        Assert.AreEqual(1, laps[0].LastLapCompleted);
    }

    [TestMethod]
    public async Task LoadCarLaps_OrdersByLapNumber()
    {
        _h.AddLap(1, 10, "42", 3);
        _h.AddLap(1, 10, "42", 1);
        _h.AddLap(1, 10, "42", 2);
        await _h.Db.SaveChangesAsync();

        var laps = await _h.Controller.LoadCarLaps(1, 10, "42");

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, laps.Select(l => l.LastLapCompleted).ToArray());
    }

    /// <summary>
    /// One unparseable row must not take out the whole lap chart. The stored JSON comes from whatever
    /// version of the event processor happened to be running when the lap was recorded.
    /// </summary>
    [TestMethod]
    public async Task LoadCarLaps_MalformedLapData_SkipsRowAndKeepsTheRest()
    {
        _h.AddLap(1, 10, "42", 1);
        _h.AddLap(1, 10, "42", 2, lapData: "{ this is not json");
        _h.AddLap(1, 10, "42", 3);
        await _h.Db.SaveChangesAsync();

        var laps = await _h.Controller.LoadCarLaps(1, 10, "42");

        CollectionAssert.AreEqual(new[] { 1, 3 }, laps.Select(l => l.LastLapCompleted).ToArray());
    }

    [TestMethod]
    public async Task LoadCarLaps_EmptyLapData_IsSkippedWithoutError()
    {
        _h.AddLap(1, 10, "42", 1, lapData: string.Empty);
        _h.AddLap(1, 10, "42", 2);
        await _h.Db.SaveChangesAsync();

        var laps = await _h.Controller.LoadCarLaps(1, 10, "42");

        Assert.AreEqual(1, laps.Count);
        Assert.AreEqual(2, laps[0].LastLapCompleted);
    }

    /// <summary>JSON "null" deserializes to a null CarPosition, which must be dropped rather than added.</summary>
    [TestMethod]
    public async Task LoadCarLaps_NullJsonLapData_IsSkipped()
    {
        _h.AddLap(1, 10, "42", 1, lapData: "null");
        await _h.Db.SaveChangesAsync();

        var laps = await _h.Controller.LoadCarLaps(1, 10, "42");

        Assert.AreEqual(0, laps.Count);
    }

    [TestMethod]
    public async Task LoadCarLaps_UnknownCar_ReturnsEmpty()
    {
        _h.AddLap(1, 10, "42", 1);
        await _h.Db.SaveChangesAsync();

        var laps = await _h.Controller.LoadCarLaps(1, 10, "99");

        Assert.AreEqual(0, laps.Count);
    }

    #endregion

    #region LoadSessionLaps

    [TestMethod]
    public async Task LoadSessionLaps_ReturnsEveryCarOrderedByCarThenLap()
    {
        _h.AddLap(1, 10, "7", 2);
        _h.AddLap(1, 10, "7", 1);
        _h.AddLap(1, 10, "42", 1);
        _h.AddLap(1, 11, "7", 1);   // other session
        await _h.Db.SaveChangesAsync();

        var laps = await _h.Controller.LoadSessionLaps(1, 10);

        Assert.AreEqual(3, laps.Count);
        CollectionAssert.AreEqual(new[] { "42", "7", "7" }, laps.Select(l => l.Number).ToArray());
        Assert.AreEqual(1, laps[1].LastLapCompleted);
        Assert.AreEqual(2, laps[2].LastLapCompleted);
    }

    #endregion

    #region LoadFlags

    [TestMethod]
    public async Task LoadFlags_FiltersByEventAndSessionAndMapsDurations()
    {
        var green = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        _h.Db.FlagLog.AddRange(
            new FlagLog { EventId = 1, SessionId = 10, Flag = Flags.Green, StartTime = green, EndTime = green.AddMinutes(20) },
            new FlagLog { EventId = 1, SessionId = 10, Flag = Flags.Yellow, StartTime = green.AddMinutes(20), EndTime = null },
            new FlagLog { EventId = 1, SessionId = 11, Flag = Flags.Red, StartTime = green },
            new FlagLog { EventId = 2, SessionId = 10, Flag = Flags.Black, StartTime = green });
        await _h.Db.SaveChangesAsync();

        var flags = await _h.Controller.LoadFlags(1, 10);

        Assert.AreEqual(2, flags.Count);
        var greenDuration = flags.Single(f => f.Flag == Flags.Green);
        Assert.AreEqual(green, greenDuration.StartTime);
        Assert.AreEqual(green.AddMinutes(20), greenDuration.EndTime);
        Assert.IsNull(flags.Single(f => f.Flag == Flags.Yellow).EndTime, "an in-progress flag keeps a null end time");
    }

    #endregion

    #region LoadSessions

    [TestMethod]
    public async Task LoadSessions_FiltersByEventAndCachesUnderPerEventKey()
    {
        _h.AddSession(1, 100, "Practice");
        _h.AddSession(2, 100, "Race");
        _h.AddSession(1, 200, "Other Event Race");
        await _h.Db.SaveChangesAsync();

        var sessions = await _h.Controller.LoadSessions(100);

        CollectionAssert.AreEquivalent(new[] { "Practice", "Race" }, sessions.Select(s => s.Name).ToArray());
        Assert.Contains("sessions:100", _h.Cache.FactoryInvocations);
    }

    [TestMethod]
    public async Task LoadSessions_SecondCall_ServedFromCache()
    {
        _h.AddSession(1, 100, "Practice");
        await _h.Db.SaveChangesAsync();

        var first = await _h.Controller.LoadSessions(100);
        _h.AddSession(2, 100, "Race");
        await _h.Db.SaveChangesAsync();
        var second = await _h.Controller.LoadSessions(100);

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(1, second.Count);
        Assert.AreEqual(1, _h.Cache.FactoryInvocations.Count(k => k == "sessions:100"));
    }

    /// <summary>
    /// The timing system announces a scratch run of its own at every run change, and one that lands
    /// at the end of an event is never superseded by a real run. It ends with no cars and no laps,
    /// and listing it puts a second, empty entry beside the session that actually ran.
    /// </summary>
    [TestMethod]
    public async Task LoadSessions_LeavesOutASessionThatSawNoCars()
    {
        _h.AddSession(1, 100, "New run");
        _h.AddSession(95, 100, "New run", withResults: false);
        await _h.Db.SaveChangesAsync();

        var sessions = await _h.Controller.LoadSessions(100);

        Assert.AreEqual(1, sessions.Single().Id, "Only the session that produced results should be listed.");
    }

    /// <summary>
    /// Results and laps are written by different services, so a session whose results did not make
    /// it has to stay reachable for the laps that did.
    /// </summary>
    [TestMethod]
    public async Task LoadSessions_KeepsASessionThatHasLapsButNoResults()
    {
        _h.AddSession(7, 100, "Race", withResults: false);
        _h.AddLap(100, 7, "42", 1);
        await _h.Db.SaveChangesAsync();

        var sessions = await _h.Controller.LoadSessions(100);

        Assert.AreEqual(7, sessions.Single().Id);
    }

    /// <summary>The live session has no results yet - they are written when it ends.</summary>
    [TestMethod]
    public async Task LoadSessions_KeepsTheLiveSession()
    {
        _h.AddSession(8, 100, "Race", withResults: false);
        _h.Db.Sessions.Local.Single(s => s.Id == 8 && s.EventId == 100).IsLive = true;
        await _h.Db.SaveChangesAsync();

        var sessions = await _h.Controller.LoadSessions(100);

        Assert.AreEqual(8, sessions.Single().Id);
    }

    /// <summary>Two events served by one process must not share a session list.</summary>
    [TestMethod]
    public async Task LoadSessions_DifferentEvents_DoNotShareACacheEntry()
    {
        _h.AddSession(1, 100, "Event100 Race");
        _h.AddSession(1, 200, "Event200 Race");
        await _h.Db.SaveChangesAsync();

        var first = await _h.Controller.LoadSessions(100);
        var second = await _h.Controller.LoadSessions(200);

        Assert.AreEqual("Event100 Race", first.Single().Name);
        Assert.AreEqual("Event200 Race", second.Single().Name);
    }

    #endregion

    #region Competitor metadata

    [TestMethod]
    public async Task LoadCompetitorMetadata_UnknownCar_ReturnsNull()
    {
        var metadata = await _h.Controller.LoadCompetitorMetadata(1, "42");

        Assert.IsNull(metadata);
    }

    [TestMethod]
    public async Task LoadCompetitorMetadata_ReturnsMatchingCarAndCachesPerCarAndEvent()
    {
        _h.Db.CompetitorMetadata.AddRange(
            new CompetitorMetadata { EventId = 1, CarNumber = "42", LastName = "Andretti" },
            new CompetitorMetadata { EventId = 1, CarNumber = "7", LastName = "Unser" },
            new CompetitorMetadata { EventId = 2, CarNumber = "42", LastName = "Other Event" });
        await _h.Db.SaveChangesAsync();

        var car42 = await _h.Controller.LoadCompetitorMetadata(1, "42");
        var car7 = await _h.Controller.LoadCompetitorMetadata(1, "7");

        Assert.AreEqual("Andretti", car42!.LastName);
        Assert.AreEqual("Unser", car7!.LastName);
        Assert.Contains(string.Format(Consts.COMPETITOR_METADATA, "42", 1), _h.Cache.FactoryInvocations);
        Assert.Contains(string.Format(Consts.COMPETITOR_METADATA, "7", 1), _h.Cache.FactoryInvocations);
    }

    [TestMethod]
    public async Task LoadCompetitorMetadata_SecondCall_ServedFromCache()
    {
        _h.Db.CompetitorMetadata.Add(new CompetitorMetadata { EventId = 1, CarNumber = "42", LastName = "Andretti" });
        await _h.Db.SaveChangesAsync();

        await _h.Controller.LoadCompetitorMetadata(1, "42");
        await _h.Controller.LoadCompetitorMetadata(1, "42");

        Assert.AreEqual(1, _h.Cache.FactoryInvocations
            .Count(k => k == string.Format(Consts.COMPETITOR_METADATA, "42", 1)));
    }

    [TestMethod]
    public async Task LoadEventCompetitorMetadata_FiltersByEvent()
    {
        _h.Db.CompetitorMetadata.AddRange(
            new CompetitorMetadata { EventId = 1, CarNumber = "42" },
            new CompetitorMetadata { EventId = 1, CarNumber = "7" },
            new CompetitorMetadata { EventId = 2, CarNumber = "99" });
        await _h.Db.SaveChangesAsync();

        var metadata = await _h.Controller.LoadEventCompetitorMetadata(1);

        CollectionAssert.AreEquivalent(new[] { "42", "7" }, metadata.Select(m => m.CarNumber).ToArray());
    }

    #endregion
}
