using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using StackExchange.Redis;
using static RedMist.TimingAndScoringService.Tests.EventLogger.RedisStreamTestHarness;

namespace RedMist.TimingAndScoringService.Tests.EventLogger;

/// <summary>
/// Behavior of the external-source message consumer: field-name parsing, verbatim persistence,
/// the type-column truncation and acknowledgement.
/// </summary>
[TestClass]
public class ExternalMessageLogConsumerTests
{
    private const int EventId = 42;
    private static readonly string StreamKey = string.Format(Consts.EVENT_EXTERNAL_LOG_STREAM_KEY, EventId);

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
            .UseInMemoryDatabase($"ExternalMessageLogConsumerTests_{Guid.NewGuid()}")
            .Options;
        dbFactory = new TestDbContextFactory(options);

        clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        (loggerFactory, logged) = RecordingLoggerFactory();
        configuration = ConfigForEvent(EventId);
    }

    private TestableExternalMessageLogConsumer CreateService()
        => new(loggerFactory, mockMux.Object, configuration, dbFactory, clock);

    #region Persistence

    [TestMethod]
    public async Task ExecuteAsync_ValidMessage_PersistsPayloadVerbatim()
    {
        const string payload = "{\"a\":1}\n{\"a\":2}\n{\"a\":3}";
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", ("sentinel-42-9", payload))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ExternalMessageLogs.SingleAsync();
        Assert.AreEqual("sentinel", row.Type);
        Assert.AreEqual(EventId, row.EventId, "The event id comes from configuration, not the field name.");
        Assert.AreEqual(9, row.SessionId);
        Assert.AreEqual(payload, row.Data, "The payload is opaque and must be stored byte for byte.");
        Assert.AreEqual(clock.GetUtcNow().UtcDateTime, row.Timestamp);
    }

    [TestMethod]
    public async Task ExecuteAsync_TypeLongerThanTheColumn_IsTruncatedToTwentyCharacters()
    {
        var longType = new string('t', 25);
        var exactType = new string('e', 20);
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts,
        [
            Entry("1-0",
                ($"{longType}-42-9", "over"),
                ($"{exactType}-42-9", "exact"))
        ]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.ExternalMessageLogs.OrderBy(r => r.Id).ToListAsync();
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(new string('t', 20), rows[0].Type);
        Assert.AreEqual(exactType, rows[1].Type, "A type of exactly the column width must not be trimmed.");
    }

    [TestMethod]
    public async Task ExecuteAsync_FieldNameWithFewerThanThreeTags_SkipsFieldButStillAcknowledges()
    {
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", ("sentinel-42", "payload"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.ExternalMessageLogs.CountAsync());
        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Warning && m.Message.Contains("Invalid external message field")));
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.Is<RedisValue>(id => id == "1-0"), It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_MixedValidAndMalformedFields_PersistsTheValidOnes()
    {
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts,
        [
            Entry("1-0",
                ("bad", "skipped"),
                ("sentinel-42-9", "kept"),
                ("alsobad-42", "skipped too"))
        ]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ExternalMessageLogs.SingleAsync();
        Assert.AreEqual("kept", row.Data);
    }

    /// <summary>
    /// BUG (pinned, not fixed): the two ways a field name can be malformed are handled completely
    /// differently. A name with too few tags is skipped and the entry is still acknowledged (see the
    /// mixed-fields test above); a name with the right shape but a non-numeric session id reaches
    /// <c>int.Parse</c>, which throws out of the whole iteration. The entry is never acknowledged, so it
    /// is redelivered on every pass for as long as the consumer runs - one poison field wedges the stream
    /// permanently.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_NonNumericSessionIdTag_FailsTheIterationWithoutAcknowledging()
    {
        using var cts = new CancellationTokenSource();
        SetupReadsCancelingFirst(mockCache, cts, [Entry("1-0", ("sentinel-42-abc", "payload"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error && m.Exception is FormatException),
            "The session id tag is parsed with int.Parse, so a non-numeric tag fails the whole iteration.");
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [TestMethod]
    public async Task ExecuteAsync_MultipleEntries_AcknowledgesEachOne()
    {
        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts,
            [Entry("1-0", ("sentinel-42-9", "one"))],
            [Entry("2-0", ("sentinel-42-9", "two"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual(2, await db.ExternalMessageLogs.CountAsync());
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.Is<RedisValue>(id => id == "1-0"), It.IsAny<CommandFlags>()), Times.Once);
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.Is<RedisValue>(id => id == "2-0"), It.IsAny<CommandFlags>()), Times.Once);
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
            [Entry("1-0", ("sentinel-42-9", "payload"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        mockCache.Verify(x => x.StreamPendingAsync(It.Is<RedisKey>(k => k == StreamKey),
            It.Is<RedisValue>(g => g == "log"), It.IsAny<CommandFlags>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_MetricIntervalNotElapsed_DoesNotQueryStreamPending()
    {
        using var cts = new CancellationTokenSource();
        SetupReadsAdvancingClock(mockCache, cts, clock, TimeSpan.FromSeconds(10),
            [Entry("1-0", ("sentinel-42-9", "payload"))]);

        await RunAsync(CreateService().RunAsync, cts.Token);

        mockCache.Verify(x => x.StreamPendingAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
            Times.Never, "The check is a strict greater-than, so exactly the interval has not elapsed yet.");
    }

    [TestMethod]
    public async Task ExecuteAsync_DbContextCreationFails_IsContainedAndTheEntryIsStillAcknowledged()
    {
        // SaveMessageAsync opens its context inside the try, so a factory failure lands in the same
        // catch that a failed write would.
        var failingFactory = new Mock<IDbContextFactory<TsContext>>();
        failingFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no database"));

        using var cts = new CancellationTokenSource();
        SetupReads(mockCache, cts, [Entry("1-0", ("sentinel-42-9", "payload"))]);

        var service = new TestableExternalMessageLogConsumer(loggerFactory, mockMux.Object, configuration,
            failingFactory.Object, clock);
        await RunAsync(service.RunAsync, cts.Token);

        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error
            && m.Message.Contains("Error saving external message") && m.Exception is InvalidOperationException));
        mockCache.Verify(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.Is<RedisValue>(id => id == "1-0"), It.IsAny<CommandFlags>()), Times.Once,
            "A persistence failure is swallowed, so the message is acknowledged and lost rather than retried.");
    }

    [TestMethod]
    public async Task ExecuteAsync_StreamReadFails_LogsErrorAndThrottles()
    {
        using var cts = new CancellationTokenSource();
        SetupReadThrows(mockCache, cts, new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        await RunAsync(CreateService().RunAsync, cts.Token);

        Assert.IsTrue(logged.Any(m => m.Level == LogLevel.Error
                && m.Message.Contains("Error reading external message stream")
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

        mockCache.Verify(x => x.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()), Times.Once,
            "The stream is ensured before the loop looks at its token, so a pod stopped at start still leaves it usable.");
        mockCache.Verify(x => x.StreamReadGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
            It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()),
            Times.Never);
    }

    #endregion
}
