using BigMission.TestHelpers.Testing;
using Microsoft.AspNetCore.SignalR;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Services;
using RedMist.Backend.Shared.Utilities;
using System.Security.Claims;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// Covers the viewer-facing hub: the access-code gate on private events, the group names clients are
/// joined to, and the connection record the rest of the system counts and addresses clients by.
/// </summary>
[TestClass]
public class StatusHubTests
{
    private const int EventId = 4321;

    private FakeRedisDatabase redis = null!;
    private Mock<IGroupManager> groups = null!;
    private Mock<IEventAccessValidator> accessValidator = null!;
    private string connectionId = null!;

    [TestInitialize]
    public void Setup()
    {
        redis = new FakeRedisDatabase();
        groups = new Mock<IGroupManager>();
        connectionId = $"conn-{Guid.NewGuid()}";
        accessValidator = new Mock<IEventAccessValidator>();
        accessValidator
            .Setup(v => v.ValidateAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private StatusHub CreateHub(string? clientId = "redmist-browser-ui")
    {
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns(clientId == null
            ? (ClaimsPrincipal?)null
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim("azp", clientId)], "test")));

        return new StatusHub(new DebugLoggerFactory(), redis.Mux.Object, accessValidator.Object)
        {
            Context = context.Object,
            Groups = groups.Object,
        };
    }

    private StatusConnection? StoredConnection()
    {
        var json = redis.GetHashValue(Consts.STATUS_CONNECTIONS, connectionId);
        return json == null ? null : JsonSerializer.Deserialize<StatusConnection>(json);
    }

    private void VerifyJoined(string group, Times times) =>
        groups.Verify(g => g.AddToGroupAsync(connectionId, group, It.IsAny<CancellationToken>()), times);

    #region Connection lifecycle

    [TestMethod]
    public async Task OnConnectedAsync_RecordsAnUnsubscribedConnectionForTheClient()
    {
        var hub = CreateHub("redmist-ios-ui");

        await hub.OnConnectedAsync();

        var conn = StoredConnection();
        Assert.IsNotNull(conn);
        Assert.AreEqual("redmist-ios-ui", conn.ClientId);
        Assert.AreEqual(0, conn.SubscribedEventId);
    }

    /// <summary>The hub is deliberately anonymous-capable, so a token-less connection is still tracked.</summary>
    [TestMethod]
    public async Task OnConnectedAsync_WithoutAUser_StillRecordsTheConnection()
    {
        var hub = CreateHub(clientId: null);

        await hub.OnConnectedAsync();

        Assert.IsNotNull(StoredConnection());
    }

    /// <summary>
    /// A token that authenticates but carries no azp claim has to be treated as an unidentified
    /// client, not rejected: this hub is reachable anonymously by design.
    /// </summary>
    [TestMethod]
    public async Task OnConnectedAsync_WithATokenThatHasNoClientIdClaim_TracksItWithoutAClientId()
    {
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "someone")], "test")));
        var hub = new StatusHub(new DebugLoggerFactory(), redis.Mux.Object, accessValidator.Object)
        {
            Context = context.Object,
            Groups = groups.Object,
        };

        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(EventId);

        Assert.IsNull(StoredConnection()!.ClientId);
        Assert.AreEqual("Web", redis.GetHashValue(string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId), connectionId));
    }

    /// <summary>Connection tracking is bookkeeping; losing the cache must not refuse the connection.</summary>
    [TestMethod]
    public async Task OnConnectedAsync_WhenTheCacheIsDown_DoesNotFaultTheConnection()
    {
        redis.FailHashWrites();
        var hub = CreateHub();

        await hub.OnConnectedAsync();

        Assert.IsNull(StoredConnection(), "the write failed, so the connection is simply untracked");
    }

    [TestMethod]
    public async Task OnDisconnectedAsync_RemovesTheConnectionAndItsEventEntry()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(EventId);
        redis.HashDeletes.Clear();

        await hub.OnDisconnectedAsync(null);

        Assert.Contains((Consts.STATUS_CONNECTIONS, connectionId), redis.HashDeletes);
        Assert.Contains((string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId), connectionId), redis.HashDeletes);
    }

    [TestMethod]
    public async Task OnDisconnectedAsync_ForAConnectionThatNeverSubscribed_OnlyRemovesTheConnection()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();

        await hub.OnDisconnectedAsync(null);

        Assert.HasCount(1, redis.HashDeletes);
        Assert.AreEqual(Consts.STATUS_CONNECTIONS, redis.HashDeletes[0].Key);
    }

    [TestMethod]
    public async Task OnDisconnectedAsync_WhenTheCacheIsDown_DoesNotFaultTheDisconnect()
    {
        redis.FailHashReads();
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(EventId);

        await hub.OnDisconnectedAsync(new IOException("socket reset"));

        // The read that decides which event entry to clean up is what failed, so nothing was removed.
        Assert.IsEmpty(redis.HashDeletes);
    }

    #endregion

    #region Event subscriptions and the access-code gate

    [TestMethod]
    public async Task SubscribeToEventV2WithCode_WhenTheCodeIsRejected_ThrowsAndDoesNotJoinTheGroup()
    {
        accessValidator
            .Setup(v => v.ValidateAsync(EventId, "wrong", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var hub = CreateHub();

        await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToEventV2WithCode(EventId, "wrong"));

        VerifyJoined(string.Format(Consts.EVENT_SUB_V2, EventId), Times.Never());
    }

    [TestMethod]
    public async Task SubscribeToEventV2WithCode_PassesTheSuppliedCodeToTheValidator()
    {
        var hub = CreateHub();

        await hub.SubscribeToEventV2WithCode(EventId, "1234567");

        accessValidator.Verify(v => v.ValidateAsync(EventId, "1234567", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Clients subscribe to event 0 before they have picked an event. There is nothing to authorize
    /// and nothing to count, but the group join still has to happen.
    /// </summary>
    [TestMethod]
    public async Task SubscribeToEventV2_WithNoEventSelected_SkipsTheAccessCheckAndTheConnectionCount()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();

        await hub.SubscribeToEventV2(0);

        accessValidator.Verify(v => v.ValidateAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyJoined(string.Format(Consts.EVENT_SUB_V2, 0), Times.Once());
        Assert.IsNull(redis.GetHashValue(string.Format(Consts.STATUS_EVENT_CONNECTIONS, 0), connectionId));
    }

    [TestMethod]
    public async Task SubscribeToEventV2_JoinsTheEventGroupAndCountsTheConnectionUnderItsClientType()
    {
        var hub = CreateHub("redmist-ios-ui");
        await hub.OnConnectedAsync();

        await hub.SubscribeToEventV2(EventId);

        VerifyJoined(string.Format(Consts.EVENT_SUB_V2, EventId), Times.Once());
        Assert.AreEqual("iOS", redis.GetHashValue(string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId), connectionId));
        Assert.AreEqual(EventId, StoredConnection()!.SubscribedEventId);
    }

    /// <summary>
    /// A subscribe that arrives before, or without, the connect record still has to be counted, or
    /// the relay's connected-client telemetry silently under-reports.
    /// </summary>
    [TestMethod]
    public async Task SubscribeToEventV2_WithNoConnectionRecord_CountsTheClientAsWeb()
    {
        var hub = CreateHub();

        await hub.SubscribeToEventV2(EventId);

        Assert.AreEqual("Web", redis.GetHashValue(string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId), connectionId));
    }

    [TestMethod]
    public async Task SubscribeToEventV2_AfterSwitchingEvents_DropsTheEntryForThePreviousEvent()
    {
        const int previousEventId = EventId + 1;
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(previousEventId);

        await hub.SubscribeToEventV2(EventId);

        Assert.Contains((string.Format(Consts.STATUS_EVENT_CONNECTIONS, previousEventId), connectionId), redis.HashDeletes);
        Assert.AreEqual(EventId, StoredConnection()!.SubscribedEventId);
    }

    /// <summary>
    /// Connection counting is telemetry. Losing the cache must not cost the client its subscription.
    /// </summary>
    [TestMethod]
    public async Task SubscribeToEventV2_WhenConnectionTrackingFails_TheClientIsStillSubscribed()
    {
        redis.FailHashReads();
        var hub = CreateHub();

        await hub.SubscribeToEventV2(EventId);

        VerifyJoined(string.Format(Consts.EVENT_SUB_V2, EventId), Times.Once());
    }

    /// <summary>
    /// A failed group join means the client will never receive updates, so it must surface as an
    /// error rather than a subscribe that quietly does nothing.
    /// </summary>
    [TestMethod]
    public async Task SubscribeToEventV2_WhenTheGroupJoinFails_TheFailureReachesTheCaller()
    {
        groups
            .Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("backplane unavailable"));
        var hub = CreateHub();

        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.SubscribeToEventV2(EventId));
    }

    /// <summary>The v1 entry point is a shim; clients on it must land in the same group as v2 clients.</summary>
    [TestMethod]
    public async Task SubscribeToEvent_V1_JoinsTheSameGroupAsV2()
    {
        var hub = CreateHub();

        await hub.SubscribeToEvent(EventId);

        VerifyJoined(string.Format(Consts.EVENT_SUB_V2, EventId), Times.Once());
    }

    [TestMethod]
    public async Task UnsubscribeFromEvent_LeavesTheGroupAndStopsCountingTheConnection()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(EventId);

        await hub.UnsubscribeFromEvent(EventId);

        groups.Verify(g => g.RemoveFromGroupAsync(connectionId, string.Format(Consts.EVENT_SUB_V2, EventId), It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsNull(redis.GetHashValue(string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId), connectionId));
    }

    [TestMethod]
    public async Task UnsubscribeFromEventV2_WhenTheCacheIsDown_StillLeavesTheGroup()
    {
        redis.FailHashDeletes();
        var hub = CreateHub();

        await hub.UnsubscribeFromEventV2(EventId);

        groups.Verify(g => g.RemoveFromGroupAsync(connectionId, string.Format(Consts.EVENT_SUB_V2, EventId), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Control logs

    [TestMethod]
    public async Task SubscribeToControlLogs_JoinsTheEventControlLogGroup()
    {
        var hub = CreateHub();

        await hub.SubscribeToControlLogs(EventId);

        VerifyJoined($"{EventId}-cl", Times.Once());
    }

    [TestMethod]
    public async Task SubscribeToCarControlLogs_JoinsThePerCarGroup()
    {
        var hub = CreateHub();

        await hub.SubscribeToCarControlLogs(EventId, "42");

        VerifyJoined($"{EventId}-42", Times.Once());
    }

    #endregion

    #region In-car driver mode

    [TestMethod]
    public async Task SubscribeToInCarDriverEvent_JoinsTheCarGroupAndRecordsTheDriverConnection()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();

        await hub.SubscribeToInCarDriverEvent(EventId, "42");

        VerifyJoined(string.Format(Consts.IN_CAR_EVENT_SUB, EventId, "42"), Times.Once());
        var conn = StoredConnection()!;
        Assert.AreEqual(EventId, conn.InCarDriverConnection!.EventId);
        Assert.AreEqual("42", conn.InCarDriverConnection.CarNumber);
    }

    [TestMethod]
    public async Task SubscribeToInCarDriverEventV2_UsesTheV2GroupName()
    {
        var hub = CreateHub();

        await hub.SubscribeToInCarDriverEventV2(EventId, "42");

        VerifyJoined(string.Format(Consts.IN_CAR_EVENT_SUB_V2, EventId, "42"), Times.Once());
    }

    /// <summary>
    /// A driver in in-car mode is still watching the event, and has to keep being counted as one.
    /// </summary>
    /// <remarks>
    /// In-car tracking used to be registered against event 0, which took the "moved to another event"
    /// branch of the tracking update and deleted the connection from its event's hash. The driver
    /// vanished from the event's live client counts, and on disconnect the cleanup no longer knew
    /// which event to tidy up. In-car mode is a phone feature, so what that lost was mobile viewers
    /// specifically. This asserts the opposite of what it used to.
    ///
    /// Counted under InCar rather than under its device: a phone in driver mode is its own live-count
    /// bucket, not an addition to iOS or Android, so the relay can total the buckets without counting
    /// the phone twice.
    /// </remarks>
    [TestMethod]
    [DataRow(false, DisplayName = "V1")]
    [DataRow(true, DisplayName = "V2")]
    public async Task SubscribeToInCarDriverEvent_KeepsTheClientCountedAsAViewerOfTheEvent(bool v2)
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(EventId);

        await (v2 ? hub.SubscribeToInCarDriverEventV2(EventId, "42") : hub.SubscribeToInCarDriverEvent(EventId, "42"));

        Assert.DoesNotContain((string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId), connectionId), redis.HashDeletes);
        Assert.AreEqual(EventId, StoredConnection()!.SubscribedEventId);
        Assert.AreEqual(ClientTypeHelper.InCar,
            redis.GetHashValue(string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId), connectionId));
    }

    /// <summary>
    /// Leaving in-car mode must not stop the connection being counted as a viewer: the driver is
    /// switching back to the timing view, not leaving the event.
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "V1")]
    [DataRow(true, DisplayName = "V2")]
    public async Task UnsubscribeFromInCarDriverEvent_LeavesTheEventSubscriptionIntact(bool v2)
    {
        // Android rather than the default: "Web" is also the fallback when no client id is known, so
        // asserting Web could not tell a restored device from a lost one.
        var hub = CreateHub("redmist-android-ui");
        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(EventId);
        await (v2 ? hub.SubscribeToInCarDriverEventV2(EventId, "42") : hub.SubscribeToInCarDriverEvent(EventId, "42"));

        await (v2 ? hub.UnsubscribeFromInCarDriverEventV2(EventId, "42") : hub.UnsubscribeFromInCarDriverEvent(EventId, "42"));

        Assert.AreEqual(EventId, StoredConnection()!.SubscribedEventId);
        Assert.DoesNotContain((string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId), connectionId), redis.HashDeletes);

        // Back under its device. Leaving driver mode passes no event id, and the hash write used to key
        // off that alone - so a phone that had left driver mode stayed counted as InCar until it
        // disconnected.
        Assert.AreEqual("Android", redis.GetHashValue(string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId), connectionId),
            "Leaving driver mode did not put the phone back under its own device.");
    }

    /// <summary>
    /// The main safety property of keying the hash write on the connection's current event: once the
    /// viewer has left the event, leaving driver mode afterwards must not put them back into its
    /// count. The ordinary unsubscribe zeroes the subscribed event, and the in-car exit writes nothing
    /// for event 0.
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "V1")]
    [DataRow(true, DisplayName = "V2")]
    public async Task LeavingDriverModeAfterLeavingTheEvent_DoesNotResurrectTheCount(bool v2)
    {
        var hub = CreateHub("redmist-android-ui");
        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(EventId);
        await (v2 ? hub.SubscribeToInCarDriverEventV2(EventId, "42") : hub.SubscribeToInCarDriverEvent(EventId, "42"));

        await hub.UnsubscribeFromEventV2(EventId);
        await (v2 ? hub.UnsubscribeFromInCarDriverEventV2(EventId, "42") : hub.UnsubscribeFromInCarDriverEvent(EventId, "42"));

        Assert.IsNull(redis.GetHashValue(string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId), connectionId),
            "Leaving driver mode put a viewer who had already left back into the event's count.");
    }

    /// <summary>A connection with no record has no current event, so leaving driver mode writes nothing.</summary>
    [TestMethod]
    public async Task LeavingDriverModeWithNoConnectionRecord_WritesNothing()
    {
        var hub = CreateHub("redmist-android-ui");

        await hub.UnsubscribeFromInCarDriverEventV2(EventId, "42");

        Assert.IsNull(redis.GetHashValue(string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId), connectionId));
    }

    /// <summary>Resubscribing to the same event while still driving keeps the phone in its InCar bucket.</summary>
    [TestMethod]
    public async Task ResubscribingWhileInDriverMode_KeepsTheInCarBucket()
    {
        var hub = CreateHub("redmist-android-ui");
        await hub.OnConnectedAsync();
        await hub.SubscribeToInCarDriverEventV2(EventId, "42");

        await hub.SubscribeToEventV2(EventId);

        Assert.AreEqual(ClientTypeHelper.InCar,
            redis.GetHashValue(string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId), connectionId));
    }

    /// <summary>
    /// Driving at one event and watching another is counted "In Vehicle" only where the car is. At
    /// the other event the phone is an ordinary viewer on its own device.
    /// </summary>
    [TestMethod]
    public async Task WatchingAnotherEventWhileDriving_CountsTheDeviceThereNotInCar()
    {
        const int otherEvent = EventId + 1;
        var hub = CreateHub("redmist-android-ui");
        await hub.OnConnectedAsync();
        await hub.SubscribeToInCarDriverEventV2(EventId, "42");

        await hub.SubscribeToEventV2(otherEvent);

        Assert.AreEqual("Android", redis.GetHashValue(string.Format(Consts.STATUS_EVENT_CONNECTIONS, otherEvent), connectionId),
            "A phone driving at one event was counted in-vehicle at another.");
    }

    /// <summary>
    /// Both in-car unsubscribes leave their own group and clear the driver connection off the tracking
    /// record; a driver who leaves in-car mode must stop being addressable as one.
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "V1")]
    [DataRow(true, DisplayName = "V2")]
    public async Task UnsubscribeFromInCarDriverEvent_LeavesTheGroupAndClearsTheDriverConnection(bool v2)
    {
        var group = string.Format(v2 ? Consts.IN_CAR_EVENT_SUB_V2 : Consts.IN_CAR_EVENT_SUB, EventId, "42");
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        await (v2 ? hub.SubscribeToInCarDriverEventV2(EventId, "42") : hub.SubscribeToInCarDriverEvent(EventId, "42"));

        await (v2 ? hub.UnsubscribeFromInCarDriverEventV2(EventId, "42") : hub.UnsubscribeFromInCarDriverEvent(EventId, "42"));

        groups.Verify(g => g.RemoveFromGroupAsync(connectionId, group, It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsNull(StoredConnection()!.InCarDriverConnection);
    }

    #endregion

    /// <summary>
    /// The control log unsubscribes carry no connection bookkeeping with them. All each one does is
    /// compute a group name and leave it, so the group name is the whole behavior worth pinning;
    /// getting it wrong leaves a client receiving updates it asked to stop receiving.
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "Whole event")]
    [DataRow(true, DisplayName = "Single car")]
    public async Task UnsubscribeFromControlLogs_LeavesTheGroupItNames(bool perCar)
    {
        var group = perCar ? $"{EventId}-42" : $"{EventId}-cl";
        var hub = CreateHub();

        await (perCar ? hub.UnsubscribeFromCarControlLogs(EventId, "42") : hub.UnsubscribeFromControlLogs(EventId));

        groups.Verify(g => g.RemoveFromGroupAsync(connectionId, group, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Every subscribe entry point has to run the access-code gate, or a private event leaks through
    /// whichever one was missed.
    /// </summary>
    [TestMethod]
    public async Task EverySubscribeEntryPoint_RejectsAPrivateEventWithoutAValidCode()
    {
        accessValidator
            .Setup(v => v.ValidateAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var hub = CreateHub();

        await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToEvent(EventId));
        await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToEventV2(EventId));
        await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToControlLogs(EventId));
        await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToCarControlLogs(EventId, "42"));
        await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToInCarDriverEvent(EventId, "42"));
        await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToInCarDriverEventV2(EventId, "42"));

        groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
