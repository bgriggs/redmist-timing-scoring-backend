using BigMission.TestHelpers.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.X2;
using StackExchange.Redis;
using System.Security.Claims;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// The relay message counters the site operations page reads: what is written, where, and which
/// relay messages count as how many.
/// </summary>
[TestClass]
public class RelayMessageCounterTests
{
    private const int EventId = 42;

    /// <summary>12:34:56.789 UTC, inside Unix minute 29_307_154 (12:34:00).</summary>
    private static readonly DateTime Now = new(2025, 9, 20, 12, 34, 56, 789, DateTimeKind.Utc);

    private FakeRedisDatabase redis = null!;

    [TestInitialize]
    public void Setup() => redis = new FakeRedisDatabase();

    private static string CountsKey(int eventId) => string.Format(Consts.RELAY_MESSAGE_COUNTS, eventId);

    private static string MinuteKey(int eventId, DateTime at) =>
        string.Format(Consts.RELAY_MESSAGE_MINUTE, eventId, RelayMessageCounter.UnixMinute(at));

    [TestMethod]
    public void UnixMinute_IsWholeMinutesSinceTheEpoch()
    {
        var expected = new DateTimeOffset(Now).ToUnixTimeSeconds() / 60;

        Assert.AreEqual(expected, RelayMessageCounter.UnixMinute(Now));
        Assert.AreEqual(new DateTime(2025, 9, 20, 12, 34, 0, DateTimeKind.Utc),
            RelayMessageCounter.MinuteStartUtc(RelayMessageCounter.UnixMinute(Now)));
    }

    /// <summary>
    /// The five writes the page is built on: total, last-received time, the totals' backstop expiry,
    /// the minute's count, and the minute's expiry - all in one batch, all fire-and-forget so the relay's path never waits.
    /// </summary>
    [TestMethod]
    public void Record_WritesTheTotalTheLastTimeAndTheMinuteInOneFireAndForgetBatch()
    {
        RelayMessageCounter.Record(redis.Db.Object, EventId, RelayMessageTypes.RMonitor, 1, Now, NullLogger.Instance);

        Assert.AreEqual(1, redis.BatchesExecuted);
        Assert.AreEqual("1", redis.GetHashValue(CountsKey(EventId), RelayMessageTypes.RMonitor));
        Assert.AreEqual(new DateTimeOffset(Now).ToUnixTimeMilliseconds().ToString(),
            redis.GetHashValue(CountsKey(EventId), RelayMessageTypes.RMonitor + RelayMessageCounter.LastSuffix));
        Assert.AreEqual("1", redis.GetHashValue(MinuteKey(EventId, Now), RelayMessageTypes.RMonitor));
        CollectionAssert.AreEquivalent(new[]
        {
            (MinuteKey(EventId, Now), (TimeSpan?)TimeSpan.FromHours(2)),
            (CountsKey(EventId), (TimeSpan?)TimeSpan.FromDays(2)),
        }, redis.KeyExpires);

        redis.Db.Verify(x => x.HashIncrementAsync(CountsKey(EventId), RelayMessageTypes.RMonitor, 1, CommandFlags.FireAndForget), Times.Once());
        redis.Db.Verify(x => x.HashIncrementAsync(MinuteKey(EventId, Now), RelayMessageTypes.RMonitor, 1, CommandFlags.FireAndForget), Times.Once());
        redis.Db.Verify(x => x.HashSetAsync(CountsKey(EventId), RelayMessageTypes.RMonitor + RelayMessageCounter.LastSuffix,
            It.IsAny<RedisValue>(), When.Always, CommandFlags.FireAndForget), Times.Once());
        redis.Db.Verify(x => x.KeyExpireAsync(MinuteKey(EventId, Now), It.IsAny<TimeSpan?>(), CommandFlags.FireAndForget), Times.Once());
        redis.Db.Verify(x => x.KeyExpireAsync(CountsKey(EventId), It.IsAny<TimeSpan?>(), CommandFlags.FireAndForget), Times.Once());
    }

    [TestMethod]
    public void Record_AccumulatesAndStartsANewHashEachMinute()
    {
        RelayMessageCounter.Record(redis.Db.Object, EventId, RelayMessageTypes.Passings, 3, Now, NullLogger.Instance);
        RelayMessageCounter.Record(redis.Db.Object, EventId, RelayMessageTypes.Passings, 2, Now.AddSeconds(1), NullLogger.Instance);
        RelayMessageCounter.Record(redis.Db.Object, EventId, RelayMessageTypes.Passings, 4, Now.AddMinutes(1), NullLogger.Instance);

        Assert.AreEqual("9", redis.GetHashValue(CountsKey(EventId), RelayMessageTypes.Passings));
        Assert.AreEqual("5", redis.GetHashValue(MinuteKey(EventId, Now), RelayMessageTypes.Passings));
        Assert.AreEqual("4", redis.GetHashValue(MinuteKey(EventId, Now.AddMinutes(1)), RelayMessageTypes.Passings));
    }

    /// <summary>
    /// Event zero is a relay with no event picked. Counting it would build one shared hash for every
    /// unconfigured relay on the site that nothing ever tears down.
    /// </summary>
    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(-1, 1)]
    [DataRow(EventId, 0)]
    [DataRow(EventId, -3)]
    public void Record_NoEventOrNothingToCount_WritesNothing(int eventId, long count)
    {
        RelayMessageCounter.Record(redis.Db.Object, eventId, RelayMessageTypes.RMonitor, count, Now, NullLogger.Instance);

        Assert.AreEqual(0, redis.BatchesExecuted);
        Assert.IsEmpty(redis.HashIncrements);
        Assert.IsEmpty(redis.HashWrites);
        Assert.IsEmpty(redis.KeyExpires);
    }

    /// <summary>A counter that cannot be written must never cost the relay its message.</summary>
    [TestMethod]
    public void Record_ARedisFailure_DoesNotThrow()
    {
        redis.Db.Setup(x => x.CreateBatch(It.IsAny<object>()))
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        RelayMessageCounter.Record(redis.Db.Object, EventId, RelayMessageTypes.RMonitor, 1, Now, NullLogger.Instance);
    }

    #region Through the hub

    private const string ClientId = "relay-client";
    private static int nextEventId = 600_000;
    private static int NewEventId() => Interlocked.Increment(ref nextEventId);

    private async Task<RelayHub> CreateHubAsync()
    {
        var dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"RelayMessageCounterTests_{Guid.NewGuid()}")
            .Options);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Organizations.Add(new Organization { ClientId = ClientId, Name = "Test Org", ShortName = "TO" });
            await db.SaveChangesAsync();
        }

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns($"conn-{Guid.NewGuid()}");
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity([new Claim("azp", ClientId)], "test")));
        return new RelayHub(new DebugLoggerFactory(), redis.Mux.Object, dbFactory, new FakeHybridCache())
        {
            Context = context.Object,
            Groups = new Mock<IGroupManager>().Object,
        };
    }

    private long Count(int eventId, string type) =>
        long.TryParse(redis.GetHashValue(CountsKey(eventId), type), out var n) ? n : 0;

    /// <summary>
    /// Every passing counts, not every call: a call is anything from one car crossing the line to a
    /// backlog after a reconnect, and the hub splits it into chunks of 25 besides.
    /// </summary>
    [TestMethod]
    public async Task SendPassings_CountsEachPassingRatherThanEachCallOrChunk()
    {
        var hub = await CreateHubAsync();
        var eventId = NewEventId();

        await hub.SendPassings(eventId, 1, [.. Enumerable.Range(1, 60).Select(i => new Passing { Id = (uint)i })]);
        await hub.SendPassings(eventId, 1, [new Passing { Id = 61 }]);

        Assert.AreEqual(61, Count(eventId, RelayMessageTypes.Passings));
    }

    [TestMethod]
    public async Task SendPassings_AnEmptyCall_IsNotCounted()
    {
        var hub = await CreateHubAsync();
        var eventId = NewEventId();

        await hub.SendPassings(eventId, 1, []);

        Assert.IsNull(redis.GetHashValue(CountsKey(eventId), RelayMessageTypes.Passings));
    }

    /// <summary>SendHeartbeatV2 goes through SendHeartbeat, and must not count twice for doing so.</summary>
    [TestMethod]
    public async Task Heartbeats_CountOncePerCall_IncludingV2()
    {
        var hub = await CreateHubAsync();
        var eventId = NewEventId();

        await hub.SendHeartbeat(eventId, "3.1.4");
        await hub.SendHeartbeatV2(eventId, "3.1.4");

        Assert.AreEqual(2, Count(eventId, RelayMessageTypes.Heartbeat));
    }

    [TestMethod]
    public async Task EachFeed_CountsUnderItsOwnType()
    {
        var hub = await CreateHubAsync();
        var eventId = NewEventId();

        await hub.SendRMonitor(eventId, 1, "$F,14");
        await hub.SendRMonitor(eventId, 1, "$F,15");
        await hub.SendMultiloop(eventId, 1, "$H");
        await hub.SendFlagtronics(eventId, 1, "[{\"car\":1},{\"car\":2}]");
        await hub.SendLoopChange(eventId, [new Loop { Id = 1 }, new Loop { Id = 2 }]);
        await hub.SendFlags(eventId, 1, [new FlagDuration { Flag = Flags.Green }]);

        Assert.AreEqual(2, Count(eventId, RelayMessageTypes.RMonitor));
        Assert.AreEqual(1, Count(eventId, RelayMessageTypes.Multiloop));
        Assert.AreEqual(1, Count(eventId, RelayMessageTypes.Flagtronics), "One feed read, however many cars it carries.");
        Assert.AreEqual(1, Count(eventId, RelayMessageTypes.Loops));
        Assert.AreEqual(1, Count(eventId, RelayMessageTypes.Flags));
    }

    [TestMethod]
    public async Task ATimingFeedWithNoEventSelected_IsNotCounted()
    {
        var hub = await CreateHubAsync();

        await hub.SendRMonitor(0, 1, "$F,14");

        Assert.IsEmpty(redis.HashIncrements);
    }

    /// <summary>
    /// A session change for an event the relay does not own is refused, and a refused message is not
    /// counted - otherwise a relay pointed at the wrong id would raise somebody else's numbers.
    /// </summary>
    [TestMethod]
    public async Task ARefusedSessionChange_IsNotCounted()
    {
        var hub = await CreateHubAsync();
        var eventId = NewEventId();

        await hub.SendSessionChange(eventId, 1, "Race", 0);

        Assert.AreEqual(0, Count(eventId, RelayMessageTypes.Session));
    }

    #endregion
}
