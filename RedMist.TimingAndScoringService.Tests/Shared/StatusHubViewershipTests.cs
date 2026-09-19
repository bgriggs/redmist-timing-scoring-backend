using BigMission.TestHelpers.Testing;
using Microsoft.AspNetCore.SignalR;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Services;
using RedMist.Database.Models;
using System.Security.Claims;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// Covers the viewer session transitions the hub publishes for each event it serves.
/// </summary>
/// <remarks>
/// These entries are the only record of who watched an event and for how long. Nothing else persists
/// it: the live connection hash is a snapshot that the orchestrator deletes outright when the event
/// is torn down, and the Prometheus gauges are scrape-retention only.
/// </remarks>
[TestClass]
public class StatusHubViewershipTests
{
    private const int EventId = 4321;
    private const int OtherEventId = 9876;

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

    private List<ViewerSessionEvent> ViewerEventsFor(int eventId)
    {
        var key = string.Format(Consts.EVENT_VIEWERSHIP_STREAM_KEY, eventId);
        return [.. redis.StreamWrites
            .Where(w => w.Key == key && w.Field == Consts.VIEWER_SESSION_TYPE)
            .Select(w => JsonSerializer.Deserialize<ViewerSessionEvent>(w.Value)!)];
    }

    private List<ViewerSessionEvent> AllViewerEvents()
        => [.. redis.StreamWrites
            .Where(w => w.Field == Consts.VIEWER_SESSION_TYPE)
            .Select(w => JsonSerializer.Deserialize<ViewerSessionEvent>(w.Value)!)];

    [TestMethod]
    public async Task SubscribeToEventV2_PublishesAViewerSessionStart()
    {
        var hub = CreateHub("redmist-android-ui");
        await hub.OnConnectedAsync();

        await hub.SubscribeToEventV2(EventId);

        var published = ViewerEventsFor(EventId).Single();
        Assert.AreEqual(ViewerSessionEventKind.Start, published.Kind);
        Assert.AreEqual(EventId, published.EventId);
        Assert.AreEqual(connectionId, published.ConnectionId);
        Assert.AreEqual("Android", published.ClientType);
        Assert.IsFalse(published.IsInCar);
    }

    /// <summary>
    /// The stream is capped on write and given a sliding expiry. Redis never trims a stream on
    /// consumption, and viewers can sit on an event whose logger pod has already been deleted, so
    /// without both of these nothing bounds the stream's growth or its lifetime.
    /// </summary>
    [TestMethod]
    public async Task SubscribeToEventV2_CapsTheStreamAndRefreshesItsExpiry()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();

        await hub.SubscribeToEventV2(EventId);

        var key = string.Format(Consts.EVENT_VIEWERSHIP_STREAM_KEY, EventId);
        var write = redis.StreamWrites.Single(w => w.Key == key);
        Assert.AreEqual(Consts.EVENT_VIEWERSHIP_STREAM_MAX_LENGTH, write.MaxLength);
        Assert.IsTrue(write.Approximate);
        Assert.Contains((key, (TimeSpan?)Consts.VIEWERSHIP_STREAM_TTL), redis.KeyExpires);
    }

    /// <summary>Event 0 is the "no event selected" group, not an event anyone is watching.</summary>
    [TestMethod]
    public async Task SubscribeToEventV2_WithNoEventSelected_PublishesNothing()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();

        await hub.SubscribeToEventV2(0);

        Assert.IsEmpty(AllViewerEvents());
    }

    [TestMethod]
    public async Task SubscribeToEventV2_Twice_PublishesOnlyOneStart()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();

        await hub.SubscribeToEventV2(EventId);
        await hub.SubscribeToEventV2(EventId);

        Assert.HasCount(1, ViewerEventsFor(EventId));
    }

    /// <summary>
    /// Going back to the event list and into the same event again is the ordinary way people browse,
    /// and it has to count as a second stretch of watching.
    /// </summary>
    /// <remarks>
    /// The unsubscribe used to leave the recorded event id in place, so the resubscribe saw an
    /// unchanged event and published no start. The viewer was then invisible until the logger's
    /// reconciler inferred them back from the live connection hash - at their original connect time,
    /// overlapping the session that had just been closed.
    /// </remarks>
    [TestMethod]
    public async Task ResubscribingToTheSameEventAfterUnsubscribing_PublishesASecondStart()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(EventId);
        await hub.UnsubscribeFromEventV2(EventId);

        await hub.SubscribeToEventV2(EventId);

        var published = ViewerEventsFor(EventId);
        Assert.HasCount(3, published);
        Assert.AreEqual(ViewerSessionEventKind.Start, published[0].Kind);
        Assert.AreEqual(ViewerSessionEventKind.End, published[1].Kind);
        Assert.AreEqual(ViewerSessionEventKind.Start, published[2].Kind);
        Assert.AreEqual(EventId, StoredConnection()!.SubscribedEventId);
    }

    /// <summary>
    /// An unsubscribe that arrives after the client has already moved on must not wipe the newer
    /// subscription, or the connection stops being counted on the event it is actually watching.
    /// </summary>
    [TestMethod]
    public async Task UnsubscribingFromAnEventAlreadyLeft_DoesNotClearTheCurrentSubscription()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(EventId);
        await hub.SubscribeToEventV2(OtherEventId);

        await hub.UnsubscribeFromEventV2(EventId);

        Assert.AreEqual(OtherEventId, StoredConnection()!.SubscribedEventId);
    }

    /// <summary>
    /// A client moving between events produces two transitions on two different streams, each read by
    /// that event's own logger pod. Neither logger needs to know the other event exists.
    /// </summary>
    [TestMethod]
    public async Task SubscribeToEventV2_AfterAnotherEvent_EndsThePreviousAndStartsTheNew()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(EventId);

        await hub.SubscribeToEventV2(OtherEventId);

        var ended = ViewerEventsFor(EventId).Last();
        Assert.AreEqual(ViewerSessionEventKind.End, ended.Kind);
        Assert.AreEqual(ViewerSessionEndReason.Switched, ended.Reason);

        var started = ViewerEventsFor(OtherEventId).Single();
        Assert.AreEqual(ViewerSessionEventKind.Start, started.Kind);
        Assert.AreEqual(OtherEventId, started.EventId);
    }

    [TestMethod]
    public async Task UnsubscribeFromEventV2_PublishesAnEndWithReasonUnsubscribed()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(EventId);

        await hub.UnsubscribeFromEventV2(EventId);

        var ended = ViewerEventsFor(EventId).Last();
        Assert.AreEqual(ViewerSessionEventKind.End, ended.Kind);
        Assert.AreEqual(ViewerSessionEndReason.Unsubscribed, ended.Reason);
    }

    /// <summary>
    /// The hub context no longer knows which event the connection was on by the time it disconnects,
    /// so the end is addressed from the tracking record the connection left behind.
    /// </summary>
    [TestMethod]
    public async Task OnDisconnectedAsync_PublishesAnEndOnTheSubscribedEvent()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        await hub.SubscribeToEventV2(EventId);

        await hub.OnDisconnectedAsync(null);

        var ended = ViewerEventsFor(EventId).Last();
        Assert.AreEqual(ViewerSessionEventKind.End, ended.Kind);
        Assert.AreEqual(ViewerSessionEndReason.Disconnected, ended.Reason);
    }

    [TestMethod]
    public async Task OnDisconnectedAsync_ForAConnectionThatNeverSubscribed_PublishesNothing()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();

        await hub.OnDisconnectedAsync(null);

        Assert.IsEmpty(AllViewerEvents());
    }

    [TestMethod]
    public async Task SubscribeToInCarDriverEventV2_PublishesAStartMarkedInCarWithTheCarNumber()
    {
        var hub = CreateHub("redmist-ios-ui");
        await hub.OnConnectedAsync();

        await hub.SubscribeToInCarDriverEventV2(EventId, "42");

        var started = ViewerEventsFor(EventId).Single();
        Assert.AreEqual(ViewerSessionEventKind.Start, started.Kind);
        Assert.IsTrue(started.IsInCar);
        Assert.AreEqual("42", started.CarNumber);
    }

    /// <summary>
    /// Switching cars within in-car mode is not leaving the event, so it must not end and restart the
    /// session - that would split one stretch of watching into two and dip concurrency in between.
    /// </summary>
    [TestMethod]
    public async Task SubscribeToInCarDriverEventV2_ThenSwitchingCars_DoesNotStartASecondSession()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();

        await hub.SubscribeToInCarDriverEventV2(EventId, "42");
        await hub.SubscribeToInCarDriverEventV2(EventId, "7");

        Assert.HasCount(1, ViewerEventsFor(EventId));
    }

    /// <summary>
    /// Leaving in-car mode means switching back to the timing view, not leaving the event. Before the
    /// in-car tracking fix this connection was associated with no event at all afterwards, so no end
    /// could ever be published for it and the session would have run to the duration cap.
    /// </summary>
    [TestMethod]
    public async Task UnsubscribeFromInCarDriverEventV2_ThenDisconnecting_StillEndsTheSession()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        await hub.SubscribeToInCarDriverEventV2(EventId, "42");
        await hub.UnsubscribeFromInCarDriverEventV2(EventId, "42");

        await hub.OnDisconnectedAsync(null);

        var ended = ViewerEventsFor(EventId).Last();
        Assert.AreEqual(ViewerSessionEventKind.End, ended.Kind);
        Assert.AreEqual(ViewerSessionEndReason.Disconnected, ended.Reason);
    }

    /// <summary>
    /// Viewership is telemetry riding alongside the live feed. A Redis failure writing it must never
    /// cost a client the subscription it asked for; the logger's reconciler recovers what is lost.
    /// </summary>
    [TestMethod]
    public async Task SubscribeToEventV2_WhenTheStreamWriteFails_TheClientIsStillSubscribed()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();
        redis.FailStreamWrites();

        await hub.SubscribeToEventV2(EventId);

        groups.Verify(g => g.AddToGroupAsync(connectionId, string.Format(Consts.EVENT_SUB_V2, EventId),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual(EventId, StoredConnection()!.SubscribedEventId);
    }

    /// <summary>
    /// A connection with no token resolves to the Web client type rather than being dropped: the hub
    /// is deliberately anonymous-capable and those viewers still count.
    /// </summary>
    [TestMethod]
    public async Task SubscribeToEventV2_WithoutAToken_PublishesAStartTypedAsWeb()
    {
        var hub = CreateHub(null);
        await hub.OnConnectedAsync();

        await hub.SubscribeToEventV2(EventId);

        Assert.AreEqual("Web", ViewerEventsFor(EventId).Single().ClientType);
    }
}
