using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;
using static RedMist.TimingAndScoringService.Tests.EventLogger.RedisStreamTestHarness;

namespace RedMist.TimingAndScoringService.Tests.EventLogger;

/// <summary>
/// Behavior of the event-processor stream consumer: lap persistence with its last-lap bookkeeping,
/// relay heartbeat handling, tag dispatch and acknowledgement.
/// </summary>
[TestClass]
public class EventProcessLoggerTests
{
    private const int EventId = 42;
    private static readonly string StreamKey = string.Format(Consts.EVENT_PROCESSOR_LOGGING_STREAM_KEY, EventId);

    private Mock<IConnectionMultiplexer> mockMux = null!;
    private Mock<IDatabase> mockCache = null!;
    private IDbContextFactory<TsContext> dbFactory = null!;
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
            .UseInMemoryDatabase($"EventProcessLoggerTests_{Guid.NewGuid()}")
            .Options;
        dbFactory = new TestDbContextFactory(options);

        clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        (loggerFactory, logged) = RecordingLoggerFactory();
        configuration = ConfigForEvent(EventId);
    }

    private TestableEventProcessLogger CreateService()
        => new(loggerFactory, mockMux.Object, configuration, dbFactory, clock);

    private static CarLapData Lap(string carNumber, int lapNumber, int sessionId, int lastLapNum, int eventId = EventId)
        => new(new CarLapLog
        {
            EventId = eventId,
            SessionId = sessionId,
            CarNumber = carNumber,
            LapNumber = lapNumber,
            Flag = 1,
            Timestamp = new DateTime(2026, 5, 1, 11, 0, 0),
            LapData = $"{{\"lap\":{lapNumber}}}",
        }, lastLapNum, sessionId);

    private static StreamEntry LapEntry(string id, params CarLapData[] laps)
        => Entry(id, (Consts.LAP_TYPE, JsonSerializer.Serialize(laps.ToList())));

    #region Lap persistence

    [TestMethod]
    public async Task ExecuteAsync_LapPayload_PersistsLapAndCreatesLastLapReference()
    {
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [LapEntry("1-0", Lap("77", lapNumber: 12, sessionId: 3, lastLapNum: 12))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        var lap = await db.CarLapLogs.SingleAsync();
        Assert.AreEqual("77", lap.CarNumber);
        Assert.AreEqual(12, lap.LapNumber);

        var lastLap = await db.CarLastLaps.SingleAsync();
        Assert.AreEqual(EventId, lastLap.EventId, "The last-lap row is keyed to the service's configured event, not the payload.");
        Assert.AreEqual(3, lastLap.SessionId);
        Assert.AreEqual("77", lastLap.CarNumber);
        Assert.AreEqual(12, lastLap.LastLapNumber);
        Assert.AreEqual(clock.GetUtcNow().UtcDateTime, lastLap.LastLapTimestamp);
    }

    [TestMethod]
    public async Task ExecuteAsync_SecondLapForSameCar_UpdatesTheExistingLastLapRowInPlace()
    {
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            seed.CarLastLaps.Add(new CarLastLap
            {
                EventId = EventId,
                SessionId = 3,
                CarNumber = "77",
                LastLapNumber = 11,
                LastLapTimestamp = new DateTime(2026, 4, 30),
            });
            await seed.SaveChangesAsync();
        }

        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [LapEntry("1-0", Lap("77", lapNumber: 12, sessionId: 3, lastLapNum: 12))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        var lastLap = await db.CarLastLaps.SingleAsync();
        Assert.AreEqual(12, lastLap.LastLapNumber);
        Assert.AreEqual(clock.GetUtcNow().UtcDateTime, lastLap.LastLapTimestamp);
    }

    [TestMethod]
    public async Task ExecuteAsync_LapsForDifferentCarsAndSessions_TrackedIndependently()
    {
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts,
        [
            LapEntry("1-0",
                Lap("77", lapNumber: 5, sessionId: 3, lastLapNum: 5),
                Lap("88", lapNumber: 4, sessionId: 3, lastLapNum: 4),
                Lap("77", lapNumber: 2, sessionId: 9, lastLapNum: 2))
        ]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(3, await db.CarLapLogs.CountAsync());
        var lastLaps = await db.CarLastLaps.OrderBy(l => l.SessionId).ThenBy(l => l.CarNumber).ToListAsync();
        Assert.AreEqual(3, lastLaps.Count);
        Assert.AreEqual((3, "77", 5), (lastLaps[0].SessionId, lastLaps[0].CarNumber, lastLaps[0].LastLapNumber));
        Assert.AreEqual((3, "88", 4), (lastLaps[1].SessionId, lastLaps[1].CarNumber, lastLaps[1].LastLapNumber));
        Assert.AreEqual((9, "77", 2), (lastLaps[2].SessionId, lastLaps[2].CarNumber, lastLaps[2].LastLapNumber));
    }

    [TestMethod]
    public async Task ExecuteAsync_LapPayloadIsJsonNull_PersistsNothingButAcknowledges()
    {
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", (Consts.LAP_TYPE, "null"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.CarLapLogs.CountAsync());
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.Is<RedisValue>(id => id == "1-0"), It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_LapPayloadMalformed_FailsTheIterationWithoutAcknowledging()
    {
        using var cts = new CancellationTokenSource();
        SetupReadsCancelingFirst(mockCache, cts, [Entry("1-0", (Consts.LAP_TYPE, "{not json"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error && m.Exception is JsonException),
            "Deserialization failures are not handled locally; they fall through to the loop's catch.");
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [TestMethod]
    public async Task ExecuteAsync_UnrecognizedTag_IsIgnoredButStillAcknowledged()
    {
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", ("somethingelse", "payload"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.CarLapLogs.CountAsync());
        Assert.AreEqual(0, await db.CarLastLaps.CountAsync());
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.Is<RedisValue>(id => id == "1-0"), It.IsAny<CommandFlags>()), Times.Once);
    }

    #endregion

    #region Relay heartbeat

    [TestMethod]
    public async Task ExecuteAsync_RelayHeartbeat_UpdatesOrganizationConnectionState()
    {
        await SeedOrganizationAsync(7);
        var heartbeat = new RelayConnectionEventEntry
        {
            ConnectionId = "conn-1",
            EventId = EventId,
            OrganizationId = 7,
            Timestamp = new DateTime(2026, 5, 1, 11, 59, 0),
            RelayVersion = "3.1.4",
        };
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", (Consts.RELAY_HEARTBEAT_TYPE, JsonSerializer.Serialize(heartbeat)))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        var org = await db.Organizations.SingleAsync();
        Assert.AreEqual(heartbeat.Timestamp, org.RelayLastConnected);
        Assert.AreEqual("3.1.4", org.RelayVersion);
    }

    [TestMethod]
    public async Task ExecuteAsync_RelayHeartbeatForUnknownOrganization_IsIgnoredAndAcknowledged()
    {
        await SeedOrganizationAsync(7);
        var heartbeat = new RelayConnectionEventEntry { ConnectionId = "conn-1", OrganizationId = 999, RelayVersion = "9.9.9" };
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", (Consts.RELAY_HEARTBEAT_TYPE, JsonSerializer.Serialize(heartbeat)))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        var org = await db.Organizations.SingleAsync();
        Assert.AreEqual(string.Empty, org.RelayVersion, "An unmatched organization id must leave existing rows untouched.");
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.Is<RedisValue>(id => id == "1-0"), It.IsAny<CommandFlags>()), Times.Once);
    }

    private async Task SeedOrganizationAsync(int orgId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            ClientId = $"client-{orgId}",
            Name = $"Org {orgId}",
            ShortName = "ORG",
        });
        await db.SaveChangesAsync();
    }

    #endregion

    #region Stream setup, metrics and failures

    [TestMethod]
    public async Task ExecuteAsync_StreamKeyMissing_CreatesStreamAndConsumerGroup()
    {
        mockCache.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(false);
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts);

        await RunAsync(CreateService().RunAsync, cts.Token);

        mockCache.Verify(x => x.StreamCreateConsumerGroupAsync(It.Is<RedisKey>(k => k == StreamKey),
            It.Is<RedisValue>(g => g == "log"), It.IsAny<RedisValue?>(), true, It.IsAny<CommandFlags>()), Times.Once);
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

        mockMux.Raise(m => m.ConnectionRestored += null, mockMux.Object, (ConnectionFailedEventArgs)null!);

        mockCache.Verify(x => x.StreamCreateConsumerGroupAsync(It.Is<RedisKey>(k => k == StreamKey),
            It.Is<RedisValue>(g => g == "log"), It.IsAny<RedisValue?>(), true, It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_MetricIntervalElapsed_QueriesStreamPending()
    {
        using var cts = new CancellationTokenSource();
        SetupReadsAdvancingClock(mockCache, cts, clock, TimeSpan.FromSeconds(11),
            [Entry("1-0", ("somethingelse", "payload"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        mockCache.Verify(x => x.StreamPendingAsync(It.Is<RedisKey>(k => k == StreamKey),
            It.Is<RedisValue>(g => g == "log"), It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_MetricIntervalNotElapsed_DoesNotQueryStreamPending()
    {
        using var cts = new CancellationTokenSource();
        SetupReadsAdvancingClock(mockCache, cts, clock, TimeSpan.FromSeconds(10),
            [Entry("1-0", ("somethingelse", "payload"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        mockCache.Verify(x => x.StreamPendingAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
            Times.Never, "Exactly the interval is not yet past it.");
    }

    [TestMethod]
    public void GetRate_CountsOnlySavesInsideTheOneMinuteWindow()
    {
        var service = CreateService();
        service.RecordLogSave();
        service.RecordLogSave();

        clock.Advance(TimeSpan.FromSeconds(45));
        service.RecordLogSave();
        Assert.AreEqual(3d, service.GetRate());

        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.AreEqual(1d, service.GetRate(), "The two oldest saves are now more than a minute old.");
    }

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
        Assert.IsTrue(logged.Any(m => m.Message.Contains("Throttling service")));
    }

    [TestMethod]
    public async Task ExecuteAsync_TokenAlreadyCanceled_EnsuresTheStreamButNeverReads()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        SetupReads(mockCache, cts);

        await RunAsync(CreateService().RunAsync, cts.Token);

        mockCache.Verify(x => x.StreamReadGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()),
            Times.Never);
    }

    #endregion
}
