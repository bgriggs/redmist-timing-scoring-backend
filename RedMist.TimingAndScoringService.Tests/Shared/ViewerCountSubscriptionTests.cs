using BigMission.TestHelpers.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Services;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.Tests.Utilities;
using System.Security.Claims;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;
using Organization = RedMist.TimingCommon.Models.Organization;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// The organizer dashboard's subscription to live viewer counts.
/// </summary>
/// <remarks>
/// Two things distinguish it from an ordinary viewer subscription, and both are load-bearing: it is
/// authorized per event against the caller's administered organizations, and it does not record the
/// connection as a viewer of the event it is reporting on.
/// </remarks>
[TestClass]
public class ViewerCountSubscriptionTests
{
    private const string Organizer = "organizer@example.com";
    private const int MineId = 1;
    private const int TheirsId = 2;
    private const int MyEvent = 10;
    private const int MyOtherEvent = 11;
    private const int TheirEvent = 20;

    private FakeRedisDatabase redis = null!;
    private Mock<IGroupManager> groups = null!;
    private IDbContextFactory<TsContext> dbFactory = null!;
    private TsContext db = null!;
    private string connectionId = null!;

    [TestInitialize]
    public void Setup()
    {
        redis = new FakeRedisDatabase();
        groups = new Mock<IGroupManager>();
        connectionId = $"conn-{Guid.NewGuid()}";
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db = dbFactory.CreateDbContext();
    }

    [TestCleanup]
    public void Cleanup() => db.Dispose();

    private StatusHub CreateHub(string? username = Organizer)
    {
        var claims = new List<Claim> { new("azp", "redmist-landing"), new("client_id", "redmist-landing") };
        if (username != null)
        {
            claims.Add(new Claim(ClaimTypes.Name, username));
        }

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));

        var accessValidator = new Mock<IEventAccessValidator>();
        accessValidator.Setup(v => v.ValidateAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new StatusHub(new DebugLoggerFactory(), redis.Mux.Object, accessValidator.Object,
            timeProvider: null, tsContext: dbFactory)
        {
            Context = context.Object,
            Groups = groups.Object,
        };
    }

    private async Task SeedAsync()
    {
        db.Organizations.AddRange(
            new Organization { Id = MineId, ClientId = "relay-mine", Name = "Mine", ShortName = "M" },
            new Organization { Id = TheirsId, ClientId = "relay-theirs", Name = "Theirs", ShortName = "T" });
        db.UserOrganizationMappings.Add(new UserOrganizationMapping
        {
            Username = Organizer,
            OrganizationId = MineId,
            Role = "admin",
        });
        db.Events.AddRange(
            NewEvent(MyEvent, MineId),
            NewEvent(MyOtherEvent, MineId),
            NewEvent(TheirEvent, TheirsId));
        await db.SaveChangesAsync();
    }

    private static ConfigEvent NewEvent(int id, int orgId) => new()
    {
        Id = id,
        OrganizationId = orgId,
        Name = $"Event {id}",
        StartDate = new DateTime(2026, 9, 1),
        EndDate = new DateTime(2026, 9, 2),
    };

    private void SeedViewers(int eventId, params string[] clientTypes)
    {
        var key = string.Format(Consts.STATUS_EVENT_CONNECTIONS, eventId);
        for (var i = 0; i < clientTypes.Length; i++)
        {
            redis.SeedHash(key, $"viewer-{eventId}-{i}", clientTypes[i]);
        }
    }

    /// <summary>
    /// THE ONE THAT MATTERS. An organizer watching their own dashboard must not appear in the number
    /// the dashboard is showing them, nor in the post-event report - and both follow from this
    /// connection never entering the event's hash, because the hash is what the counts read and the
    /// viewer sessions the report is built from are published by the same method that writes it.
    /// </summary>
    [TestMethod]
    public async Task Subscribing_DoesNotRecordTheDashboardAsAViewer()
    {
        await SeedAsync();
        SeedViewers(MyEvent, "Web", "iOS");
        var hub = CreateHub();

        var snapshots = await hub.SubscribeToEventViewerCounts([MyEvent]);

        Assert.AreEqual(2, snapshots[MyEvent].Total, "The dashboard counted itself.");
        Assert.IsNull(redis.GetHashValue(string.Format(Consts.STATUS_EVENT_CONNECTIONS, MyEvent), connectionId),
            "The dashboard's own connection was written into the event's viewer hash.");

        // Not asserted: that the connection is absent from the global connections record. It is not,
        // and should not be - OnConnectedAsync writes that for every connection, the dashboard's
        // included, and nothing derives viewership from it. Only the per-event hash is counted.
    }

    /// <summary>
    /// The dashboard is cross-organization: one page watches every live event the account
    /// administers, which may span organizations.
    /// </summary>
    [TestMethod]
    public async Task SeveralEventsAtOnce_AreEachJoinedAndSnapshotted()
    {
        await SeedAsync();
        SeedViewers(MyEvent, "Web");
        SeedViewers(MyOtherEvent, "Android", "Android", "iOS");
        var hub = CreateHub();

        var snapshots = await hub.SubscribeToEventViewerCounts([MyEvent, MyOtherEvent]);

        CollectionAssert.AreEquivalent(new[] { MyEvent, MyOtherEvent }, snapshots.Keys.ToArray());
        Assert.AreEqual(1, snapshots[MyEvent].Total);
        Assert.AreEqual(3, snapshots[MyOtherEvent].Total);
        Assert.AreEqual(2, snapshots[MyOtherEvent].ByClientType["Android"]);
        Assert.AreEqual(1, snapshots[MyOtherEvent].ByClientType["iOS"]);
        Assert.IsFalse(snapshots[MyOtherEvent].ByClientType.ContainsKey("Web"),
            "A client type with nobody on it was sent as an explicit zero.");

        VerifyJoined(string.Format(Consts.EVENT_VIEWER_COUNTS_SUB, MyEvent));
        VerifyJoined(string.Format(Consts.EVENT_VIEWER_COUNTS_SUB, MyOtherEvent));
    }

    /// <summary>
    /// Dropped rather than refused, so a dashboard whose event list has drifted still gets the
    /// events it may see instead of the whole call failing.
    /// </summary>
    [TestMethod]
    public async Task AnEventTheCallerDoesNotAdminister_IsOmittedAndNotJoined()
    {
        await SeedAsync();
        SeedViewers(TheirEvent, "Web", "Web");
        var hub = CreateHub();

        var snapshots = await hub.SubscribeToEventViewerCounts([MyEvent, TheirEvent]);

        CollectionAssert.AreEqual(new[] { MyEvent }, snapshots.Keys.ToArray());
        groups.Verify(g => g.AddToGroupAsync(connectionId,
            string.Format(Consts.EVENT_VIEWER_COUNTS_SUB, TheirEvent), It.IsAny<CancellationToken>()), Times.Never());
    }

    /// <summary>
    /// The dashboard reads the same hash the relay does, so in-car phones arrive as an InCar key rather
    /// than inside iOS or Android - and the total still counts them, once.
    /// </summary>
    [TestMethod]
    public async Task InCarConnections_ArriveAsTheirOwnKeyAndCountTowardTheTotal()
    {
        await SeedAsync();
        SeedViewers(MyEvent, "Android", RedMist.Backend.Shared.Utilities.ClientTypeHelper.InCar,
            RedMist.Backend.Shared.Utilities.ClientTypeHelper.InCar);
        var hub = CreateHub();

        var snapshot = (await hub.SubscribeToEventViewerCounts([MyEvent]))[MyEvent];

        Assert.AreEqual(3, snapshot.Total);
        Assert.AreEqual(2, snapshot.ByClientType[RedMist.Backend.Shared.Utilities.ClientTypeHelper.InCar]);
        Assert.AreEqual(1, snapshot.ByClientType["Android"]);
        Assert.AreEqual(snapshot.Total, snapshot.ByClientType.Values.Sum(),
            "The buckets no longer add up to the total, so something is counted twice or dropped.");
    }

    [TestMethod]
    public async Task ANonAdminMember_GetsNothing()
    {
        await SeedAsync();
        db.UserOrganizationMappings.Add(new UserOrganizationMapping
        {
            Username = "viewer@example.com",
            OrganizationId = MineId,
            Role = "viewer",
        });
        await db.SaveChangesAsync();
        var hub = CreateHub("viewer@example.com");

        Assert.IsEmpty(await hub.SubscribeToEventViewerCounts([MyEvent]));
    }

    [TestMethod]
    public async Task ATokenIdentifyingNobody_GetsNothing()
    {
        await SeedAsync();
        var hub = CreateHub(username: null);

        Assert.IsEmpty(await hub.SubscribeToEventViewerCounts([MyEvent]));
    }

    [TestMethod]
    public async Task ADeletedEvent_IsNotWatchable()
    {
        await SeedAsync();
        db.Events.Single(e => e.Id == MyEvent).IsDeleted = true;
        await db.SaveChangesAsync();
        var hub = CreateHub();

        Assert.IsEmpty(await hub.SubscribeToEventViewerCounts([MyEvent]));
    }

    /// <summary>
    /// A dashboard cannot make one connection walk an unbounded list of ids. Well above any real
    /// organizer, and present only so the list has an end.
    /// </summary>
    [TestMethod]
    public async Task TheNumberOfEventsOneConnectionCanWatch_IsBounded()
    {
        await SeedAsync();
        var ids = new List<int>();
        for (var i = 0; i < StatusHub.MaxWatchedEvents + 25; i++)
        {
            var id = 1000 + i;
            db.Events.Add(NewEvent(id, MineId));
            ids.Add(id);
        }
        await db.SaveChangesAsync();
        var hub = CreateHub();

        var snapshots = await hub.SubscribeToEventViewerCounts([.. ids]);

        Assert.AreEqual(StatusHub.MaxWatchedEvents, snapshots.Count);
    }

    /// <summary>An event nobody is watching is a real zero, with a timestamp, not an absence.</summary>
    [TestMethod]
    public async Task AnEventNobodyIsWatching_IsZeroRatherThanMissing()
    {
        await SeedAsync();
        var hub = CreateHub();

        var snapshot = (await hub.SubscribeToEventViewerCounts([MyEvent]))[MyEvent];

        Assert.AreEqual(MyEvent, snapshot.EventId);
        Assert.AreEqual(0, snapshot.Total);
        Assert.IsEmpty(snapshot.ByClientType);
        Assert.AreNotEqual(default, snapshot.AsOfUtc, "A zero arrived with no timestamp to age.");
    }

    [TestMethod]
    public async Task Unsubscribing_LeavesTheGroup()
    {
        await SeedAsync();
        var hub = CreateHub();

        await hub.UnsubscribeFromEventViewerCounts([MyEvent]);

        groups.Verify(g => g.RemoveFromGroupAsync(connectionId,
            string.Format(Consts.EVENT_VIEWER_COUNTS_SUB, MyEvent), It.IsAny<CancellationToken>()), Times.Once());
    }

    private void VerifyJoined(string group) =>
        groups.Verify(g => g.AddToGroupAsync(connectionId, group, It.IsAny<CancellationToken>()), Times.Once());
}
