using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.PenaltyEnricher;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.EventProcessor.Tests.EventStatus.PenaltyEnricher;

/// <summary>
/// Penalties reach the enricher through a background refresh from Redis; the control log processor
/// is a separate service, so this poll is the only route by which a black flag or lap penalty ever
/// reaches a car. ControlLogEnricherTests covers applying the lookup - these cover filling it.
/// </summary>
[TestClass]
public class ControlLogEnricherRefreshTests
{
    private const int EventId = 123;

    private Mock<IDatabase> _database = null!;
    private SessionContext _sessionContext = null!;
    private ControlLogEnricher _enricher = null!;

    [TestInitialize]
    public void Setup()
    {
        _database = new Mock<IDatabase>();
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_database.Object);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", EventId.ToString() } })
            .Build();
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        var loggerFactory = new DebugLoggerFactory();

        _sessionContext = new SessionContext(configuration, new TestDbContextFactory(optionsBuilder.Options), loggerFactory,
            new Mock<ICarLapHistoryService>().Object);
        _enricher = new ControlLogEnricher(loggerFactory, mux.Object, configuration, _sessionContext);
    }

    /// <summary>
    /// Runs exactly one refresh pass: the poll is stopped as the read is served, so the pass
    /// completes and the loop then exits on the cancellation rather than on a timer.
    /// </summary>
    private async Task RunOneRefreshAsync(Func<HashEntry[]> read)
    {
        // Not disposed: the service keeps a token source linked to this one for as long as it lives,
        // and a test may run a second pass over the same enricher.
        var cts = new CancellationTokenSource();
        RedisKey requestedKey = default;
        _database.Setup(x => x.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, CommandFlags>((key, _) => { requestedKey = key; cts.Cancel(); })
            .Returns(() => Task.FromResult(read()));

        await _enricher.StartAsync(cts.Token);
        await _enricher.ExecuteTask!;

        Assert.AreEqual(string.Format(Consts.CONTROL_LOG_CAR_PENALTIES, EventId), requestedKey.ToString(),
            "Penalties must be read from this event's own hash.");
    }

    private static HashEntry Entry(string carNumber, CarPenalty penalty)
        => new(carNumber, JsonSerializer.Serialize(penalty));

    [TestMethod]
    [Timeout(30_000)]
    public async Task Refresh_LoadsPenaltiesFromRedisSoTheSweepCanApplyThem()
    {
        _sessionContext.UpdateCars([new CarPosition { Number = "1" }, new CarPosition { Number = "2" }]);

        await RunOneRefreshAsync(() => [Entry("1", new CarPenalty(2, 1, 3))]);

        var patches = _enricher.Process();

        Assert.HasCount(1, patches);
        Assert.AreEqual("1", patches[0].Number);
        Assert.AreEqual(2, patches[0].PenalityWarnings);
        Assert.AreEqual(1, patches[0].PenalityLaps);
        Assert.AreEqual(3, patches[0].BlackFlags);
    }

    /// <summary>
    /// The lookup is replaced wholesale rather than merged, so a car whose penalties were rescinded
    /// disappears from the hash and has to be cleared rather than keeping its last known penalty.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Refresh_ReplacesThePreviousLookupRatherThanMergingIntoIt()
    {
        _sessionContext.UpdateCars([new CarPosition { Number = "1" }]);
        await RunOneRefreshAsync(() => [Entry("1", new CarPenalty(2, 0, 0))]);
        _enricher.Process();
        Assert.AreEqual(2, _sessionContext.GetCarByNumber("1")!.PenalityWarnings);

        await RunOneRefreshAsync(() => []);
        var patches = _enricher.Process();

        Assert.HasCount(1, patches);
        Assert.AreEqual(0, patches[0].PenalityWarnings);
        Assert.AreEqual(0, _sessionContext.GetCarByNumber("1")!.PenalityWarnings);
    }

    /// <summary>
    /// Entries that carry no penalty - a null value, or a value stored without a car number - are
    /// dropped rather than admitted to the lookup, where a null key or a null penalty would take
    /// out the sweep that applies them to the field.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Refresh_SkipsEntriesWithNoPenaltyOrNoCarNumber()
    {
        _sessionContext.UpdateCars([new CarPosition { Number = "1" }, new CarPosition { Number = "2" }]);

        await RunOneRefreshAsync(() =>
        [
            new HashEntry("1", "null"),
            new HashEntry(RedisValue.Null, JsonSerializer.Serialize(new CarPenalty(9, 9, 9))),
            Entry("2", new CarPenalty(0, 0, 1)),
        ]);

        var patches = _enricher.Process();

        Assert.HasCount(1, patches);
        Assert.AreEqual("2", patches[0].Number);
        Assert.AreEqual(1, patches[0].BlackFlags);
    }

    /// <summary>
    /// A Redis outage must leave the poll running; it recovers on its own once Redis is back.
    ///
    /// BUG (pinned, not fixed): the retry is completely unthrottled. The one-second delay sits at the end
    /// of the try block, after the read, so a read that throws skips it and the catch goes straight back
    /// round the loop. During a Redis outage this spins as fast as the connection can fail, hammering the
    /// log with an error per iteration - every sibling service throttles before retrying. That is why this
    /// test needs no fake clock to reach its second attempt: the second attempt is immediate.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Refresh_WhenRedisIsUnreachable_DoesNotStopThePoll()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        _database.Setup(x => x.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Callback(() =>
            {
                if (++attempts >= 2)
                    cts.Cancel();
            })
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        await _enricher.StartAsync(cts.Token);
        await _enricher.ExecuteTask!;

        Assert.AreEqual(2, attempts, "The poll must keep retrying after a Redis fault.");
    }
}
