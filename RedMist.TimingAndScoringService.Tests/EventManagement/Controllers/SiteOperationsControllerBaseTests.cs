using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventManagement.Controllers;
using RedMist.EventManagement.Models;
using RedMist.EventManagement.Operations;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingAndScoringService.Tests.Shared;
using StackExchange.Redis;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ConfigEvent = RedMist.TimingCommon.Models.Configuration.Event;
using Organization = RedMist.TimingCommon.Models.Organization;
using Session = RedMist.TimingCommon.Models.Session;

namespace RedMist.TimingAndScoringService.Tests.EventManagement.Controllers;

/// <summary>
/// The site operations page's API: which events count as active, how the relay, pod, viewer and
/// message figures are put together, and that the role - not organization membership - is what
/// lets a caller in.
/// </summary>
[TestClass]
public class SiteOperationsControllerBaseTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 16, 0, 0, DateTimeKind.Utc);

    private const int Alpha = 1;
    private const int Bravo = 2;
    private const int Charlie = 3;

    /// <summary>Live, with a fresh heartbeat on a connection that is still registered.</summary>
    private const int AlphaLive = 10;
    /// <summary>Flagged live, but no relay heartbeat.</summary>
    private const int AlphaNoHeartbeat = 11;
    /// <summary>Not flagged live, but a heartbeat from a relay gone quiet - not yet torn down.</summary>
    private const int BravoQuiet = 20;
    /// <summary>Neither live nor heartbeating, but pods are running for it.</summary>
    private const int CharlieOrphan = 30;
    /// <summary>Nothing about it is running.</summary>
    private const int CharlieIdle = 40;
    /// <summary>Flagged live, but deleted.</summary>
    private const int BravoDeleted = 50;

    private FakeRedisDatabase redis = null!;
    private IDbContextFactory<TsContext> dbFactory = null!;
    private TsContext db = null!;
    private FakeHybridCache cache = null!;
    private FakeTimeProvider clock = null!;
    private TestSiteOperationsController controller = null!;

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        redis = new FakeRedisDatabase();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db = dbFactory.CreateDbContext();
        cache = new FakeHybridCache();
        clock = new FakeTimeProvider(Now);
        controller = new TestSiteOperationsController(loggerFactory.Object, dbFactory, redis.Mux.Object, cache, clock);

        // A site administrator who administers no organization at all, so anything they can see is
        // the role's doing.
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "ops@example.com"), new Claim(ClaimTypes.Role, Consts.SITE_ADMIN_ROLE)],
                    "TestAuthType")),
            },
        };
    }

    [TestCleanup]
    public void Cleanup() => db.Dispose();

    #region The gate

    /// <summary>
    /// The role requirement sits on the controller, so an action added later inherits it. What has to
    /// be watched is an action opted back out of it.
    /// </summary>
    [TestMethod]
    public void EveryEndpoint_RequiresTheSiteAdminRole()
    {
        var authorize = typeof(SiteOperationsControllerBase).GetCustomAttribute<AuthorizeAttribute>();
        Assert.IsNotNull(authorize, "The controller is not gated at all");
        Assert.AreEqual(Consts.SITE_ADMIN_ROLE, authorize.Roles);

        var routed = typeof(RedMist.EventManagement.Controllers.V1.SiteOperationsController);
        var anonymous = routed.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(SiteOperationsControllerBase).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();
        Assert.IsEmpty(anonymous, "Nothing on this page may be reachable without the role.");
    }

    /// <summary>The page is read-only, so every action is a GET and none writes.</summary>
    [TestMethod]
    public void EveryEndpoint_IsARead()
    {
        var actions = typeof(SiteOperationsControllerBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToList();

        CollectionAssert.AreEquivalent(
            new[]
            {
                nameof(SiteOperationsControllerBase.Overview), nameof(SiteOperationsControllerBase.Viewership),
                nameof(SiteOperationsControllerBase.OverallViewership),
            },
            actions.Select(a => a.Name).ToArray());
        Assert.IsTrue(actions.All(a => a.GetCustomAttribute<HttpGetAttribute>() is not null));
    }

    #endregion

    #region Seeding

    private async Task SeedAsync()
    {
        db.Organizations.AddRange(
            new Organization { Id = Alpha, ClientId = "relay-alpha", Name = "Alpha Racing", ShortName = "ALPHA" },
            new Organization { Id = Bravo, ClientId = "relay-bravo", Name = "Bravo Club", ShortName = "BRAVO" },
            new Organization { Id = Charlie, ClientId = "relay-charlie", Name = "Charlie Series", ShortName = "CHAR" });

        var alphaLive = NewEvent(AlphaLive, Alpha, "Alpha Night Race");
        alphaLive.IsLive = true;
        alphaLive.HideName = true;
        alphaLive.IsPrivate = true;
        alphaLive.TrackName = "Sebring";
        var alphaNoHeartbeat = NewEvent(AlphaNoHeartbeat, Alpha, "Alpha Practice");
        alphaNoHeartbeat.IsLive = true;
        var bravoQuiet = NewEvent(BravoQuiet, Bravo, "Bravo Sim");
        bravoQuiet.IsSimulation = true;
        bravoQuiet.TimingSource = RedMist.TimingCommon.Models.TimingSource.External;
        var charlieOrphan = NewEvent(CharlieOrphan, Charlie, "Charlie Old");
        charlieOrphan.IsArchived = true;
        var bravoDeleted = NewEvent(BravoDeleted, Bravo, "Bravo Deleted");
        bravoDeleted.IsLive = true;
        bravoDeleted.IsDeleted = true;
        db.Events.AddRange(alphaLive, alphaNoHeartbeat, bravoQuiet, charlieOrphan,
            NewEvent(CharlieIdle, Charlie, "Charlie Idle"), bravoDeleted);
        await db.SaveChangesAsync();

        SeedHeartbeat(AlphaLive, Alpha, "conn-a", Now.AddSeconds(-5), "3.1.4");
        SeedHeartbeat(BravoQuiet, Bravo, "conn-gone", Now.AddMinutes(-8), "3.0.0");
        SeedConnection("conn-a", "relay-alpha", Now.AddHours(-2));
        SeedConnection("conn-x", "relay-nobody", Now.AddHours(-1));
        // A field that does not parse costs its own row, not the page.
        redis.SeedHash(Consts.RELAY_EVENT_CONNECTIONS, string.Format(Consts.RELAY_CONNECTION, "bad"), "not json");

        SeedPods(new SitePodHealth
        {
            AsOfUtc = Now.AddSeconds(-3),
            Pods =
            [
                Pod("alpha-evt-10-event-processor-1", AlphaLive, Alpha, ready: true),
                Pod("char-evt-30-logger-1", CharlieOrphan, Charlie, ready: false, waiting: "CrashLoopBackOff"),
                Pod("redmist-status-api-1", eventId: null, organizationId: null, ready: true),
                // A finished job is not ready because there is nothing left to be ready for.
                Pod("migration-1", eventId: null, organizationId: null, ready: false, phase: "Succeeded"),
            ],
            MissingJobs = [new MissingJob { JobName = "alpha-evt-10-control-log", EventId = AlphaLive, Role = "control-log" }],
        });

        SeedViewers(AlphaLive, "Web", "iOS", "iOS");
        SeedViewers(BravoQuiet, "Android");
    }

    private static ConfigEvent NewEvent(int id, int orgId, string name) => new()
    {
        Id = id,
        OrganizationId = orgId,
        Name = name,
        StartDate = new DateTime(2026, 9, 1),
        EndDate = new DateTime(2026, 9, 2),
    };

    private void SeedHeartbeat(int eventId, int organizationId, string connectionId, DateTime at, string version) =>
        redis.SeedHash(Consts.RELAY_EVENT_CONNECTIONS, string.Format(Consts.RELAY_HEARTBEAT, eventId),
            JsonSerializer.Serialize(new RelayConnectionEventEntry
            {
                EventId = eventId, OrganizationId = organizationId, ConnectionId = connectionId, Timestamp = at, RelayVersion = version,
            }));

    private void SeedConnection(string connectionId, string clientId, DateTime at) =>
        redis.SeedHash(Consts.RELAY_EVENT_CONNECTIONS, string.Format(Consts.RELAY_CONNECTION, connectionId),
            JsonSerializer.Serialize(new RelayConnectionStatus { ConnectionId = connectionId, ClientId = clientId, ConnectedTimestamp = at }));

    private void SeedPods(SitePodHealth health) =>
        redis.SeedString(Consts.SITE_POD_HEALTH, JsonSerializer.Serialize(health, SitePodHealth.JsonOptions));

    private static PodHealth Pod(string name, int? eventId, int? organizationId, bool ready, string? waiting = null,
        string phase = "Running") => new()
    {
        PodName = name,
        EventId = eventId,
        OrganizationId = organizationId,
        Phase = phase,
        Ready = ready,
        ReadyContainers = ready ? 1 : 0,
        TotalContainers = 1,
        WaitingReason = waiting,
    };

    private void SeedViewers(int eventId, params string[] clientTypes)
    {
        for (var i = 0; i < clientTypes.Length; i++)
        {
            redis.SeedHash(string.Format(Consts.STATUS_EVENT_CONNECTIONS, eventId), $"viewer-{eventId}-{i}", clientTypes[i]);
        }
    }

    private static long CurrentMinute => RelayMessageCounter.UnixMinute(Now);

    private void SeedMessageTotal(int eventId, string type, long total, DateTime last)
    {
        var key = string.Format(Consts.RELAY_MESSAGE_COUNTS, eventId);
        redis.SeedHash(key, type, total.ToString());
        redis.SeedHash(key, type + RelayMessageCounter.LastSuffix, new DateTimeOffset(last).ToUnixTimeMilliseconds().ToString());
    }

    private void SeedMinute(int eventId, long minute, string type, long count) =>
        redis.SeedHash(string.Format(Consts.RELAY_MESSAGE_MINUTE, eventId, minute), type, count.ToString());

    #endregion

    #region Overview

    private async Task<SiteOperationsOverviewDto> OverviewAsync() => (await controller.Overview()).Value!;

    private static SiteOperationsEventDto Event(SiteOperationsOverviewDto overview, int id) =>
        overview.Events.Single(e => e.EventId == id);

    /// <summary>
    /// Active is the union of three things, and each matters: the live flag, a heartbeat still in
    /// Redis - which outlives the flag while a quiet relay waits out the teardown timeout - and pods
    /// with neither, which the orchestrator has yet to reap. Idle and deleted events are not listed.
    /// </summary>
    [TestMethod]
    public async Task Overview_ListsLiveHeartbeatingAndOrphanedEvents_ByOrganizationThenNewestFirst()
    {
        await SeedAsync();

        var overview = await OverviewAsync();

        CollectionAssert.AreEqual(new[] { AlphaNoHeartbeat, AlphaLive, BravoQuiet, CharlieOrphan },
            overview.Events.Select(e => e.EventId).ToArray());
        Assert.AreEqual(Now, overview.AsOfUtc);
        Assert.AreEqual(4, overview.Totals.Events);
    }

    /// <summary>
    /// A site administrator is the one person the page is for, so a hidden or private event's real
    /// name is shown - with its flags beside it so the page can say it is hidden.
    /// </summary>
    [TestMethod]
    public async Task Overview_ShowsTheRealNameOfAHiddenEventAndSaysItIsHidden()
    {
        await SeedAsync();

        var live = Event(await OverviewAsync(), AlphaLive);

        Assert.AreEqual("Alpha Night Race", live.EventName);
        Assert.IsTrue(live.HideName);
        Assert.IsTrue(live.IsPrivate);
        Assert.AreEqual("Alpha Racing", live.OrganizationName);
        Assert.AreEqual("ALPHA", live.OrganizationShortName);
        Assert.AreEqual("Sebring", live.TrackName);
        Assert.AreEqual("Relay", live.TimingSource);
        Assert.IsTrue(live.IsLiveInDb);
        Assert.IsFalse(live.IsOrphan);
    }

    [TestMethod]
    public async Task Overview_TheRelayIsMatchedToItsHeartbeatAndConnection()
    {
        await SeedAsync();
        var overview = await OverviewAsync();

        var live = Event(overview, AlphaLive).Relay;
        Assert.IsTrue(live.HasHeartbeat);
        Assert.AreEqual(Now.AddSeconds(-5), live.LastHeartbeatUtc);
        Assert.AreEqual(DateTimeKind.Utc, live.LastHeartbeatUtc!.Value.Kind);
        Assert.AreEqual("3.1.4", live.RelayVersion);
        Assert.AreEqual("conn-a", live.ConnectionId);
        Assert.IsTrue(live.ConnectionPresent);
        Assert.AreEqual(Now.AddHours(-2), live.ConnectedUtc);

        Assert.IsFalse(Event(overview, AlphaNoHeartbeat).Relay.HasHeartbeat);
        Assert.IsNull(Event(overview, AlphaNoHeartbeat).Relay.LastHeartbeatUtc);
    }

    /// <summary>
    /// The case the live flag alone would hide: a relay gone quiet whose heartbeat is still waiting out
    /// the teardown timeout, on a connection that has since dropped. The age is reported, not judged.
    /// </summary>
    [TestMethod]
    public async Task Overview_AQuietRelayStillAwaitingTeardown_IsListedWithItsStaleHeartbeat()
    {
        await SeedAsync();

        var quiet = Event(await OverviewAsync(), BravoQuiet);

        Assert.IsFalse(quiet.IsLiveInDb);
        Assert.IsFalse(quiet.IsOrphan, "A heartbeat makes it active in its own right.");
        Assert.IsTrue(quiet.Relay.HasHeartbeat);
        Assert.AreEqual(Now.AddMinutes(-8), quiet.Relay.LastHeartbeatUtc);
        Assert.IsFalse(quiet.Relay.ConnectionPresent);
        Assert.IsNull(quiet.Relay.ConnectedUtc);
        Assert.IsTrue(quiet.IsSimulation, "Simulations are included, and tagged.");
        Assert.AreEqual("External", quiet.TimingSource);
    }

    [TestMethod]
    public async Task Overview_PodsWithNoHeartbeatAndNoLiveFlag_AreAnOrphan_EvenWhenArchived()
    {
        await SeedAsync();

        var orphan = Event(await OverviewAsync(), CharlieOrphan);

        Assert.IsTrue(orphan.IsOrphan);
        Assert.IsTrue(orphan.IsArchived);
        Assert.AreEqual("Charlie Old", orphan.EventName);
        Assert.AreEqual("char-evt-30-logger-1", orphan.Pods.Single().PodName);
    }

    [TestMethod]
    public async Task Overview_PodsAreSplitIntoEventAndSharedAndUnhealthyOnesCounted()
    {
        await SeedAsync();

        var overview = await OverviewAsync();

        Assert.AreEqual("alpha-evt-10-event-processor-1", Event(overview, AlphaLive).Pods.Single().PodName);
        CollectionAssert.AreEquivalent(new[] { "redmist-status-api-1", "migration-1" },
            overview.SharedPods.Select(p => p.PodName).ToArray());
        Assert.AreEqual("alpha-evt-10-control-log", overview.MissingJobs.Single().JobName);
        Assert.AreEqual(Now.AddSeconds(-3), overview.PodsAsOfUtc);
        Assert.AreEqual(1, overview.Totals.UnhealthyPods,
            "Only the crash-looping pod; a finished job is not unhealthy for being not ready.");
    }

    /// <summary>
    /// No report from the orchestrator is "unknown", not "no pods": the page has to be able to tell
    /// the two apart, and an orphan can only be recognized from pods it can see.
    /// </summary>
    [TestMethod]
    public async Task Overview_WithNoPodReport_SaysThePodDataIsUnavailable()
    {
        await SeedAsync();
        redis.SeedString(Consts.SITE_POD_HEALTH, "");

        var overview = await OverviewAsync();

        Assert.IsNull(overview.PodsAsOfUtc);
        Assert.IsEmpty(overview.SharedPods);
        Assert.IsEmpty(overview.MissingJobs);
        Assert.AreEqual(0, overview.Totals.UnhealthyPods);
        Assert.IsFalse(overview.Events.Any(e => e.EventId == CharlieOrphan));
    }

    [TestMethod]
    public async Task Overview_AnUnreadablePodReport_IsTreatedAsNone()
    {
        await SeedAsync();
        redis.SeedString(Consts.SITE_POD_HEALTH, "{not json");

        var overview = await OverviewAsync();

        Assert.IsNull(overview.PodsAsOfUtc);
        Assert.HasCount(3, overview.Events);
    }

    [TestMethod]
    public async Task Overview_ViewersArePerEventAndSummed()
    {
        await SeedAsync();

        var overview = await OverviewAsync();

        var live = Event(overview, AlphaLive).Viewers!;
        Assert.AreEqual(3, live.Total);
        Assert.AreEqual(2, live.ByClientType["iOS"]);
        Assert.AreEqual(1, live.ByClientType["Web"]);
        Assert.AreEqual(0, Event(overview, AlphaNoHeartbeat).Viewers!.Total);

        Assert.AreEqual(4, overview.Totals.Viewers.Total);
        Assert.AreEqual(2, overview.Totals.Viewers.ByClientType["iOS"]);
        Assert.AreEqual(1, overview.Totals.Viewers.ByClientType["Web"]);
        Assert.AreEqual(1, overview.Totals.Viewers.ByClientType["Android"]);
    }

    /// <summary>
    /// Every relay hub connection is listed, mapped to its organization through its client id, with
    /// the events it is heartbeating - including one heartbeating nothing, which is either a relay with
    /// no event picked or a record left behind by a hub pod that died.
    /// </summary>
    [TestMethod]
    public async Task Overview_RelaysAreMappedToTheirOrganizationAndEvents()
    {
        await SeedAsync();

        var overview = await OverviewAsync();

        // The connection heartbeating nothing is listed but not counted: it may be a dead pod's record.
        Assert.AreEqual(1, overview.Totals.RelaysConnected);
        Assert.HasCount(2, overview.Relays);
        var alpha = overview.Relays[0];
        Assert.AreEqual("conn-a", alpha.ConnectionId);
        Assert.AreEqual("relay-alpha", alpha.ClientId);
        Assert.AreEqual(Alpha, alpha.OrganizationId);
        Assert.AreEqual("Alpha Racing", alpha.OrganizationName);
        Assert.AreEqual(Now.AddHours(-2), alpha.ConnectedUtc);
        CollectionAssert.AreEqual(new[] { AlphaLive }, alpha.HeartbeatEventIds);

        var unknown = overview.Relays[1];
        Assert.AreEqual("conn-x", unknown.ConnectionId);
        Assert.IsNull(unknown.OrganizationId);
        Assert.IsNull(unknown.OrganizationName);
        Assert.IsEmpty(unknown.HeartbeatEventIds);
    }

    /// <summary>
    /// Sixty complete minutes and the current one, oldest first, with the empty ones filled as zeros -
    /// and heartbeats in their own row but out of every total, because a relay whose timing feed has
    /// died still heartbeats.
    /// </summary>
    [TestMethod]
    public async Task Overview_MessagesAreTotaledAndBucketedByMinute_WithHeartbeatsKeptOut()
    {
        await SeedAsync();
        SeedMessageTotal(AlphaLive, RelayMessageTypes.RMonitor, 100, Now.AddSeconds(-2));
        SeedMessageTotal(AlphaLive, RelayMessageTypes.Passings, 40, Now.AddSeconds(-30));
        SeedMessageTotal(AlphaLive, RelayMessageTypes.Heartbeat, 500, Now.AddSeconds(-1));
        SeedMinute(AlphaLive, CurrentMinute, RelayMessageTypes.RMonitor, 5);
        SeedMinute(AlphaLive, CurrentMinute, RelayMessageTypes.Heartbeat, 6);
        SeedMinute(AlphaLive, CurrentMinute - 1, RelayMessageTypes.RMonitor, 30);
        SeedMinute(AlphaLive, CurrentMinute - 1, RelayMessageTypes.Passings, 10);
        SeedMinute(AlphaLive, CurrentMinute - 1, RelayMessageTypes.Heartbeat, 6);
        SeedMinute(AlphaLive, CurrentMinute - 60, RelayMessageTypes.RMonitor, 2);
        // Older than the window, so never read.
        SeedMinute(AlphaLive, CurrentMinute - 61, RelayMessageTypes.RMonitor, 99);

        var messages = Event(await OverviewAsync(), AlphaLive).Messages;

        Assert.AreEqual(140, messages.Total);
        Assert.AreEqual(Now.AddSeconds(-2), messages.LastMessageUtc);
        Assert.AreEqual(40, messages.PerMinuteLast);

        CollectionAssert.AreEqual(
            new[] { RelayMessageTypes.RMonitor, RelayMessageTypes.Passings, RelayMessageTypes.Heartbeat },
            messages.ByType.Select(t => t.Type).ToArray());
        var heartbeat = messages.ByType[2];
        Assert.AreEqual(500, heartbeat.Total);
        Assert.AreEqual(Now.AddSeconds(-1), heartbeat.LastUtc);
        Assert.AreEqual(6, heartbeat.PerMinuteLast);
        Assert.AreEqual(30, messages.ByType[0].PerMinuteLast);

        Assert.HasCount(61, messages.PerMinute);
        Assert.AreEqual(RelayMessageCounter.MinuteStartUtc(CurrentMinute - 60), messages.PerMinute[0].MinuteUtc);
        Assert.AreEqual(2, messages.PerMinute[0].Count);
        Assert.AreEqual(40, messages.PerMinute[59].Count);
        Assert.AreEqual(Now, messages.PerMinute[60].MinuteUtc, "The last entry is the current minute.");
        Assert.AreEqual(5, messages.PerMinute[60].Count);
        Assert.AreEqual(47, messages.PerMinute.Sum(m => m.Count), "Every other minute is a zero.");
    }

    /// <summary>
    /// Each event reads dozens of hashes; one timing out blanks that event's messages rather than
    /// failing the page, as an unreadable viewer count blanks only its viewers.
    /// </summary>
    [TestMethod]
    public async Task Overview_ARedisFailureReadingOneEventsMessages_BlanksOnlyThatEvent()
    {
        await SeedAsync();
        SeedMessageTotal(AlphaLive, RelayMessageTypes.RMonitor, 100, Now.AddSeconds(-2));
        SeedMessageTotal(BravoQuiet, RelayMessageTypes.RMonitor, 7, Now.AddMinutes(-8));
        redis.Db.Setup(x => x.HashGetAllAsync(
                (RedisKey)string.Format(Consts.RELAY_MESSAGE_MINUTE, AlphaLive, CurrentMinute - 5), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisTimeoutException("timed out", CommandStatus.Sent));

        var overview = await OverviewAsync();

        var alpha = Event(overview, AlphaLive).Messages;
        Assert.AreEqual(0, alpha.Total);
        Assert.IsEmpty(alpha.ByType);
        Assert.IsEmpty(alpha.PerMinute);
        Assert.AreEqual(7, Event(overview, BravoQuiet).Messages.Total);
        Assert.HasCount(4, overview.Events);
    }

    /// <summary>A pod report that cannot be read for a Redis failure is "unavailable", like a missing one.</summary>
    [TestMethod]
    public async Task Overview_ARedisFailureReadingThePodReport_IsTreatedAsNone()
    {
        await SeedAsync();
        redis.Db.Setup(x => x.StringGetAsync((RedisKey)Consts.SITE_POD_HEALTH, It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisTimeoutException("timed out", CommandStatus.Sent));

        var overview = await OverviewAsync();

        Assert.IsNull(overview.PodsAsOfUtc);
        Assert.IsEmpty(overview.MissingJobs);
        Assert.HasCount(3, overview.Events);
    }

    [TestMethod]
    public async Task Overview_AnEventTheRelayHasSentNothingFor_HasZerosRatherThanGaps()
    {
        await SeedAsync();

        var messages = Event(await OverviewAsync(), AlphaNoHeartbeat).Messages;

        Assert.AreEqual(0, messages.Total);
        Assert.IsNull(messages.LastMessageUtc);
        Assert.IsEmpty(messages.ByType);
        Assert.HasCount(61, messages.PerMinute);
        Assert.IsTrue(messages.PerMinute.All(m => m.Count == 0));
    }

    [TestMethod]
    public async Task Overview_NothingRunning_IsAnEmptyPageNotAnError()
    {
        var overview = await OverviewAsync();

        Assert.IsEmpty(overview.Events);
        Assert.AreEqual(0, overview.Totals.Events);
        Assert.AreEqual(0, overview.Totals.Viewers.Total);
        Assert.IsNull(overview.PodsAsOfUtc);
    }

    [TestMethod]
    public async Task Overview_IsServedFromTheCacheBetweenRefreshes()
    {
        await SeedAsync();

        await OverviewAsync();
        await OverviewAsync();

        Assert.HasCount(1, cache.FactoryInvocations);
    }

    /// <summary>
    /// Pins the field names the page was built against. A renamed or missing field fails here rather
    /// than as a blank card.
    /// </summary>
    [TestMethod]
    public async Task Overview_WireFormat_IsTheAgreedShape()
    {
        await SeedAsync();
        SeedMessageTotal(AlphaLive, RelayMessageTypes.RMonitor, 1, Now);

        var json = JsonSerializer.Serialize(await OverviewAsync(), LiveViewershipWireFormat.WebOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var live = root.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("eventId").GetInt32() == AlphaLive);

        CollectionAssert.AreEquivalent(
            new[] { "asOfUtc", "events", "totals", "sharedPods", "missingJobs", "podsAsOfUtc", "relays" },
            Names(root));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "eventId", "organizationId", "organizationName", "organizationShortName", "eventName", "trackName",
                "isPrivate", "hideName", "isSimulation", "isLiveInDb", "isArchived", "timingSource", "eventStartDate",
                "relay", "isOrphan", "pods", "viewers", "messages",
            },
            Names(live));
        CollectionAssert.AreEquivalent(
            new[] { "hasHeartbeat", "lastHeartbeatUtc", "relayVersion", "connectionId", "connectionPresent", "connectedUtc" },
            Names(live.GetProperty("relay")));
        CollectionAssert.AreEquivalent(new[] { "asOfUtc", "total", "byClientType" }, Names(live.GetProperty("viewers")));
        Assert.IsTrue(live.GetProperty("viewers").GetProperty("byClientType").TryGetProperty("iOS", out _),
            "Client types are keys as named, not camel-cased.");
        CollectionAssert.AreEquivalent(
            new[] { "total", "lastMessageUtc", "perMinuteLast", "byType", "perMinute" },
            Names(live.GetProperty("messages")));
        CollectionAssert.AreEquivalent(
            new[] { "type", "total", "lastUtc", "perMinuteLast" },
            Names(live.GetProperty("messages").GetProperty("byType")[0]));
        CollectionAssert.AreEquivalent(
            new[] { "minuteUtc", "count" },
            Names(live.GetProperty("messages").GetProperty("perMinute")[0]));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "podName", "namespace", "eventId", "organizationId", "appName", "serviceName", "phase", "ready",
                "readyContainers", "totalContainers", "restartCount", "waitingReason", "lastTerminatedReason",
                "lastTerminatedUtc", "startedUtc", "nodeName", "deleting",
            },
            Names(live.GetProperty("pods")[0]));
        CollectionAssert.AreEquivalent(
            new[] { "events", "viewers", "relaysConnected", "unhealthyPods" },
            Names(root.GetProperty("totals")));
        CollectionAssert.AreEquivalent(new[] { "jobName", "eventId", "role" }, Names(root.GetProperty("missingJobs")[0]));
        CollectionAssert.AreEquivalent(
            new[] { "connectionId", "clientId", "organizationId", "organizationName", "connectedUtc", "heartbeatEventIds" },
            Names(root.GetProperty("relays")[0]));

        // Every instant carries its Z. The event's start date is left out of that: it is a calendar
        // date as the organizer entered it, not an instant, exactly as on the viewership reports.
        LiveViewershipWireFormat.AssertEveryTimestampHasZ(
            System.Text.RegularExpressions.Regex.Replace(json, "\"eventStartDate\":\"[^\"]*\"", ""));

        static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];
    }

    #endregion

    #region Viewership

    private async Task SeedLiveViewershipAsync(int eventId)
    {
        db.Sessions.Add(new Session
        {
            Id = 1, EventId = eventId, Name = "Race", StartTime = new DateTime(2026, 9, 1, 15, 0, 0), IsLive = true,
        });
        db.EventViewerSessions.AddRange(
            NewViewer(eventId, new DateTime(2026, 9, 1, 15, 0, 0), new DateTime(2026, 9, 1, 15, 30, 0)),
            NewViewer(eventId, new DateTime(2026, 9, 1, 15, 10, 0), null));
        await db.SaveChangesAsync();
    }

    private static EventViewerSession NewViewer(int eventId, DateTime start, DateTime? end) => new()
    {
        EventId = eventId,
        ConnectionId = Guid.NewGuid().ToString(),
        ClientType = "Web",
        StartUtc = start,
        EndUtc = end,
    };

    /// <summary>
    /// The organization check is the one difference from the organizer dashboard's read: this caller
    /// administers no organization at all, and reads any event's viewership.
    /// </summary>
    [TestMethod]
    public async Task Viewership_AnyOrganizationsEvent_IsReadWithoutMembership()
    {
        await SeedAsync();
        await SeedLiveViewershipAsync(AlphaLive);

        var live = (await controller.Viewership(AlphaLive)).Value!;

        Assert.AreEqual(AlphaLive, live.EventId);
        Assert.AreEqual(Now, live.AsOfUtc);
        Assert.AreEqual(60, live.BucketSeconds);
        Assert.AreEqual("Race", live.Sessions.Single().SessionName);
        Assert.IsNull(live.Sessions[0].EndUtc, "The race is still running.");
        Assert.AreEqual((30 + 50) * 60, live.Event.TotalViewerSeconds);
        Assert.AreEqual(2, live.Event.MaxConcurrent);
    }

    [TestMethod]
    public async Task Viewership_ADeletedOrMissingEvent_IsNotFound()
    {
        await SeedAsync();

        Assert.IsInstanceOfType<NotFoundResult>((await controller.Viewership(BravoDeleted)).Result);
        Assert.IsInstanceOfType<NotFoundResult>((await controller.Viewership(999)).Result);
        Assert.IsEmpty(cache.FactoryInvocations);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task Viewership_AnEventIdThatCannotExist_IsRefused(int eventId)
    {
        Assert.IsInstanceOfType<BadRequestObjectResult>((await controller.Viewership(eventId)).Result);
    }

    #endregion

    #region Overall viewership

    private async Task<OverallViewershipDto> OverallAsync() => (await controller.OverallViewership()).Value!;

    /// <summary>
    /// Swept together, not summed: three people watching three events at once is a peak of three at
    /// that moment. Viewers of an idle event and of an orphan are not part of what is running.
    /// </summary>
    [TestMethod]
    public async Task OverallViewership_SumsConcurrencyAcrossTheActiveEvents()
    {
        await SeedAsync();
        static DateTime At(int hour, int minute) => new(2026, 9, 1, hour, minute, 0);
        db.EventViewerSessions.AddRange(
            NewViewer(AlphaLive, At(15, 0), At(15, 30)),
            NewViewer(AlphaLive, At(15, 10), At(15, 20)),
            NewViewer(BravoQuiet, At(15, 15), At(15, 45)),
            NewViewer(CharlieIdle, At(15, 15), At(15, 16)),
            NewViewer(CharlieOrphan, At(15, 15), At(15, 16)));
        await db.SaveChangesAsync();

        var overall = await OverallAsync();

        CollectionAssert.AreEqual(new[] { AlphaLive, AlphaNoHeartbeat, BravoQuiet }, overall.EventIds);
        Assert.AreEqual(60, overall.BucketSeconds);
        Assert.AreEqual(Now, overall.AsOfUtc);
        Assert.AreEqual(new DateTime(2026, 9, 1, 15, 0, 0, DateTimeKind.Utc), overall.WindowStartUtc);
        Assert.AreEqual(3, overall.MaxConcurrent);
        Assert.AreEqual(new DateTime(2026, 9, 1, 15, 15, 0, DateTimeKind.Utc), overall.PeakUtc);
        Assert.HasCount(60, overall.Buckets);
        Assert.AreEqual(1, overall.Buckets.Single(b => b.StartUtc == At(15, 5)).Max);
        Assert.AreEqual(2, overall.Buckets.Single(b => b.StartUtc == At(15, 12)).Max);
        Assert.AreEqual(1, overall.Buckets.Single(b => b.StartUtc == At(15, 40)).Max);
        Assert.AreEqual(0, overall.Buckets.Single(b => b.StartUtc == At(15, 50)).Max);
        Assert.IsTrue(overall.Buckets.All(b => b.StartUtc.Kind == DateTimeKind.Utc));
    }

    [TestMethod]
    public async Task OverallViewership_NothingRunning_HasNoBuckets()
    {
        var overall = await OverallAsync();

        Assert.IsEmpty(overall.EventIds);
        Assert.IsEmpty(overall.Buckets);
        Assert.AreEqual(0, overall.MaxConcurrent);
        Assert.IsNull(overall.PeakUtc);
        Assert.AreEqual(Now, overall.WindowStartUtc);
    }

    /// <summary>
    /// An event live for days would otherwise send days of minutes on every poll. The window starts no
    /// earlier than a day ago, on a whole minute.
    /// </summary>
    [TestMethod]
    public async Task OverallViewership_ReachesBackNoMoreThanADay()
    {
        await SeedAsync();
        var longRunning = db.Events.Single(e => e.Id == AlphaLive);
        longRunning.StartDate = new DateTime(2026, 8, 30);
        db.EventViewerSessions.Add(NewViewer(AlphaLive, Now.AddHours(-30), null));
        await db.SaveChangesAsync();

        var overall = await OverallAsync();

        Assert.AreEqual(Now.AddHours(-24), overall.WindowStartUtc);
        Assert.HasCount(24 * 60, overall.Buckets);
        Assert.AreEqual(1, overall.Buckets[0].Max);
    }

    /// <summary>
    /// The day clamp lands on a whole minute even when the clock does not, so every bucket starts on
    /// a minute boundary.
    /// </summary>
    [TestMethod]
    public async Task OverallViewership_DayClampIsRoundedUpToAWholeMinute()
    {
        clock.SetUtcNow(Now.AddSeconds(45));
        await SeedAsync();
        db.Events.Single(e => e.Id == AlphaLive).StartDate = new DateTime(2026, 8, 30);
        db.EventViewerSessions.Add(NewViewer(AlphaLive, Now.AddHours(-30), null));
        await db.SaveChangesAsync();

        var overall = await OverallAsync();

        Assert.AreEqual(Now.AddSeconds(45), overall.AsOfUtc);
        Assert.AreEqual(Now.AddHours(-24).AddMinutes(1), overall.WindowStartUtc, "16:01:00, not 16:00:45");
        Assert.IsTrue(overall.Buckets.All(b => b.StartUtc.Second == 0 && b.StartUtc.Millisecond == 0));
        Assert.AreEqual(Now.AddHours(-24).AddMinutes(1), overall.Buckets[0].StartUtc);
    }

    /// <summary>
    /// Rows that ended before the day clamp are not read, because they cannot reach any bucket - but
    /// they still say the event's window began before it, so the series starts at the clamp rather
    /// than at the first row still in range.
    /// </summary>
    [TestMethod]
    public async Task OverallViewership_RowsEndedBeforeTheDayClamp_StillStartTheSeriesAtTheClamp()
    {
        await SeedAsync();
        db.Events.Single(e => e.Id == AlphaLive).StartDate = new DateTime(2026, 8, 30);
        db.EventViewerSessions.AddRange(
            NewViewer(AlphaLive, Now.AddHours(-30), Now.AddHours(-29)),
            NewViewer(AlphaLive, Now.AddHours(-2), Now.AddHours(-1)));
        await db.SaveChangesAsync();

        var overall = await OverallAsync();

        Assert.AreEqual(Now.AddHours(-24), overall.WindowStartUtc);
        Assert.HasCount(24 * 60, overall.Buckets);
        Assert.AreEqual(0, overall.Buckets[0].Max, "The row that ended before the clamp adds nothing.");
        Assert.AreEqual(1, overall.MaxConcurrent);
        Assert.AreEqual(Now.AddHours(-2), overall.PeakUtc);
    }

    /// <summary>
    /// A row before the clamp that the plausibility bounds would have dropped anyway - here, before
    /// the event's start date - does not drag the series back to the clamp.
    /// </summary>
    [TestMethod]
    public async Task OverallViewership_ImplausibleRowsBeforeTheDayClamp_AreIgnored()
    {
        await SeedAsync();
        db.EventViewerSessions.AddRange(
            NewViewer(AlphaLive, Now.AddDays(-5), Now.AddDays(-5).AddHours(1)),
            NewViewer(AlphaLive, new DateTime(2026, 9, 1, 15, 0, 0), new DateTime(2026, 9, 1, 15, 30, 0)));
        await db.SaveChangesAsync();

        var overall = await OverallAsync();

        Assert.AreEqual(new DateTime(2026, 9, 1, 15, 0, 0, DateTimeKind.Utc), overall.WindowStartUtc);
    }

    [TestMethod]
    public async Task OverallViewership_IsCached()
    {
        await SeedAsync();

        await OverallAsync();
        await OverallAsync();

        Assert.HasCount(1, cache.FactoryInvocations);
    }

    #endregion

    #region Readings

    [TestMethod]
    public void BuildMessages_ToleratesJunkAndOrdersUnknownTypesLast()
    {
        HashEntry[] totals =
        [
            new("zeta", "3"),
            new(RelayMessageTypes.Flags, "not a number"),
            new(RelayMessageTypes.RMonitor, "-5"),
            new(RelayMessageTypes.RMonitor + RelayMessageCounter.LastSuffix, "garbage"),
        ];

        var messages = SiteOperationsReadings.BuildMessages(totals, [[], []], 100);

        CollectionAssert.AreEqual(new[] { RelayMessageTypes.RMonitor, RelayMessageTypes.Flags, "zeta" },
            messages.ByType.Select(t => t.Type).ToArray());
        Assert.AreEqual(0, messages.ByType[0].Total, "A negative count is read as zero.");
        Assert.IsNull(messages.ByType[0].LastUtc);
        Assert.AreEqual(0, messages.ByType[1].Total);
        Assert.AreEqual(3, messages.Total);
        Assert.HasCount(2, messages.PerMinute);
    }

    [TestMethod]
    [DataRow(true, "Running", null, false)]
    [DataRow(false, "Running", null, true)]
    [DataRow(false, "Pending", null, true)]
    [DataRow(false, "Succeeded", null, false)]
    [DataRow(true, "Running", "CrashLoopBackOff", true)]
    public void IsUnhealthy(bool ready, string phase, string? waiting, bool expected)
    {
        var pod = new PodHealth { Ready = ready, Phase = phase, WaitingReason = waiting };

        Assert.AreEqual(expected, SiteOperationsReadings.IsUnhealthy(pod));
    }

    #endregion

    private sealed class TestSiteOperationsController(ILoggerFactory loggerFactory, IDbContextFactory<TsContext> tsContext,
        IConnectionMultiplexer cacheMux, HybridCache hcache, TimeProvider clock)
        : SiteOperationsControllerBase(loggerFactory, tsContext, cacheMux, hcache, clock);
}
