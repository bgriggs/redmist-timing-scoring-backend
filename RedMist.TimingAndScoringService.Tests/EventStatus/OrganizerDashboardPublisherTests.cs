using BigMission.TestHelpers.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingAndScoringService.Tests.Shared;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventStatus;

/// <summary>
/// The tick that feeds an organizer's dashboard card.
/// </summary>
/// <remarks>
/// One timer, two messages. They share a cadence because they are drawn on the same card, and stay
/// separate messages so a client already handling viewer counts does not have to change to receive
/// the status beside it.
/// </remarks>
[TestClass]
public class OrganizerDashboardPublisherTests
{
    private const int EventId = 123;

    private FakeRedisDatabase redis = null!;
    private Mock<IHubClients> clients = null!;
    private Mock<IClientProxy> groupProxy = null!;
    private SessionContext sessionContext = null!;
    private TestDbContextFactory dbFactory = null!;
    private FakeTimeProvider clock = null!;
    private readonly List<(string Method, object?[] Args)> sent = [];

    [TestInitialize]
    public void Setup()
    {
        redis = new FakeRedisDatabase();
        clock = new FakeTimeProvider(new DateTimeOffset(new DateTime(2026, 9, 20, 14, 0, 0, DateTimeKind.Utc)));

        groupProxy = new Mock<IClientProxy>();
        groupProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((m, a, _) => sent.Add((m, a)))
            .Returns(Task.CompletedTask);

        clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["event_id"] = EventId.ToString() })
            .Build();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        sessionContext = new SessionContext(configuration, dbFactory, new DebugLoggerFactory(),
            new Mock<ICarLapHistoryService>().Object, clock);
    }

    private OrganizerDashboardPublisher CreatePublisher()
    {
        var hubContext = new Mock<IHubContext<StatusHub>>();
        hubContext.SetupGet(h => h.Clients).Returns(clients.Object);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["event_id"] = EventId.ToString() })
            .Build();

        return new OrganizerDashboardPublisher(new DebugLoggerFactory(), redis.Mux.Object, configuration,
            hubContext.Object, sessionContext, dbFactory, clock);
    }

    private T Payload<T>(string method) => (T)sent.Single(x => x.Method == method).Args[0]!;

    private void SeedViewers(params string[] clientTypes)
    {
        var key = string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId);
        for (var i = 0; i < clientTypes.Length; i++)
        {
            redis.SeedHash(key, $"conn-{i}", clientTypes[i]);
        }
    }

    private void SeedRelayHeartbeat(DateTime at)
        => redis.SeedHash(Consts.RELAY_EVENT_CONNECTIONS, string.Format(Consts.RELAY_HEARTBEAT, EventId),
            JsonSerializer.Serialize(new RelayConnectionEventEntry { EventId = EventId, Timestamp = at }));

    [TestMethod]
    public async Task OneTick_SendsBothMessagesToTheEventsCountsGroup()
    {
        await CreatePublisher().PublishAsync(CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { "ReceiveViewerCounts", "ReceiveEventStatusSummary" },
            sent.Select(x => x.Method).ToArray());
        clients.Verify(c => c.Group(string.Format(Consts.EVENT_VIEWER_COUNTS_SUB, EventId)), Times.Exactly(2));
    }

    [TestMethod]
    public async Task TheViewerCounts_ComeFromTheEventsConnectionHash()
    {
        SeedViewers("Web", "Web", "iOS");

        await CreatePublisher().PublishAsync(CancellationToken.None);

        var counts = Payload<ViewerCountSnapshot>("ReceiveViewerCounts");
        Assert.AreEqual(EventId, counts.EventId);
        Assert.AreEqual(3, counts.Total);
        Assert.AreEqual(2, counts.ByClientType["Web"]);
        Assert.AreEqual(clock.GetUtcNow().UtcDateTime, counts.AsOfUtc);
    }

    [TestMethod]
    public async Task TheStatusSummary_CarriesWhatACardShows()
    {
        using (await sessionContext.SessionStateLock.AcquireWriteLockAsync(CancellationToken.None))
        {
            sessionContext.SessionState.SessionId = 7;
            sessionContext.SessionState.SessionName = "Race";
            sessionContext.SessionState.IsPracticeQualifying = false;
            sessionContext.SessionState.CarPositions.AddRange(
                [new CarPosition { Number = "1" }, new CarPosition { Number = "2" }]);
        }
        sessionContext.RMonitorTrackFlag = Flags.Green;

        await CreatePublisher().PublishAsync(CancellationToken.None);

        var status = Payload<EventStatusSummary>("ReceiveEventStatusSummary");
        Assert.AreEqual(EventId, status.EventId);
        Assert.AreEqual(7, status.SessionId);
        Assert.AreEqual("Race", status.SessionName);
        Assert.AreEqual(2, status.CarCount);
        Assert.AreEqual(Flags.Green.ToString(), status.Flag);
    }

    /// <summary>
    /// Comes from the Sessions row, which is the only place it is written - SessionMonitor records it
    /// with ExecuteUpdateAsync and never touches the in-memory copy. SessionState.LastUpdated exists
    /// and nothing in the solution assigns it, so reading that would have shipped a field that was
    /// null for every event forever while a test set it by hand and passed.
    /// </summary>
    [TestMethod]
    public async Task LastDataUtc_ComesFromTheSessionRowRatherThanSessionState()
    {
        var wrote = new DateTime(2026, 9, 20, 13, 58, 0, DateTimeKind.Utc);
        using (var db = dbFactory.CreateDbContext())
        {
            db.Sessions.Add(new RedMist.TimingCommon.Models.Session
            {
                Id = 7,
                EventId = EventId,
                Name = "Race",
                LastUpdated = wrote,
            });
            await db.SaveChangesAsync();
        }
        using (await sessionContext.SessionStateLock.AcquireWriteLockAsync(CancellationToken.None))
        {
            sessionContext.SessionState.SessionId = 7;
        }

        await CreatePublisher().PublishAsync(CancellationToken.None);

        Assert.AreEqual(wrote, Payload<EventStatusSummary>("ReceiveEventStatusSummary").LastDataUtc);
    }

    [TestMethod]
    public async Task BeforeAnySessionStarts_LastDataIsNull()
    {
        await CreatePublisher().PublishAsync(CancellationToken.None);

        Assert.IsNull(Payload<EventStatusSummary>("ReceiveEventStatusSummary").LastDataUtc);
    }

    /// <summary>
    /// Read from the hash the relay hub writes rather than from anything this pod tracks, so a relay
    /// that is connected but sending nothing reads differently from one that has gone.
    /// </summary>
    [TestMethod]
    public async Task TheRelayHeartbeat_IsReportedAsATimestamp()
    {
        var beat = new DateTime(2026, 9, 20, 13, 59, 45, DateTimeKind.Utc);
        SeedRelayHeartbeat(beat);

        await CreatePublisher().PublishAsync(CancellationToken.None);

        Assert.AreEqual(beat, Payload<EventStatusSummary>("ReceiveEventStatusSummary").RelayLastHeartbeatUtc);
    }

    [TestMethod]
    public async Task ARelayThatHasNeverConnected_ReportsNullRatherThanAnOldDate()
    {
        await CreatePublisher().PublishAsync(CancellationToken.None);

        Assert.IsNull(Payload<EventStatusSummary>("ReceiveEventStatusSummary").RelayLastHeartbeatUtc);
    }

    /// <summary>
    /// An unreadable heartbeat must not cost the whole tick - the counts and the rest of the status
    /// are still true, and "we do not know" is the honest answer for the one field.
    /// </summary>
    [TestMethod]
    public async Task AnUnreadableRelayHeartbeat_DoesNotFailTheTick()
    {
        redis.SeedHash(Consts.RELAY_EVENT_CONNECTIONS, string.Format(Consts.RELAY_HEARTBEAT, EventId), "{not json");

        await CreatePublisher().PublishAsync(CancellationToken.None);

        Assert.HasCount(2, sent);
        Assert.IsNull(Payload<EventStatusSummary>("ReceiveEventStatusSummary").RelayLastHeartbeatUtc);
    }

    /// <summary>
    /// Nobody watching and nothing running is a real answer with a timestamp on it, not an absence -
    /// a card shows a zero it can age rather than a spinner.
    /// </summary>
    [TestMethod]
    public async Task AQuietEvent_StillPublishesBothMessages()
    {
        await CreatePublisher().PublishAsync(CancellationToken.None);

        Assert.AreEqual(0, Payload<ViewerCountSnapshot>("ReceiveViewerCounts").Total);
        var status = Payload<EventStatusSummary>("ReceiveEventStatusSummary");
        Assert.AreEqual(0, status.CarCount);
        Assert.AreEqual(clock.GetUtcNow().UtcDateTime, status.AsOfUtc);
    }
}
