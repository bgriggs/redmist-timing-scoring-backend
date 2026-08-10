using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventOrchestration.Services;
using RedMist.EventOrchestration.Utilities;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingAndScoringService.Tests.EventOrchestration.Utilities;

namespace RedMist.TimingAndScoringService.Tests.EventOrchestration.Services;

/// <summary>
/// Simulated events are deleted outright, so the retention window and the "is this a simulation"
/// filter are the only things standing between a replay and a real event's data.
/// </summary>
[TestClass]
public class SimulatedEventPurgeServiceTests
{
    private IDbContextFactory<TsContext> dbFactory = null!;
    private Mock<ILoggerFactory> loggerFactory = null!;
    private readonly List<string> failureEmailBodies = [];

    [TestInitialize]
    public void Setup()
    {
        dbFactory = PurgeUtilitiesTests.CreateFactory($"SimulatedEventPurgeServiceTests_{Guid.NewGuid()}");
        loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
    }

    private SimulatedEventPurgeService CreateService(IDbContextFactory<TsContext>? factory = null)
    {
        factory ??= dbFactory;
        var archiveEmailHelper = new ArchiveEmailHelper(new Mock<ILogger>().Object, factory, emailHelper: null,
            (_, body, _, _) =>
            {
                failureEmailBodies.Add(body);
                return Task.CompletedTask;
            });
        return new SimulatedEventPurgeService(loggerFactory.Object, factory, emailHelper: null, archiveEmailHelper);
    }

    [TestMethod]
    public async Task RunSimulatedEventPurgeAsync_PurgesOnlySimulatedEventsThatEndedMoreThanADayAgo()
    {
        var now = DateTime.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Events.Add(NewEvent(1, isSimulation: true, endDate: now.AddDays(-5)));
            db.Events.Add(NewEvent(2, isSimulation: true, endDate: now.AddHours(-25)));
            db.Events.Add(NewEvent(3, isSimulation: true, endDate: now.AddHours(-23)));
            db.Events.Add(NewEvent(4, isSimulation: false, endDate: now.AddDays(-5)));
            db.Events.Add(NewEvent(5, isSimulation: true, endDate: now.AddDays(1)));
            await db.SaveChangesAsync();
        }

        await CreateService().RunSimulatedEventPurgeAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        var remaining = await check.Events.Select(e => e.Id).OrderBy(id => id).ToListAsync();
        CollectionAssert.AreEqual(new[] { 3, 4, 5 }, remaining);
        Assert.IsEmpty(failureEmailBodies);
    }

    [TestMethod]
    public async Task RunSimulatedEventPurgeAsync_AlsoRemovesThePurgedEventsChildData()
    {
        var now = DateTime.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Events.Add(NewEvent(1, isSimulation: true, endDate: now.AddDays(-5)));
            db.Events.Add(NewEvent(2, isSimulation: false, endDate: now.AddDays(-5)));
            db.CarLapLogs.Add(NewLap(1));
            db.CarLapLogs.Add(NewLap(2));
            db.EventStatusLogs.Add(new EventStatusLog { EventId = 1, SessionId = 1, Type = "t", Data = "d", Timestamp = now });
            db.EventStatusLogs.Add(new EventStatusLog { EventId = 2, SessionId = 1, Type = "t", Data = "d", Timestamp = now });
            await db.SaveChangesAsync();
        }

        await CreateService().RunSimulatedEventPurgeAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await check.CarLapLogs.CountAsync(l => l.EventId == 1));
        Assert.AreEqual(0, await check.EventStatusLogs.CountAsync(l => l.EventId == 1));
        Assert.AreEqual(1, await check.CarLapLogs.CountAsync(l => l.EventId == 2));
        Assert.AreEqual(1, await check.EventStatusLogs.CountAsync(l => l.EventId == 2));
    }

    [TestMethod]
    public async Task RunSimulatedEventPurgeAsync_NothingToPurge_SendsNoAlert()
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Events.Add(NewEvent(1, isSimulation: false, endDate: DateTime.UtcNow.AddDays(-5)));
            await db.SaveChangesAsync();
        }

        await CreateService().RunSimulatedEventPurgeAsync(CancellationToken.None);

        Assert.IsEmpty(failureEmailBodies);
        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.HasCount(1, await check.Events.ToListAsync());
    }

    /// <summary>
    /// What is being pinned here is the alert and the containment, not the rollback:
    /// <c>PurgeUtilities.DeleteAllEventDataAsync</c> opens its transaction before its try block, so a
    /// database that refuses one fails before a single delete is issued. "Nothing should have been deleted"
    /// is true by construction, and the rollback in the catch is never reached on this path.
    /// </summary>
    [TestMethod]
    public async Task RunSimulatedEventPurgeAsync_PurgeThrows_AlertsAndDoesNotPropagate()
    {
        // A factory without transaction support stands in for a database that rejects the purge.
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"SimulatedEventPurgeServiceTests_NoTx_{Guid.NewGuid()}")
            .Options;
        var strictFactory = new TestDbContextFactory(options);
        await using (var db = await strictFactory.CreateDbContextAsync())
        {
            db.Events.Add(NewEvent(1, isSimulation: true, endDate: DateTime.UtcNow.AddDays(-5)));
            await db.SaveChangesAsync();
        }

        await CreateService(strictFactory).RunSimulatedEventPurgeAsync(CancellationToken.None);

        Assert.HasCount(1, failureEmailBodies);
        StringAssert.Contains(failureEmailBodies[0], "Simulated event purge failed:");
        await using var check = await strictFactory.CreateDbContextAsync();
        Assert.HasCount(1, await check.Events.ToListAsync(), "Nothing should have been deleted");
    }

    private static RedMist.TimingCommon.Models.Configuration.Event NewEvent(int id, bool isSimulation, DateTime endDate) => new()
    {
        Id = id,
        OrganizationId = 7,
        Name = $"Event {id}",
        IsSimulation = isSimulation,
        EndDate = endDate
    };

    private static CarLapLog NewLap(int eventId) => new()
    {
        EventId = eventId,
        SessionId = 1,
        CarNumber = "5",
        Timestamp = DateTime.UtcNow,
        LapNumber = 1,
        Flag = 1,
        LapData = "{}"
    };
}
