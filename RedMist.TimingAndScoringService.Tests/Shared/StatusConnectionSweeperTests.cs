using BigMission.TestHelpers.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Services;
using System.Security.Claims;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// Covers the cleanup of connection records left behind by a status API process that was killed
/// before its disconnect handlers could run.
/// </summary>
/// <remarks>
/// A status pod crash-looping on OOM kills during an event left every connection it served in the
/// cache, and the clients reconnecting to a healthy pod were counted again. The live viewer count
/// climbed to several hundred against a few dozen real connections.
/// </remarks>
[TestClass]
public sealed class StatusConnectionSweeperTests
{
    private const int EventId = 521;
    private const string Me = "status-api-a-111111111111";
    private const string Peer = "status-api-b-222222222222";

    private FakeRedisDatabase redis = null!;
    private FakeTimeProvider clock = null!;
    private StatusConnectionSweeper sweeper = null!;
    private StatusHubInstance me = null!;

    [TestInitialize]
    public void Setup()
    {
        redis = new FakeRedisDatabase();
        clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 22, 0, 0, TimeSpan.Zero));
        me = new StatusHubInstance(Me);
        sweeper = new StatusConnectionSweeper(new DebugLoggerFactory(), redis.Mux.Object, me, clock);
    }

    private static string EventKey(int eventId = EventId) => string.Format(Consts.STATUS_EVENT_CONNECTIONS, eventId);

    private void SeedConnection(string connectionId, string? owner, int eventId = EventId, DateTime? connectedAt = null)
    {
        var conn = new StatusConnection
        {
            ConnectedTimestamp = connectedAt ?? clock.GetUtcNow().UtcDateTime,
            ClientId = "redmist-ios-ui",
            SubscribedEventId = eventId,
            InstanceId = owner,
        };
        redis.SeedHash(Consts.STATUS_CONNECTIONS, connectionId, JsonSerializer.Serialize(conn));
        if (eventId > 0)
        {
            redis.SeedHash(EventKey(eventId), connectionId, "iOS");
        }
    }

    private void PeerIsAlive() => redis.SeedString(StatusConnectionSweeper.HeartbeatKey(Peer), "beat");

    private bool IsListed(string connectionId, int eventId = EventId)
        => redis.GetHashValue(Consts.STATUS_CONNECTIONS, connectionId) != null
            || redis.GetHashValue(EventKey(eventId), connectionId) != null;

    #region Owned records

    [TestMethod]
    public async Task ALivePeersConnections_AreKept()
    {
        PeerIsAlive();
        SeedConnection("c1", Peer);

        await sweeper.SweepAsync();
        await sweeper.SweepAsync();

        Assert.IsTrue(IsListed("c1"));
    }

    [TestMethod]
    public async Task ConnectionsThisProcessIsServing_AreKept()
    {
        me.Connected("c1");
        SeedConnection("c1", Me);

        await sweeper.SweepAsync();
        await sweeper.SweepAsync();

        Assert.IsTrue(IsListed("c1"), "This process is plainly running and serving it; no heartbeat check is needed.");
    }

    /// <summary>
    /// A disconnect during a Redis blip deletes nothing, and a subscribe still in flight when its
    /// connection dropped writes the record back. The owner is alive, so the heartbeat alone would
    /// keep these counted for as long as the pod runs.
    /// </summary>
    [TestMethod]
    public async Task ThisProcessesOwnLeakedRecords_AreRemovedOnTheSecondSweep()
    {
        SeedConnection("leaked", Me);

        Assert.AreEqual(0, await sweeper.SweepAsync(), "One sweep could be a connect still writing its record.");
        Assert.AreEqual(1, await sweeper.SweepAsync());
        Assert.IsFalse(IsListed("leaked"));
    }

    [TestMethod]
    public async Task AConnectionThatStartsBeingServedBetweenSweeps_IsKept()
    {
        SeedConnection("c1", Me);

        await sweeper.SweepAsync();
        me.Connected("c1");
        await sweeper.SweepAsync();

        Assert.IsTrue(IsListed("c1"));
    }

    [TestMethod]
    public async Task OrphansAcrossEvents_AreRemovedWithOneDeletePerHash()
    {
        SeedConnection("a", Peer, eventId: 1);
        SeedConnection("b", Peer, eventId: 1);
        SeedConnection("c", Peer, eventId: 2);

        await sweeper.SweepAsync();
        Assert.AreEqual(3, await sweeper.SweepAsync());

        Assert.IsFalse(IsListed("a", 1) || IsListed("b", 1) || IsListed("c", 2));
        redis.Db.Verify(x => x.HashDeleteAsync(It.IsAny<StackExchange.Redis.RedisKey>(), It.IsAny<StackExchange.Redis.RedisValue>(),
            It.IsAny<StackExchange.Redis.CommandFlags>()), Times.Never, "A backlog deleted one field at a time takes a round trip per field.");
    }

    [TestMethod]
    public async Task ADeadPeersConnections_AreRemovedFromBothHashes()
    {
        SeedConnection("c1", Peer);
        SeedConnection("c2", Peer);

        await sweeper.SweepAsync();
        var removed = await sweeper.SweepAsync();

        Assert.AreEqual(2, removed);
        Assert.IsNull(redis.GetHashValue(EventKey(), "c1"), "Left in the event hash, it is still counted as a viewer.");
        Assert.IsNull(redis.GetHashValue(Consts.STATUS_CONNECTIONS, "c1"));
        Assert.IsFalse(IsListed("c2"));
    }

    /// <summary>A Redis blip can delay a heartbeat; one missed pass must not take a pod's viewers off the count.</summary>
    [TestMethod]
    public async Task APeerMissingOnOnePassOnly_IsNotSwept()
    {
        SeedConnection("c1", Peer);

        var removed = await sweeper.SweepAsync();

        Assert.AreEqual(0, removed);
        Assert.IsTrue(IsListed("c1"));
    }

    [TestMethod]
    public async Task APeerThatComesBackBetweenPasses_IsNotSwept()
    {
        SeedConnection("c1", Peer);

        await sweeper.SweepAsync();
        PeerIsAlive();
        await sweeper.SweepAsync();
        redis.ExpireString(StatusConnectionSweeper.HeartbeatKey(Peer));
        var removed = await sweeper.SweepAsync();

        Assert.AreEqual(0, removed, "Missing, back, missing is not two misses in a row.");
        Assert.IsTrue(IsListed("c1"));
    }

    [TestMethod]
    public async Task OnlyTheDeadPeersConnections_AreRemoved()
    {
        PeerIsAlive();
        SeedConnection("live", Peer);
        SeedConnection("dead", "status-api-c-333333333333");

        await sweeper.SweepAsync();
        await sweeper.SweepAsync();

        Assert.IsTrue(IsListed("live"));
        Assert.IsFalse(IsListed("dead"));
    }

    [TestMethod]
    public async Task AnUnsubscribedConnection_IsRemovedFromTheConnectionHashOnly()
    {
        SeedConnection("c1", Peer, eventId: 0);

        await sweeper.SweepAsync();
        await sweeper.SweepAsync();

        Assert.IsNull(redis.GetHashValue(Consts.STATUS_CONNECTIONS, "c1"));
        Assert.IsFalse(redis.HashDeletes.Any(d => d.Key == EventKey(0)), "There is no event hash for event 0 to touch.");
    }

    #endregion

    #region Legacy records

    [TestMethod]
    public async Task ARecentRecordWithNoOwner_IsKept()
    {
        // Written by a pod still on the old version during a rollout; it may well be alive.
        SeedConnection("c1", owner: null, connectedAt: clock.GetUtcNow().UtcDateTime - TimeSpan.FromHours(1));

        await sweeper.SweepAsync();
        await sweeper.SweepAsync();

        Assert.IsTrue(IsListed("c1"));
    }

    [TestMethod]
    public async Task AnOldRecordWithNoOwner_IsRemoved()
    {
        SeedConnection("c1", owner: null, connectedAt: clock.GetUtcNow().UtcDateTime - StatusConnectionSweeper.LegacyRecordMaxAge - TimeSpan.FromMinutes(1));

        var removed = await sweeper.SweepAsync();

        Assert.AreEqual(1, removed, "These are what is already orphaned in the cache today.");
        Assert.IsFalse(IsListed("c1"));
    }

    [TestMethod]
    public async Task AnUnreadableRecord_IsLeftAlone()
    {
        redis.SeedHash(Consts.STATUS_CONNECTIONS, "c1", "not json");

        var removed = await sweeper.SweepAsync();

        Assert.AreEqual(0, removed);
        Assert.IsNotNull(redis.GetHashValue(Consts.STATUS_CONNECTIONS, "c1"));
    }

    #endregion

    #region Heartbeat

    [TestMethod]
    public async Task TheHeartbeat_IsWrittenUnderThisProcessesKey()
    {
        var ok = await sweeper.TryHeartbeatAsync();

        Assert.IsTrue(ok);
        Assert.Contains(StatusConnectionSweeper.HeartbeatKey(Me), redis.StringWrites);
    }

    [TestMethod]
    public async Task AFailedHeartbeat_ReportsFalseRatherThanThrowing()
    {
        redis.FailStringWrites();

        Assert.IsFalse(await sweeper.TryHeartbeatAsync());
    }

    [TestMethod]
    public async Task WithoutARecentHeartbeatOfItsOwn_TheSweepIsSkipped()
    {
        SeedConnection("c1", Peer);

        Assert.IsNull(await sweeper.SweepIfHealthyAsync(), "Redis may only just be back; the peers' heartbeats may be just as late.");
        Assert.IsTrue(IsListed("c1"));
    }

    [TestMethod]
    public async Task WithARecentHeartbeat_TheSweepRuns()
    {
        SeedConnection("c1", Peer);
        await sweeper.TryHeartbeatAsync();

        await sweeper.SweepIfHealthyAsync();
        var removed = await sweeper.SweepIfHealthyAsync();

        Assert.AreEqual(1, removed);
    }

    /// <summary>The two sweeps that condemn a record must not be either side of an outage.</summary>
    [TestMethod]
    public async Task ASkippedSweep_ForgetsWhatWasSuspected()
    {
        SeedConnection("c1", Peer);
        await sweeper.TryHeartbeatAsync();
        await sweeper.SweepIfHealthyAsync();

        clock.Advance(StatusConnectionSweeper.HeartbeatInterval * 3);
        Assert.IsNull(await sweeper.SweepIfHealthyAsync(), "Sanity check: this heartbeat is stale, so the sweep is skipped.");

        await sweeper.TryHeartbeatAsync();
        var removed = await sweeper.SweepIfHealthyAsync();

        Assert.AreEqual(0, removed, "Only one fresh sweep has seen the peer missing.");
        Assert.IsTrue(IsListed("c1"));
    }

    [TestMethod]
    public void TheHeartbeatOutlivesSeveralRefreshes()
    {
        Assert.IsTrue(StatusConnectionSweeper.HeartbeatTtl >= StatusConnectionSweeper.HeartbeatInterval * 3,
            "A single late refresh must not let the key lapse.");
    }

    [TestMethod]
    public void TwoProcessesOnOnePod_GetDifferentIds()
    {
        // A container restart keeps the pod name; the new process must not inherit the old one's records.
        Assert.AreNotEqual(new StatusHubInstance().Id, new StatusHubInstance().Id);
        StringAssert.StartsWith(new StatusHubInstance().Id, Environment.MachineName);
    }

    #endregion

    #region The hub

    private StatusHub CreateHub(string connectionId)
    {
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity([new Claim("azp", "redmist-ios-ui")], "test")));
        var accessValidator = new Mock<IEventAccessValidator>();
        accessValidator.Setup(v => v.ValidateAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        return new StatusHub(new DebugLoggerFactory(), redis.Mux.Object, accessValidator.Object, clock, instance: me)
        {
            Context = context.Object,
            Groups = new Mock<IGroupManager>().Object,
        };
    }

    private StatusConnection? Stored(string connectionId)
    {
        var json = redis.GetHashValue(Consts.STATUS_CONNECTIONS, connectionId);
        return json == null ? null : JsonSerializer.Deserialize<StatusConnection>(json);
    }

    [TestMethod]
    public async Task AConnectionRecord_NamesTheProcessThatServesIt()
    {
        await CreateHub("c1").OnConnectedAsync();

        Assert.AreEqual(Me, Stored("c1")?.InstanceId);
        Assert.IsTrue(me.IsServing("c1"));
    }

    [TestMethod]
    public async Task ADisconnect_StopsServingEvenWhenRedisFails()
    {
        var hub = CreateHub("c1");
        await hub.OnConnectedAsync();
        redis.FailHashReads();

        await hub.OnDisconnectedAsync(null);

        Assert.IsFalse(me.IsServing("c1"), "Whatever the disconnect could not delete has to become the sweeper's.");
    }

    /// <summary>
    /// Without a record, disconnect cannot find the event to remove the connection from, so the event
    /// hash entry written on subscribe was counted forever.
    /// </summary>
    [TestMethod]
    public async Task SubscribingWithNoRecord_RecreatesOneSoDisconnectCanCleanUp()
    {
        var hub = CreateHub("c1");

        await hub.SubscribeToEventV2(EventId);

        var conn = Stored("c1");
        Assert.IsNotNull(conn);
        Assert.AreEqual(EventId, conn.SubscribedEventId);
        Assert.AreEqual(Me, conn.InstanceId);

        await hub.OnDisconnectedAsync(null);

        Assert.IsNull(redis.GetHashValue(EventKey(), "c1"), "The disconnect has to be able to find and remove it.");
    }

    #endregion
}
