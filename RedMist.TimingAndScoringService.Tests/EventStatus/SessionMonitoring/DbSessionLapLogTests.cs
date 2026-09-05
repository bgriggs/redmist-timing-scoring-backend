using Microsoft.EntityFrameworkCore;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.EventStatus.SessionMonitoring.Metrics;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.EventProcessor.Tests.EventStatus.SessionMonitoring;

/// <summary>
/// Reading a session's lap log back out of the database. The rows are written by another service
/// and hold a serialized car position each, so what is tested here is that they come back in the
/// order the laps were run, scoped to one session, and that a row nobody can read does not take the
/// rest of the session with it.
/// </summary>
[TestClass]
public class DbSessionLapLogTests
{
    private const int EventId = 7;
    private const int SessionId = 3;

    private static readonly DateTime Start = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ReadLaps_ReturnsTheLapsInTheOrderTheyWereCompleted()
    {
        var factory = await SeedAsync(
            Row("1", lap: 1, atSeconds: 30, Flags.Green, overall: 1, inClass: 1),
            Row("2", lap: 1, atSeconds: 32, Flags.Green, overall: 2, inClass: 2),
            Row("2", lap: 2, atSeconds: 90, Flags.Yellow, overall: 1, inClass: 1),
            Row("1", lap: 2, atSeconds: 95, Flags.Yellow, overall: 2, inClass: 2));

        var laps = new DbSessionLapLog(factory, EventId, SessionId).ReadLaps().ToList();

        CollectionAssert.AreEqual(new[] { "1", "2", "2", "1" }, laps.Select(l => l.CarNumber).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 1, 2, 2 }, laps.Select(l => l.LapNumber).ToArray());
        Assert.AreEqual(Flags.Yellow, laps[2].Flag);
        Assert.AreEqual(1, laps[2].OverallPosition);
        Assert.AreEqual(1, laps[2].ClassPosition);
    }

    /// <summary>
    /// A session number is not unique across events, and zero is a session number the feed really
    /// does emit, so neither part of the key can be treated as optional.
    /// </summary>
    [TestMethod]
    public async Task ReadLaps_ReadsOnlyTheLapsOfOneSessionOfOneEvent()
    {
        var mine = Row("1", lap: 1, atSeconds: 30, Flags.Green, overall: 1, inClass: 1);
        var otherSession = Row("1", lap: 1, atSeconds: 31, Flags.Green, overall: 1, inClass: 1);
        otherSession.SessionId = SessionId + 1;
        var otherEvent = Row("1", lap: 1, atSeconds: 32, Flags.Green, overall: 1, inClass: 1);
        otherEvent.EventId = EventId + 1;

        var factory = await SeedAsync(mine, otherSession, otherEvent);
        var log = new DbSessionLapLog(factory, EventId, SessionId);

        Assert.HasCount(1, log.ReadLaps().ToList());
        Assert.HasCount(1, log.ReadSummary());
    }

    [TestMethod]
    public async Task ReadSummary_ReportsWhatEachCarLogged()
    {
        var factory = await SeedAsync(
            Row("1", lap: 1, atSeconds: 30, Flags.Green, overall: 1, inClass: 1),
            Row("1", lap: 2, atSeconds: 90, Flags.Green, overall: 1, inClass: 1),
            Row("1", lap: 3, atSeconds: 150, Flags.Green, overall: 1, inClass: 1),
            Row("2", lap: 1, atSeconds: 32, Flags.Green, overall: 2, inClass: 2));

        var summary = new DbSessionLapLog(factory, EventId, SessionId).ReadSummary();

        Assert.AreEqual(new LoggedCarSummary(3, 3), summary["1"]);
        Assert.AreEqual(new LoggedCarSummary(1, 1), summary["2"]);
    }

    /// <summary>
    /// The metrics are counts over thousands of laps, so one unreadable row moves them far less than
    /// abandoning the session's metrics altogether would.
    /// </summary>
    [TestMethod]
    public async Task ReadLaps_SkipsARowWhoseLapDataCannotBeRead()
    {
        var corrupt = Row("2", lap: 1, atSeconds: 32, Flags.Green, overall: 2, inClass: 2);
        corrupt.LapData = "{not json";
        var empty = Row("3", lap: 1, atSeconds: 33, Flags.Green, overall: 3, inClass: 3);
        empty.LapData = string.Empty;

        var factory = await SeedAsync(
            Row("1", lap: 1, atSeconds: 30, Flags.Green, overall: 1, inClass: 1),
            corrupt,
            empty,
            Row("1", lap: 2, atSeconds: 90, Flags.Green, overall: 1, inClass: 1));

        var laps = new DbSessionLapLog(factory, EventId, SessionId).ReadLaps().ToList();

        CollectionAssert.AreEqual(new[] { "1", "1" }, laps.Select(l => l.CarNumber).ToArray());
    }

    [TestMethod]
    public async Task ReadLaps_ForASessionWithNoLaps_ReturnsNothing()
    {
        var factory = await SeedAsync();
        var log = new DbSessionLapLog(factory, EventId, SessionId);

        Assert.IsEmpty(log.ReadLaps().ToList());
        Assert.IsEmpty(log.ReadSummary());
    }

    /// <summary>
    /// The rows are written by the event logger from what the lap processor serialized, so the two
    /// have to agree on the shape. Reading a position the processor itself produced is what keeps
    /// them honest.
    /// </summary>
    [TestMethod]
    public async Task ReadLaps_ReadsThePositionTheLapProcessorSerialized()
    {
        var position = new CarPosition
        {
            Number = "54",
            Class = "GTO",
            OverallPosition = 1,
            ClassPosition = 1,
            LastLapCompleted = 143,
        };

        var factory = await SeedAsync(new CarLapLog
        {
            EventId = EventId,
            SessionId = SessionId,
            CarNumber = "54",
            LapNumber = 143,
            Timestamp = Start,
            Flag = (int)Flags.Checkered,
            LapData = JsonSerializer.Serialize(position),
        });

        var lap = new DbSessionLapLog(factory, EventId, SessionId).ReadLaps().Single();

        Assert.AreEqual(1, lap.OverallPosition);
        Assert.AreEqual(1, lap.ClassPosition);
        Assert.AreEqual(Flags.Checkered, lap.Flag);
    }

    private static CarLapLog Row(string car, int lap, int atSeconds, Flags flag, int overall, int inClass) => new()
    {
        EventId = EventId,
        SessionId = SessionId,
        CarNumber = car,
        LapNumber = lap,
        Timestamp = Start.AddSeconds(atSeconds),
        Flag = (int)flag,
        LapData = JsonSerializer.Serialize(new CarPosition
        {
            Number = car,
            OverallPosition = overall,
            ClassPosition = inClass,
            LastLapCompleted = lap,
        }),
    };

    private async Task<IDbContextFactory<TsContext>> SeedAsync(params CarLapLog[] rows)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        var factory = new TestDbContextFactory(optionsBuilder.Options);

        await using var db = factory.CreateDbContext();
        db.CarLapLogs.AddRange(rows);
        await db.SaveChangesAsync(TestContext.CancellationToken);
        return factory;
    }
}
