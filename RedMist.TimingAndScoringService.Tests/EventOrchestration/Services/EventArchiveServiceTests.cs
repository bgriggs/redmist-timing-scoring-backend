using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventOrchestration.Services;
using RedMist.EventOrchestration.Utilities;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventOrchestration.Services;

/// <summary>
/// Covers which events the nightly archive picks up, how it reacts when a stage fails, and what it
/// reports. The archive writers themselves run for real against an InMemory database with a mocked
/// blob store.
/// </summary>
[TestClass]
public class EventArchiveServiceTests
{
    private IDbContextFactory<TsContext> dbFactory = null!;
    private Mock<ILoggerFactory> loggerFactory = null!;
    private Mock<IArchiveStorage> storage = null!;
    private readonly List<string> failureReasons = [];

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"EventArchiveServiceTests_{Guid.NewGuid()}")
            .Options;
        dbFactory = new TestDbContextFactory(options);

        loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        storage = new Mock<IArchiveStorage>();
        storage.Setup(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), It.IsAny<int>())).ReturnsAsync(true);
        storage.Setup(x => x.UploadSessionLogsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(true);
        storage.Setup(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(true);
        storage.Setup(x => x.UploadSessionCarLapsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>())).ReturnsAsync(true);
        storage.Setup(x => x.UploadSessionFlagsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(true);
        storage.Setup(x => x.UploadEventX2LoopsAsync(It.IsAny<Stream>(), It.IsAny<int>())).ReturnsAsync(true);
        storage.Setup(x => x.UploadEventX2PassingsAsync(It.IsAny<Stream>(), It.IsAny<int>())).ReturnsAsync(true);
        storage.Setup(x => x.UploadEventCompetitorMetadataAsync(It.IsAny<Stream>(), It.IsAny<int>())).ReturnsAsync(true);
    }

    private EventArchiveService CreateService()
    {
        var archiveEmailHelper = new ArchiveEmailHelper(new Mock<ILogger>().Object, dbFactory, emailHelper: null,
            (_, body, _, _) =>
            {
                // The failure reason is rendered into the body; keep the whole body for assertions.
                failureReasons.Add(body);
                return Task.CompletedTask;
            });
        return new EventArchiveService(loggerFactory.Object, dbFactory, storage.Object, emailHelper: null, archiveEmailHelper);
    }

    #region Which events get archived

    [TestMethod]
    public async Task LoadEventsToArchiveAsync_TakesOnlyFinishedRealEventsThatAreNotYetArchived()
    {
        var now = DateTime.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Events.Add(NewEvent(1, endDate: now.AddDays(-5)));
            db.Events.Add(NewEvent(2, endDate: now.AddHours(-25)));
            db.Events.Add(NewEvent(3, endDate: now.AddHours(-23)));           // inside the one day grace period
            db.Events.Add(NewEvent(4, endDate: now.AddDays(-5), isArchived: true));
            db.Events.Add(NewEvent(5, endDate: now.AddDays(-5), isLive: true));
            db.Events.Add(NewEvent(6, endDate: now.AddDays(-5), isSimulation: true));
            db.Events.Add(NewEvent(7, endDate: now.AddDays(2)));              // has not ended yet
            await db.SaveChangesAsync();
        }

        var events = await CreateService().LoadEventsToArchiveAsync();

        CollectionAssert.AreEquivalent(new[] { 1, 2 }, events);
    }

    #endregion

    #region Retry loop

    [TestMethod]
    public async Task RunArchiveProcessWithRetriesAsync_NothingToArchive_SucceedsWithoutAlerting()
    {
        await CreateService().RunArchiveProcessWithRetriesAsync(maxRetriesPerDay: 3, CancellationToken.None);

        Assert.IsEmpty(failureReasons);
    }

    [TestMethod]
    public async Task RunArchiveProcessWithRetriesAsync_ArchiveFails_AlertsForTheEventAndForTheExhaustedRun()
    {
        await SeedArchivableEventAsync(1);
        storage.Setup(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), It.IsAny<int>())).ReturnsAsync(false);

        // One attempt keeps the five minute inter-retry wait out of the test.
        await CreateService().RunArchiveProcessWithRetriesAsync(maxRetriesPerDay: 1, CancellationToken.None);

        Assert.HasCount(2, failureReasons);
        StringAssert.Contains(failureReasons[0], "Failed to archive event logs");
        StringAssert.Contains(failureReasons[1], "Archive process failed after all retry attempts");
        StringAssert.Contains(failureReasons[1], "Retry Attempts:</strong> 1");

        await using var db = await dbFactory.CreateDbContextAsync();
        var ev = await db.Events.SingleAsync(e => e.Id == 1);
        Assert.IsFalse(ev.IsArchived, "A failed archive must not mark the event as archived");
        Assert.AreEqual(3, await db.EventStatusLogs.CountAsync(l => l.EventId == 1), "Logs must survive a failed upload");
    }

    [TestMethod]
    public async Task RunArchiveProcessWithRetriesAsync_AlreadyCanceled_DoesNoWork()
    {
        await SeedArchivableEventAsync(1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await CreateService().RunArchiveProcessWithRetriesAsync(maxRetriesPerDay: 3, cts.Token);

        storage.Verify(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), It.IsAny<int>()), Times.Never);
        // Shutdown still counts as "not successful", so an alert goes out; documented here so a
        // change to that behavior is a deliberate one.
        Assert.HasCount(1, failureReasons);
        StringAssert.Contains(failureReasons[0], "Archive process failed after all retry attempts");
    }

    #endregion

    #region Single event pipeline

    [TestMethod]
    public async Task ArchiveSingleEventAsync_EventLogUploadFails_StopsBeforeArchivingLaps()
    {
        await SeedArchivableEventAsync(1);
        storage.Setup(x => x.UploadEventLogsAsync(It.IsAny<Stream>(), It.IsAny<int>())).ReturnsAsync(false);

        var result = await CreateService().ArchiveSingleEventAsync(1, CancellationToken.None);

        Assert.IsFalse(result);
        storage.Verify(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        storage.Verify(x => x.UploadSessionFlagsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        StringAssert.Contains(failureReasons.Single(), "Failed to archive event logs");
    }

    [TestMethod]
    public async Task ArchiveSingleEventAsync_LapUploadFails_StopsBeforeArchivingX2Data()
    {
        await SeedArchivableEventAsync(1);
        storage.Setup(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(false);

        var result = await CreateService().ArchiveSingleEventAsync(1, CancellationToken.None);

        Assert.IsFalse(result);
        storage.Verify(x => x.UploadEventX2PassingsAsync(It.IsAny<Stream>(), It.IsAny<int>()), Times.Never);
        StringAssert.Contains(failureReasons.Single(), "Failed to archive laps");

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(2, await db.CarLapLogs.CountAsync(l => l.EventId == 1), "Laps must survive a failed upload");
    }

    [TestMethod]
    public async Task ArchiveSingleEventAsync_EventRowMissing_ReturnsFalseWithoutAlerting()
    {
        // Logs exist but the event definition does not; every archiver succeeds and the final
        // "mark archived" step is the one that cannot complete.
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.EventStatusLogs.Add(new EventStatusLog { EventId = 99, SessionId = 1, Type = "t", Data = "d", Timestamp = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var result = await CreateService().ArchiveSingleEventAsync(99, CancellationToken.None);

        Assert.IsFalse(result);
        Assert.IsEmpty(failureReasons);
    }

    [TestMethod]
    public async Task ArchiveSingleEventAsync_LastLapCleanupFails_EventIsAlreadyMarkedArchived()
    {
        // Characterization test, not a specification: the event row is flagged and saved before
        // CarLastLaps is cleaned up, so a failure in that last step leaves an archived event behind
        // while still reporting failure. LoadEventsToArchiveAsync skips archived events, so the retry
        // cannot pick the event up again and the CarLastLaps rows leak. If that ordering is ever
        // fixed, update this test to match the new behavior.
        // (The InMemory provider has no ExecuteDelete, which is what makes the cleanup fail here.)
        await SeedArchivableEventAsync(1);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.CarLastLaps.Add(new CarLastLap { EventId = 1, SessionId = 1, CarNumber = "5", LastLapNumber = 1, LastLapTimestamp = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var result = await CreateService().ArchiveSingleEventAsync(1, CancellationToken.None);

        Assert.IsFalse(result);
        StringAssert.Contains(failureReasons.Single(), "Failed to cleanup CarLastLaps");
        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.IsTrue((await check.Events.SingleAsync(e => e.Id == 1)).IsArchived);
        Assert.IsEmpty(await CreateService().LoadEventsToArchiveAsync());
    }

    #endregion

    #region Per session fan out

    [TestMethod]
    public async Task ArchiveEventLapsAsync_NoLaps_ReportsSuccessWithoutUploading()
    {
        var (success, exception) = await CreateService().ArchiveEventLapsAsync(1, CancellationToken.None);

        Assert.IsTrue(success);
        Assert.IsNull(exception);
        storage.Verify(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public async Task ArchiveEventLapsAsync_ArchivesEverySessionThatHasLaps()
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.CarLapLogs.Add(NewLap(1, sessionId: 1));
            db.CarLapLogs.Add(NewLap(1, sessionId: 2));
            db.CarLapLogs.Add(NewLap(2, sessionId: 1));
            await db.SaveChangesAsync();
        }

        var (success, _) = await CreateService().ArchiveEventLapsAsync(1, CancellationToken.None);

        Assert.IsTrue(success);
        storage.Verify(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), 1, 1), Times.Once);
        storage.Verify(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), 1, 2), Times.Once);
        storage.Verify(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), 2, It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public async Task ArchiveEventLapsAsync_SessionUploadFails_ReportsFailureNamingTheSession()
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.CarLapLogs.Add(NewLap(1, sessionId: 4));
            await db.SaveChangesAsync();
        }
        storage.Setup(x => x.UploadSessionLapsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(false);

        var (success, exception) = await CreateService().ArchiveEventLapsAsync(1, CancellationToken.None);

        Assert.IsFalse(success);
        Assert.IsNotNull(exception);
        StringAssert.Contains(exception.Message, "event 1, session 4");
        StringAssert.Contains(exception.Message, "Processed 0 of 1 sessions");
    }

    [TestMethod]
    public async Task ArchiveEventFlagsAsync_NoFlags_ReportsSuccessWithoutUploading()
    {
        var (success, exception) = await CreateService().ArchiveEventFlagsAsync(1, CancellationToken.None);

        Assert.IsTrue(success);
        Assert.IsNull(exception);
        storage.Verify(x => x.UploadSessionFlagsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [TestMethod]
    public async Task ArchiveEventFlagsAsync_UploadFails_ReportsFailureNamingTheSession()
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.FlagLog.Add(new FlagLog { EventId = 1, SessionId = 6, Flag = Flags.Green, StartTime = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        storage.Setup(x => x.UploadSessionFlagsAsync(It.IsAny<Stream>(), It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(false);

        var (success, exception) = await CreateService().ArchiveEventFlagsAsync(1, CancellationToken.None);

        Assert.IsFalse(success);
        Assert.IsNotNull(exception);
        StringAssert.Contains(exception.Message, "event 1, session 6");
    }

    #endregion

    private static RedMist.TimingCommon.Models.Configuration.Event NewEvent(int id, DateTime endDate,
        bool isArchived = false, bool isLive = false, bool isSimulation = false) => new()
        {
            Id = id,
            OrganizationId = 7,
            Name = $"Event {id}",
            EndDate = endDate,
            IsArchived = isArchived,
            IsLive = isLive,
            IsSimulation = isSimulation
        };

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

    private async Task SeedArchivableEventAsync(int eventId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Events.Add(NewEvent(eventId, endDate: DateTime.UtcNow.AddDays(-5)));
        for (var i = 0; i < 3; i++)
        {
            db.EventStatusLogs.Add(new EventStatusLog
            {
                EventId = eventId,
                SessionId = 1,
                Type = "t",
                Data = $"d{i}",
                Timestamp = DateTime.UtcNow.AddSeconds(i)
            });
        }
        db.CarLapLogs.Add(NewLap(eventId, sessionId: 1));
        db.CarLapLogs.Add(NewLap(eventId, sessionId: 1));
        db.FlagLog.Add(new FlagLog { EventId = eventId, SessionId = 1, Flag = Flags.Green, StartTime = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }
}
