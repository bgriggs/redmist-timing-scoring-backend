using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;
using StackExchange.Redis;
using System.Text.Json;
using static RedMist.TimingAndScoringService.Tests.EventLogger.RedisStreamTestHarness;

namespace RedMist.TimingAndScoringService.Tests.EventLogger;

/// <summary>
/// Behavior of the event-status stream consumer: consumer-group setup, field-name parsing,
/// per-type dispatch, acknowledgement, the source-logging gate and the metric window.
/// </summary>
[TestClass]
public class LogConsumerServiceTests
{
    private const int EventId = 42;
    private static readonly string StreamKey = string.Format(Consts.EVENT_STATUS_STREAM_KEY, EventId);

    private Mock<IConnectionMultiplexer> mockMux = null!;
    private Mock<IDatabase> mockCache = null!;
    private IDbContextFactory<TsContext> dbFactory = null!;
    private FakeHybridCache hybridCache = null!;
    private FakeTimeProvider clock = null!;
    private ILoggerFactory loggerFactory = null!;
    private List<(LogLevel Level, string Message, Exception? Exception)> logged = null!;
    private IConfiguration configuration = null!;

    [TestInitialize]
    public void Setup()
    {
        mockMux = new Mock<IConnectionMultiplexer>();
        mockCache = new Mock<IDatabase>();
        mockMux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockCache.Object);

        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"LogConsumerServiceTests_{Guid.NewGuid()}")
            .Options;
        dbFactory = new TestDbContextFactory(options);

        hybridCache = new FakeHybridCache();
        clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        (loggerFactory, logged) = RecordingLoggerFactory();
        configuration = ConfigForEvent(EventId);
    }

    private TestableLogConsumerService CreateService()
        => new(loggerFactory, mockMux.Object, configuration, dbFactory, hybridCache, clock);

    private async Task SeedEventAsync(bool sourceLoggingEnabled)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Events.Add(new Event
        {
            Id = EventId,
            OrganizationId = 1,
            Name = "Test Event",
            EnableSourceDataLogging = sourceLoggingEnabled,
            StartDate = new DateTime(2026, 5, 1),
            EndDate = new DateTime(2026, 5, 2),
            TrackName = "Test Track",
            CourseConfiguration = "Full",
            Distance = "2.5mi",
            EventUrl = "",
        });
        await db.SaveChangesAsync();
    }

    #region Consumer group setup

    [TestMethod]
    public async Task ExecuteAsync_StreamKeyMissing_CreatesStreamAndConsumerGroup()
    {
        mockCache.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(false);
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts);

        await RunAsync(CreateService().RunAsync, cts.Token);

        mockCache.Verify(x => x.StreamCreateConsumerGroupAsync(
            It.Is<RedisKey>(k => k == StreamKey), It.Is<RedisValue>(g => g == "log"),
            It.IsAny<RedisValue?>(), true, It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_ConsumerGroupAlreadyExists_DoesNotRecreateIt()
    {
        mockCache.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        mockCache.Setup(x => x.StreamGroupInfoAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([GroupInfo("log")]);
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts);

        await RunAsync(CreateService().RunAsync, cts.Token);

        mockCache.Verify(x => x.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [TestMethod]
    public async Task ExecuteAsync_StreamExistsWithOtherGroupsOnly_CreatesTheLogGroup()
    {
        mockCache.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        mockCache.Setup(x => x.StreamGroupInfoAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([GroupInfo("processor"), GroupInfo("archive")]);
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts);

        await RunAsync(CreateService().RunAsync, cts.Token);

        mockCache.Verify(x => x.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.Is<RedisValue>(g => g == "log"),
            It.IsAny<RedisValue?>(), true, It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_GroupInfoLookupFails_SwallowsAndStillRunsTheLoop()
    {
        mockCache.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        mockCache.Setup(x => x.StreamGroupInfoAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("NOGROUP"));
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts);

        await RunAsync(CreateService().RunAsync, cts.Token);

        Assert.IsTrue(logged.Any(m => m.Message.Contains("Error checking stream")), "Stream check failure should be logged.");
        mockCache.Verify(x => x.StreamReadGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()),
            Times.AtLeastOnce, "The loop should still start after a failed stream check.");
    }

    /// <summary>
    /// The reconnect handler is <c>async void</c>, so this asserts synchronously right after raising
    /// the event. That is deterministic here and not a race: every await inside the handler is on an
    /// already-completed task (a free SemaphoreSlim, and Moq setups that return completed tasks), so
    /// the handler runs to completion inline before Raise returns.
    /// </summary>
    [TestMethod]
    public void ConnectionRestored_ReRunsTheStreamCheck()
    {
        mockCache.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(false);
        _ = CreateService();

        // The service is not running; only the reconnect handler should trigger the group creation.
        mockMux.Raise(m => m.ConnectionRestored += null, mockMux.Object, (ConnectionFailedEventArgs)null!);

        mockCache.Verify(x => x.StreamCreateConsumerGroupAsync(It.Is<RedisKey>(k => k == StreamKey),
            It.Is<RedisValue>(g => g == "log"), It.IsAny<RedisValue?>(), true, It.IsAny<CommandFlags>()), Times.Once);
    }

    #endregion

    #region Message parsing and dispatch

    [TestMethod]
    public async Task ExecuteAsync_FieldNameWithFewerThanThreeTags_SkipsFieldButStillAcknowledges()
    {
        await SeedEventAsync(sourceLoggingEnabled: true);
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", ("rmonitor-42", "$F,payload"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.EventStatusLogs.CountAsync(), "A malformed field name must not be persisted.");
        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Warning && m.Message.Contains("Invalid event status update")));
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.Is<RedisValue>(id => id == "1-0"), It.IsAny<CommandFlags>()), Times.Once,
            "The entry must still be acknowledged so a bad field cannot wedge the stream.");
    }

    [TestMethod]
    public async Task ExecuteAsync_NonNumericSessionIdTag_FailsTheIterationAndLogsAnError()
    {
        using var cts = new CancellationTokenSource();
        SetupReadsCancelingFirst(mockCache, cts, [Entry("1-0", ("rmonitor-42-notanumber", "$F,payload"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error && m.Exception is FormatException),
            "The session id tag is parsed with int.Parse, so a non-numeric tag fails the whole iteration.");
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Never,
            "An entry that could not be parsed must not be acknowledged.");
    }

    [TestMethod]
    public async Task ExecuteAsync_SourceLoggingEnabled_PersistsRawStatusRow()
    {
        await SeedEventAsync(sourceLoggingEnabled: true);
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", ("rmonitor-42-7", "$F,14,\"00:12:34\""))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.EventStatusLogs.SingleAsync();
        Assert.AreEqual("rmonitor", row.Type);
        Assert.AreEqual(EventId, row.EventId);
        Assert.AreEqual(7, row.SessionId);
        Assert.AreEqual("$F,14,\"00:12:34\"", row.Data);
        Assert.AreEqual(clock.GetUtcNow().UtcDateTime, row.Timestamp);
    }

    [TestMethod]
    public async Task ExecuteAsync_SourceLoggingDisabled_SkipsRawStatusRowButStillAcknowledges()
    {
        await SeedEventAsync(sourceLoggingEnabled: false);
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", ("rmonitor-42-7", "$F,14"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.EventStatusLogs.CountAsync());
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_EventRowMissing_DefaultsSourceLoggingToDisabled()
    {
        // No event seeded at all: FirstOrDefaultAsync yields false rather than throwing.
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", ("rmonitor-42-7", "$F,14"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.EventStatusLogs.CountAsync());
    }

    [TestMethod]
    public async Task ExecuteAsync_MultipleBatches_ReadsTheSourceLoggingFlagFromTheDatabaseOnlyOnce()
    {
        await SeedEventAsync(sourceLoggingEnabled: true);
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts,
            [Entry("1-0", ("rmonitor-42-7", "a"))],
            [Entry("2-0", ("rmonitor-42-7", "b"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(2, await db.EventStatusLogs.CountAsync());
        Assert.AreEqual(1, hybridCache.FactoryInvocations.Count,
            "The source-logging flag is cached; it must not be re-read per batch.");
        Assert.AreEqual(string.Format("event_status_logging_{0}", EventId), hybridCache.FactoryInvocations[0]);
    }

    [TestMethod]
    public async Task ExecuteAsync_EntryWithSeveralFields_AcknowledgesOncePerEntry()
    {
        await SeedEventAsync(sourceLoggingEnabled: true);
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts,
        [
            Entry("5-0",
                ("rmonitor-42-7", "one"),
                ("flags-42-7", "two"),
                ("competitors-42-7", "three"))
        ]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(3, await db.EventStatusLogs.CountAsync(), "Every field in the entry is logged.");
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.Is<RedisValue>(id => id == "5-0"), It.IsAny<CommandFlags>()), Times.Once);
    }

    #endregion

    #region X2 passings

    [TestMethod]
    public async Task ExecuteAsync_X2Passings_PersistsOnlyEntriesWithANonZeroId()
    {
        var passings = new List<Passing>
        {
            new() { OrganizationId = 1, EventId = EventId, Id = 100, LoopId = 1, TransponderId = 555 },
            new() { OrganizationId = 1, EventId = EventId, Id = 0, LoopId = 1, TransponderId = 556 },
            new() { OrganizationId = 1, EventId = EventId, Id = 101, LoopId = 2, TransponderId = 557 },
        };
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", (Consts.X2PASS_TYPE + "-42-7", JsonSerializer.Serialize(passings)))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        var saved = await db.X2Passings.OrderBy(p => p.Id).ToListAsync();
        Assert.AreEqual(2, saved.Count, "The sentinel Id of 0 marks a passing that must not be stored.");
        CollectionAssert.AreEqual(new uint[] { 100, 101 }, saved.Select(p => p.Id).ToArray());
    }

    [TestMethod]
    public async Task ExecuteAsync_X2PassingsEmptyArray_LogsWarningAndPersistsNothing()
    {
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", (Consts.X2PASS_TYPE + "-42-7", "[]"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.X2Passings.CountAsync());
        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Warning && m.Message.Contains("No passings to process")));
    }

    [TestMethod]
    public async Task ExecuteAsync_X2PassingsMalformedJson_IsContainedAndTheEntryIsStillAcknowledged()
    {
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", (Consts.X2PASS_TYPE + "-42-7", "{not json"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error
            && m.Message.Contains("Error processing X2 passings") && m.Exception is JsonException));
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.Is<RedisValue>(id => id == "1-0"), It.IsAny<CommandFlags>()), Times.Once,
            "A payload that cannot be parsed is dropped, not retried forever.");
    }

    #endregion

    #region X2 loops

    // The happy path of SaveLoopsAsync issues ExecuteDeleteAsync, which the EF InMemory provider does
    // not implement, so the delete-then-reinsert behavior cannot be pinned down here. It needs a
    // relational provider (e.g. SQLite in-memory) to be covered.

    [TestMethod]
    public async Task ExecuteAsync_X2LoopsEmptyArray_LeavesExistingLoopsUntouched()
    {
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            seed.X2Loops.Add(new Loop { OrganizationId = 1, EventId = EventId, Id = 10, Name = "S/F" });
            await seed.SaveChangesAsync();
        }

        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", ("x2loop-42-7", "[]"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(1, await db.X2Loops.CountAsync(), "An empty update must not wipe the loop table.");
        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Warning && m.Message.Contains("No loops to process")));
    }

    [TestMethod]
    public async Task ExecuteAsync_X2LoopsMalformedJson_IsContainedAndTheEntryIsStillAcknowledged()
    {
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", ("x2loop-42-7", "{not json"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error
            && m.Message.Contains("Error processing X2 loops") && m.Exception is JsonException));
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.Is<RedisValue>(id => id == "1-0"), It.IsAny<CommandFlags>()), Times.Once);
    }

    #endregion

    #region Metrics window

    [TestMethod]
    public async Task ExecuteAsync_MetricIntervalElapsed_QueriesStreamPending()
    {
        await SeedEventAsync(sourceLoggingEnabled: false);
        using var cts = new CancellationTokenSource();
        SetupReadsAdvancingClock(mockCache, cts, clock, TimeSpan.FromSeconds(11),
            [Entry("1-0", ("rmonitor-42-7", "a"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        mockCache.Verify(x => x.StreamPendingAsync(It.Is<RedisKey>(k => k == StreamKey),
            It.Is<RedisValue>(g => g == "log"), It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_MetricIntervalNotElapsed_DoesNotQueryStreamPending()
    {
        await SeedEventAsync(sourceLoggingEnabled: false);
        using var cts = new CancellationTokenSource();
        SetupReadsAdvancingClock(mockCache, cts, clock, TimeSpan.FromSeconds(10),
            [Entry("1-0", ("rmonitor-42-7", "a"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        mockCache.Verify(x => x.StreamPendingAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
            Times.Never, "The check is a strict greater-than, so exactly the interval has not elapsed yet.");
    }

    [TestMethod]
    public void GetRate_CountsOnlySavesInsideTheOneMinuteWindow()
    {
        var service = CreateService();
        for (var i = 0; i < 5; i++)
            service.RecordLogSave();

        clock.Advance(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 3; i++)
            service.RecordLogSave();

        Assert.AreEqual(8d, service.GetRate(), "Everything is still inside the window.");

        // Push the first five past 60s old, leaving the three recorded 30s later.
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.AreEqual(3d, service.GetRate());

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.AreEqual(0d, service.GetRate());
    }

    [TestMethod]
    public void GetRate_TimestampExactlyOneMinuteOld_IsStillCounted()
    {
        var service = CreateService();
        service.RecordLogSave();

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.AreEqual(1d, service.GetRate(), "Eviction is strictly greater than the window.");

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.AreEqual(0d, service.GetRate());
    }

    #endregion

    #region Loop error handling and cancellation

    [TestMethod]
    public async Task ExecuteAsync_StreamReadFails_LogsErrorAndThrottles()
    {
        using var cts = new CancellationTokenSource();
        SetupReadThrows(mockCache, cts, new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        await RunAsync(CreateService().RunAsync, cts.Token);

        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error
                && m.Message.Contains("Error reading event status stream")
                && m.Exception is RedisConnectionException),
            "The read failure itself must be the error that is logged.");
        Assert.IsTrue(logged.Any(m => m.Message.Contains("Throttling service")), "The loop backs off after a read failure.");
    }

    [TestMethod]
    public async Task ExecuteAsync_TokenAlreadyCanceled_EnsuresTheStreamButNeverReads()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        SetupReads(mockCache, cts);

        await RunAsync(CreateService().RunAsync, cts.Token);

        mockCache.Verify(x => x.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()), Times.Once);
        mockCache.Verify(x => x.StreamReadGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()),
            Times.Never);
    }

    #endregion
}
