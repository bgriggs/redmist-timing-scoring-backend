using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventOrchestration.Utilities;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.X2;

namespace RedMist.TimingAndScoringService.Tests.EventOrchestration.Utilities;

/// <summary>
/// Purging is destructive and irreversible, so these pin down exactly which rows go and which stay.
/// </summary>
[TestClass]
public class PurgeUtilitiesTests
{
    private IDbContextFactory<TsContext> dbFactory = null!;
    private PurgeUtilities purge = null!;

    [TestInitialize]
    public void Setup()
    {
        dbFactory = CreateFactory($"PurgeUtilitiesTests_{Guid.NewGuid()}");
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
        purge = new PurgeUtilities(loggerFactory.Object, dbFactory);
    }

    /// <summary>
    /// The InMemory provider has no transactions; without this the purge would fail before doing any work.
    /// </summary>
    internal static IDbContextFactory<TsContext> CreateFactory(string databaseName)
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestDbContextFactory(options);
    }

    [TestMethod]
    public async Task DeleteEventStatusLogsFromDatabaseAsync_RemovesOnlyTheTargetEventsLogs()
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.EventStatusLogs.Add(new EventStatusLog { EventId = 1, SessionId = 1, Type = "t", Data = "d", Timestamp = DateTime.UtcNow });
            db.EventStatusLogs.Add(new EventStatusLog { EventId = 1, SessionId = 2, Type = "t", Data = "d", Timestamp = DateTime.UtcNow });
            db.EventStatusLogs.Add(new EventStatusLog { EventId = 2, SessionId = 1, Type = "t", Data = "d", Timestamp = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await purge.DeleteEventStatusLogsFromDatabaseAsync(1, totalLogs: 2, CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await check.EventStatusLogs.CountAsync(e => e.EventId == 1));
        Assert.AreEqual(1, await check.EventStatusLogs.CountAsync(e => e.EventId == 2));
    }

    [TestMethod]
    public async Task DeleteLapsFromDatabaseAsync_RemovesOnlyTheTargetEventAndSession()
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.CarLapLogs.Add(NewLap(eventId: 1, sessionId: 1));
            db.CarLapLogs.Add(NewLap(eventId: 1, sessionId: 2));
            db.CarLapLogs.Add(NewLap(eventId: 2, sessionId: 1));
            await db.SaveChangesAsync();
        }

        await purge.DeleteLapsFromDatabaseAsync(eventId: 1, sessionId: 1, totalLaps: 1, CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await check.CarLapLogs.CountAsync(l => l.EventId == 1 && l.SessionId == 1));
        Assert.AreEqual(1, await check.CarLapLogs.CountAsync(l => l.EventId == 1 && l.SessionId == 2));
        Assert.AreEqual(1, await check.CarLapLogs.CountAsync(l => l.EventId == 2));
    }

    [TestMethod]
    public async Task DeleteAllEventDataAsync_RemovesEveryChildTableAndTheEventItself()
    {
        await SeedFullEventAsync(eventId: 1);

        await purge.DeleteAllEventDataAsync(1, CancellationToken.None);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.IsEmpty(await db.CarLapLogs.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.CarLastLaps.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.CompetitorMetadata.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.EventStatusLogs.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.FlagLog.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.SessionResults.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.Sessions.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.X2Loops.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.X2Passings.Where(e => e.EventId == 1).ToListAsync());
        Assert.IsEmpty(await db.Events.Where(e => e.Id == 1).ToListAsync());
    }

    [TestMethod]
    public async Task DeleteAllEventDataAsync_LeavesOtherEventsIntact()
    {
        await SeedFullEventAsync(eventId: 1);
        await SeedFullEventAsync(eventId: 2);

        await purge.DeleteAllEventDataAsync(1, CancellationToken.None);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.HasCount(1, await db.CarLapLogs.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.CarLastLaps.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.CompetitorMetadata.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.EventStatusLogs.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.FlagLog.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.SessionResults.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.Sessions.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.X2Loops.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.X2Passings.Where(e => e.EventId == 2).ToListAsync());
        Assert.HasCount(1, await db.Events.Where(e => e.Id == 2).ToListAsync());
    }

    [TestMethod]
    public async Task DeleteAllEventDataAsync_TransactionCannotStart_ThrowsAndDeletesNothing()
    {
        // A factory without the transaction warning suppressed stands in for a database that
        // refuses the transaction; the purge must fail loudly rather than half-delete an event.
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"PurgeUtilitiesTests_NoTx_{Guid.NewGuid()}")
            .Options;
        var strictFactory = new TestDbContextFactory(options);
        await using (var db = await strictFactory.CreateDbContextAsync())
        {
            db.CarLapLogs.Add(NewLap(eventId: 1, sessionId: 1));
            await db.SaveChangesAsync();
        }
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
        var strictPurge = new PurgeUtilities(loggerFactory.Object, strictFactory);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => strictPurge.DeleteAllEventDataAsync(1, CancellationToken.None));

        await using var check = await strictFactory.CreateDbContextAsync();
        Assert.HasCount(1, await check.CarLapLogs.Where(e => e.EventId == 1).ToListAsync());
    }

    private static CarLapLog NewLap(int eventId, int sessionId) => new()
    {
        EventId = eventId,
        SessionId = sessionId,
        CarNumber = "5",
        Timestamp = DateTime.UtcNow,
        LapNumber = 1,
        Flag = 1,
        LapData = "{}"
    };

    private async Task SeedFullEventAsync(int eventId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Events.Add(new RedMist.TimingCommon.Models.Configuration.Event { Id = eventId, OrganizationId = 7, Name = $"Event {eventId}" });
        db.CarLapLogs.Add(NewLap(eventId, sessionId: 1));
        db.CarLastLaps.Add(new CarLastLap { EventId = eventId, SessionId = 1, CarNumber = "5", LastLapNumber = 1, LastLapTimestamp = DateTime.UtcNow });
        db.CompetitorMetadata.Add(new CompetitorMetadata { EventId = eventId, CarNumber = "5", LastUpdated = DateTime.UtcNow });
        db.EventStatusLogs.Add(new EventStatusLog { EventId = eventId, SessionId = 1, Type = "t", Data = "d", Timestamp = DateTime.UtcNow });
        db.FlagLog.Add(new FlagLog { EventId = eventId, SessionId = 1, Flag = Flags.Green, StartTime = DateTime.UtcNow });
        db.SessionResults.Add(new SessionResult { EventId = eventId, SessionId = 1, Start = DateTime.UtcNow });
        db.Sessions.Add(new Session { Id = 1, EventId = eventId, Name = "Race" });
        db.X2Loops.Add(new Loop { OrganizationId = 7, EventId = eventId, Id = 1, Name = "L1" });
        db.X2Passings.Add(new Passing { OrganizationId = 7, EventId = eventId, Id = 1, LoopId = 1 });
        await db.SaveChangesAsync();
    }
}
