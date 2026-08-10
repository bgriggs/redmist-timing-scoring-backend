using BigMission.TestHelpers.Testing;
using Microsoft.Extensions.Configuration;
using Moq;
using RedMist.Backend.Shared;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Net;

namespace RedMist.EventProcessor.Tests.EventStatus.LapData;

/// <summary>
/// Retiring the lap history is what stops one session's lap times being restored onto the next
/// session's cars, and a car's stored laps are the only record of its last lap time across a
/// restart. Both directions of getting that wrong are silent, so the clearing path and the
/// failure paths are covered here; the happy add/get paths live in CarLapHistoryServiceTests.
/// </summary>
[TestClass]
public class CarLapHistoryServiceClearTests
{
    private const int EventId = 7;

    private Mock<IConnectionMultiplexer> _mux = null!;
    private Mock<IDatabase> _database = null!;
    private Mock<IServer> _server = null!;
    private CarLapHistoryService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _mux = new Mock<IConnectionMultiplexer>();
        _database = new Mock<IDatabase>();
        _server = new Mock<IServer>();

        _mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_database.Object);
        _mux.Setup(x => x.GetEndPoints(It.IsAny<bool>())).Returns([new DnsEndPoint("localhost", 6379)]);
        _mux.Setup(x => x.GetServer(It.IsAny<EndPoint>(), It.IsAny<object>())).Returns(_server.Object);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", EventId.ToString() } })
            .Build();

        _service = new CarLapHistoryService(new DebugLoggerFactory(), _mux.Object, configuration);
    }

    /// <summary>
    /// The scan has to be confined to this event: the same Redis instance carries every other
    /// event running at the time, and deleting their history would cost each of those cars its
    /// last lap time.
    /// </summary>
    [TestMethod]
    public async Task ClearLapsAsync_DeletesEveryKeyMatchingThisEventsPattern()
    {
        RedisValue scannedPattern = RedisValue.Null;
        RedisKey[] matched =
        [
            string.Format(Consts.CAR_LAP_HISTORY, EventId, "1"),
            string.Format(Consts.CAR_LAP_HISTORY, EventId, "42"),
        ];
        _server.Setup(x => x.Keys(It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>()))
            .Callback<int, RedisValue, int, long, int, CommandFlags>((_, pattern, _, _, _, _) => scannedPattern = pattern)
            .Returns(matched);

        RedisKey[]? deleted = null;
        _database.Setup(x => x.KeyDeleteAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey[], CommandFlags>((keys, _) => deleted = keys)
            .ReturnsAsync(matched.Length);

        await _service.ClearLapsAsync();

        Assert.AreEqual(string.Format(Consts.CAR_LAP_HISTORY, EventId, "*"), scannedPattern.ToString());
        CollectionAssert.AreEqual(matched, deleted);
    }

    /// <summary>
    /// Deleting nothing is not the same as deleting an empty set - passing an empty key array to
    /// Redis is a request that does not need making.
    /// </summary>
    [TestMethod]
    public async Task ClearLapsAsync_WithNoMatchingKeys_DoesNotIssueADelete()
    {
        _server.Setup(x => x.Keys(It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>()))
            .Returns([]);

        await _service.ClearLapsAsync();

        _database.Verify(x => x.KeyDeleteAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    /// <summary>
    /// A clear that silently failed would leave the outgoing session's lap times to be restored
    /// onto the new session's cars, so the caller has to see the failure.
    /// </summary>
    [TestMethod]
    public async Task ClearLapsAsync_WhenRedisFails_Throws()
    {
        _server.Setup(x => x.Keys(It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>()))
            .Throws(new RedisTimeoutException("scan timed out", CommandStatus.WaitingInBacklog));

        await Assert.ThrowsExactlyAsync<RedisTimeoutException>(() => _service.ClearLapsAsync());
    }

    [TestMethod]
    public async Task AddLapAsync_WhenRedisFails_Throws()
    {
        _database.Setup(x => x.ListLeftPushAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        await Assert.ThrowsExactlyAsync<RedisConnectionException>(
            () => _service.AddLapAsync(new CarPosition { Number = "1", LastLapCompleted = 3 }));
    }

    /// <summary>
    /// The projected-lap-time and fastest-pace enrichers read through this. Returning an empty
    /// list on a Redis fault would be indistinguishable from a car that has completed no laps and
    /// would quietly wipe its projection, so the fault is surfaced instead.
    /// </summary>
    [TestMethod]
    public async Task GetLapsAsync_WhenRedisFails_Throws()
    {
        _database.Setup(x => x.ListRangeAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        await Assert.ThrowsExactlyAsync<RedisConnectionException>(() => _service.GetLapsAsync("1"));
    }

    /// <summary>
    /// A blank slot in the rolling window is a gap, not a lap; it must be dropped rather than
    /// counted, or a car's projected lap time would be averaged against nothing.
    /// </summary>
    [TestMethod]
    public async Task GetLapsAsync_SkipsEmptyEntries()
    {
        RedisValue[] stored =
        [
            System.Text.Json.JsonSerializer.Serialize(new CarPosition { Number = "1", LastLapCompleted = 4 }),
            RedisValue.EmptyString,
            System.Text.Json.JsonSerializer.Serialize(new CarPosition { Number = "1", LastLapCompleted = 3 }),
        ];
        _database.Setup(x => x.ListRangeAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(stored);

        var laps = await _service.GetLapsAsync("1");

        Assert.HasCount(2, laps);
        CollectionAssert.AreEqual(new[] { 4, 3 }, laps.Select(l => l.LastLapCompleted).ToArray());
    }
}
