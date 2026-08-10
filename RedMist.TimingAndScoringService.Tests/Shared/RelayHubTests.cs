using BigMission.TestHelpers.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;
using System.Security.Claims;
using System.Text.Json;
using Event = RedMist.TimingCommon.Models.Configuration.Event;
using Session = RedMist.TimingCommon.Models.Session;

namespace RedMist.TimingAndScoringService.Tests.Shared;

/// <summary>
/// Covers the relay-facing hub: who is allowed to speak for an organization, what ends up on the
/// event stream, and the group bookkeeping that decides which relay gets broadcast messages.
/// </summary>
/// <remarks>
/// <see cref="RelayHub"/> keeps its group membership in a process-wide static dictionary, so every
/// test allocates its own event id and connection id and only ever asserts on those. Nothing here
/// asserts on the Prometheus gauges for the same reason.
/// </remarks>
[TestClass]
public class RelayHubTests
{
    private static int nextEventId = 500_000;

    /// <summary>Hands out an event id no other test in this assembly uses.</summary>
    private static int NewEventId() => Interlocked.Increment(ref nextEventId);

    private const string ClientId = "relay-client";

    private FakeRedisDatabase redis = null!;
    private IDbContextFactory<TsContext> dbFactory = null!;
    private FakeHybridCache hcache = null!;
    private Mock<IGroupManager> groups = null!;
    private string connectionId = null!;

    [TestInitialize]
    public void Setup()
    {
        redis = new FakeRedisDatabase();
        hcache = new FakeHybridCache();
        groups = new Mock<IGroupManager>();
        connectionId = $"conn-{Guid.NewGuid()}";
        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"RelayHubTests_{Guid.NewGuid()}")
            .Options;
        dbFactory = new TestDbContextFactory(options);
    }

    private RelayHub CreateHub(ClaimsPrincipal? user = null, IDbContextFactory<TsContext>? factory = null)
    {
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns(user ?? PrincipalFor(ClientId));

        return new RelayHub(new DebugLoggerFactory(), redis.Mux.Object, factory ?? dbFactory, hcache)
        {
            Context = context.Object,
            Groups = groups.Object,
        };
    }

    private static ClaimsPrincipal PrincipalFor(string clientId)
        => new(new ClaimsIdentity([new Claim("azp", clientId)], "test"));

    private async Task<int> SeedOrganizationAsync(string clientId = ClientId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var org = new Organization { ClientId = clientId, Name = "Test Org", ShortName = "TO" };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        return org.Id;
    }

    private async Task SeedEventAsync(int eventId, int organizationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Events.Add(new Event
        {
            Id = eventId,
            OrganizationId = organizationId,
            Name = $"Event {eventId}",
            StartDate = DateTime.UtcNow.AddHours(-1),
            EndDate = DateTime.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();
    }

    #region Connection lifecycle

    [TestMethod]
    public async Task OnConnectedAsync_RecordsTheConnectionAgainstItsClientId()
    {
        var hub = CreateHub();

        await hub.OnConnectedAsync();

        var field = string.Format(Consts.RELAY_CONNECTION, connectionId);
        var json = redis.GetHashValue(Consts.RELAY_EVENT_CONNECTIONS, field);
        Assert.IsNotNull(json, "the relay connection must be discoverable by other services");
        var entry = JsonSerializer.Deserialize<RelayConnectionStatus>(json)!;
        Assert.AreEqual(ClientId, entry.ClientId);
        Assert.AreEqual(connectionId, entry.ConnectionId);
    }

    /// <summary>
    /// An unauthenticated socket must not be able to register itself as a relay.
    /// </summary>
    [TestMethod]
    public async Task OnConnectedAsync_WithoutAUser_RecordsNothing()
    {
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns((ClaimsPrincipal?)null);
        var hub = new RelayHub(new DebugLoggerFactory(), redis.Mux.Object, dbFactory, hcache)
        {
            Context = context.Object,
            Groups = groups.Object,
        };

        await hub.OnConnectedAsync();

        Assert.IsEmpty(redis.HashWrites);
    }

    /// <summary>
    /// Nothing a relay sends may reach an event stream or the database without an authenticated
    /// client behind it, since the client id is what the organization is resolved from.
    /// </summary>
    [TestMethod]
    public async Task WithoutAUser_NoRelayMessageIsAccepted()
    {
        await SeedOrganizationAsync();
        var eventId = NewEventId();
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns((ClaimsPrincipal?)null);
        var hub = new RelayHub(new DebugLoggerFactory(), redis.Mux.Object, dbFactory, hcache)
        {
            Context = context.Object,
            Groups = groups.Object,
        };

        await hub.SendHeartbeat(eventId, "3.1.4");
        await hub.SendSessionChange(eventId, 1, "Race", 0);
        await hub.SendPassings(eventId, 1, [new Passing { Id = 1 }]);
        await hub.SendLoopChange(eventId, [new Loop { Id = 1 }]);
        await hub.SendFlags(eventId, 1, [new FlagDuration { Flag = Flags.Green }]);
        await hub.SendCompetitorMetadata(eventId, [new CompetitorMetadata { EventId = eventId, CarNumber = "42" }]);
        await hub.SaveLoggerMessage(DateTime.UtcNow, "Error", "boom", "");
        await hub.OnDisconnectedAsync(null);

        Assert.IsEmpty(redis.StreamWrites);
        Assert.IsEmpty(redis.HashWrites);
        Assert.IsEmpty(redis.HashDeletes);
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.IsFalse(await db.Sessions.AnyAsync());
        Assert.IsFalse(await db.RelayLogs.AnyAsync());
        Assert.IsFalse(await db.CompetitorMetadata.AnyAsync());
    }

    [TestMethod]
    public async Task OnDisconnectedAsync_RemovesTheConnectionRecord()
    {
        var hub = CreateHub();
        await hub.OnConnectedAsync();

        await hub.OnDisconnectedAsync(null);

        var field = string.Format(Consts.RELAY_CONNECTION, connectionId);
        Assert.Contains((Consts.RELAY_EVENT_CONNECTIONS, field), redis.HashDeletes);
    }

    /// <summary>
    /// <c>RelayHub</c> reads the azp claim with First(), not FirstOrDefault(), so a token that
    /// authenticates but carries no azp claim faults the hub method rather than being ignored the
    /// way a missing user is. <c>StatusHub</c> uses FirstOrDefault for the same claim.
    /// </summary>
    [TestMethod]
    public async Task OnConnectedAsync_UserWithoutAnAzpClaim_Throws()
    {
        var hub = CreateHub(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "someone")], "test")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.OnConnectedAsync());
    }

    #endregion

    #region Timing feeds

    [TestMethod]
    public async Task SendRMonitor_ForwardsTheCommandVerbatimToTheEventStream()
    {
        var eventId = NewEventId();
        var hub = CreateHub();

        await hub.SendRMonitor(eventId, 7, "$F,14,\"00:12:45\"\r\n");

        var write = redis.StreamWrites.Single();
        Assert.AreEqual(string.Format(Consts.EVENT_STATUS_STREAM_KEY, eventId), write.Key);
        Assert.AreEqual(string.Format(Consts.EVENT_RMON_STREAM_FIELD, eventId, 7), write.Field);
        // The stripped copy exists only for the trace log; consumers get the original framing.
        Assert.AreEqual("$F,14,\"00:12:45\"\r\n", write.Value);
        Assert.AreEqual((long)Consts.EVENT_STATUS_STREAM_MAX_LENGTH, write.MaxLength!.Value);
        Assert.IsTrue(write.Approximate, "an exact MAXLEN would make every write O(n)");
    }

    [TestMethod]
    public async Task SendMultiloop_UsesTheMultiloopStreamField()
    {
        var eventId = NewEventId();
        var hub = CreateHub();

        await hub.SendMultiloop(eventId, 3, "$C§1§...");

        Assert.AreEqual(string.Format(Consts.EVENT_MULTILOOP_STREAM_FIELD, eventId, 3), redis.StreamWrites.Single().Field);
    }

    [TestMethod]
    public async Task SendFlagtronics_UsesTheFlagtronicsStreamField()
    {
        var eventId = NewEventId();
        var hub = CreateHub();

        await hub.SendFlagtronics(eventId, 9, "[{\"car\":\"42\"}]");

        var write = redis.StreamWrites.Single();
        Assert.AreEqual(string.Format(Consts.EVENT_FLAGTRONICS_STREAM_FIELD, eventId, 9), write.Field);
        Assert.AreEqual("[{\"car\":\"42\"}]", write.Value);
    }

    /// <summary>
    /// A relay that has not picked an event yet sends 0. Nothing may be written for it, or the data
    /// would land on a stream no event processor owns.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task TimingFeeds_WithNoSelectedEvent_WriteNothing(int eventId)
    {
        var hub = CreateHub();

        await hub.SendRMonitor(eventId, 1, "$F,14");
        await hub.SendMultiloop(eventId, 1, "$C");
        await hub.SendFlagtronics(eventId, 1, "[]");

        Assert.IsEmpty(redis.StreamWrites);
        groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Relay group membership

    [TestMethod]
    public async Task SendRMonitor_AddsTheConnectionToTheEventRelayGroupOnlyOnce()
    {
        var eventId = NewEventId();
        var groupName = string.Format(Consts.RELAY_GROUP_PREFIX, eventId);
        var hub = CreateHub();

        await hub.SendRMonitor(eventId, 1, "$F,1");
        await hub.SendRMonitor(eventId, 1, "$F,2");
        await hub.SendMultiloop(eventId, 1, "$C");

        groups.Verify(g => g.AddToGroupAsync(connectionId, groupName, It.IsAny<CancellationToken>()), Times.Once,
            "the local tracker exists to keep every message from hitting the Redis backplane");
    }

    /// <summary>
    /// Disconnecting has to drop the connection from the tracker, otherwise a relay that reconnects
    /// with the same id would never be re-added to the group and would silently stop receiving
    /// broadcasts.
    /// </summary>
    [TestMethod]
    public async Task AfterDisconnect_TheNextMessageRejoinsTheRelayGroup()
    {
        var eventId = NewEventId();
        var groupName = string.Format(Consts.RELAY_GROUP_PREFIX, eventId);
        var hub = CreateHub();

        await hub.SendRMonitor(eventId, 1, "$F,1");
        await hub.OnDisconnectedAsync(null);
        await hub.SendRMonitor(eventId, 1, "$F,2");

        groups.Verify(g => g.AddToGroupAsync(connectionId, groupName, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task RemoveRelayConnectionFromAllGroups_LeavesOtherConnectionsInTheGroup()
    {
        var eventId = NewEventId();
        var groupName = string.Format(Consts.RELAY_GROUP_PREFIX, eventId);
        var otherConnectionId = $"conn-{Guid.NewGuid()}";

        var first = CreateHub();
        await first.SendRMonitor(eventId, 1, "$F,1");

        connectionId = otherConnectionId;
        var second = CreateHub();
        await second.SendRMonitor(eventId, 1, "$F,1");

        RelayHub.RemoveRelayConnectionFromAllGroups(otherConnectionId);
        await second.SendRMonitor(eventId, 1, "$F,2");

        groups.Verify(g => g.AddToGroupAsync(otherConnectionId, groupName, It.IsAny<CancellationToken>()), Times.Exactly(2));
        groups.Verify(g => g.AddToGroupAsync(first.Context.ConnectionId, groupName, It.IsAny<CancellationToken>()), Times.Once,
            "removing one connection must not evict the other relay from the group");
    }

    #endregion

    #region Heartbeat

    [TestMethod]
    public async Task SendHeartbeat_PublishesTheRelayVersionAndOrganizationForTheEvent()
    {
        var orgId = await SeedOrganizationAsync();
        var eventId = NewEventId();
        var hub = CreateHub();

        await hub.SendHeartbeat(eventId, "3.1.4");

        var json = redis.GetHashValue(Consts.RELAY_EVENT_CONNECTIONS, string.Format(Consts.RELAY_HEARTBEAT, eventId));
        Assert.IsNotNull(json);
        var entry = JsonSerializer.Deserialize<RelayConnectionEventEntry>(json)!;
        Assert.AreEqual(eventId, entry.EventId);
        Assert.AreEqual(orgId, entry.OrganizationId);
        Assert.AreEqual("3.1.4", entry.RelayVersion);
        Assert.AreEqual(connectionId, entry.ConnectionId);

        var logWrite = redis.StreamWrites.Single();
        Assert.AreEqual(string.Format(Consts.EVENT_PROCESSOR_LOGGING_STREAM_KEY, eventId), logWrite.Key);
        Assert.AreEqual(Consts.RELAY_HEARTBEAT_TYPE, logWrite.Field);
    }

    [TestMethod]
    public async Task SendHeartbeatV2_WithNothingCached_ReturnsEmptyTelemetryRatherThanNull()
    {
        var eventId = NewEventId();
        var hub = CreateHub();

        var telemetry = await hub.SendHeartbeatV2(eventId, "3.1.4");

        Assert.IsEmpty(telemetry.ServiceStatuses);
        Assert.IsEmpty(telemetry.EventConnections);
    }

    [TestMethod]
    public async Task SendHeartbeatV2_ReturnsTheCachedServiceStatuses()
    {
        var eventId = NewEventId();
        redis.SeedString(string.Format(Consts.EVENT_SERVICE_STATUSES, eventId), JsonSerializer.Serialize(new List<ServiceStatus>
        {
            new() { ServiceName = "event-processor", Status = "Healthy" },
            new() { ServiceName = "control-log", Status = "Degraded" },
        }));
        var hub = CreateHub();

        var telemetry = await hub.SendHeartbeatV2(eventId, "3.1.4");

        Assert.HasCount(2, telemetry.ServiceStatuses);
        Assert.AreEqual("Healthy", telemetry.ServiceStatuses.Single(s => s.ServiceName == "event-processor").Status);
    }

    /// <summary>
    /// The per-event connection hash used to hold timestamps and now holds a client type. Anything
    /// that is not one of the four known types — including the old timestamps and, because the known
    /// set is case sensitive, a differently cased type — is counted as a web client.
    /// </summary>
    [TestMethod]
    public async Task SendHeartbeatV2_CountsConnectionsByClientTypeAndFoldsUnknownValuesIntoWeb()
    {
        var eventId = NewEventId();
        var connKey = string.Format(Consts.STATUS_EVENT_CONNECTIONS, eventId);
        redis.SeedHash(connKey, "c1", "iOS");
        redis.SeedHash(connKey, "c2", "iOS");
        redis.SeedHash(connKey, "c3", "Android");
        redis.SeedHash(connKey, "c4", "Web");
        redis.SeedHash(connKey, "c5", "638000000000000000");  // legacy timestamp entry
        redis.SeedHash(connKey, "c6", "ios");                 // not in the known set as written
        var hub = CreateHub();

        var telemetry = await hub.SendHeartbeatV2(eventId, "3.1.4");

        var counts = telemetry.EventConnections.ToDictionary(c => c.ClientApplication, c => c.Clients);
        Assert.AreEqual(2, counts["iOS"]);
        Assert.AreEqual(1, counts["Android"]);
        Assert.AreEqual(3, counts["Web"]);
        Assert.HasCount(3, telemetry.EventConnections);
    }

    #endregion

    #region Session registration

    [TestMethod]
    public async Task SendSessionChange_ForAnEventTheClientDoesNotOwn_RegistersNothing()
    {
        var otherOrgId = await SeedOrganizationAsync("some-other-relay");
        var eventId = NewEventId();
        await SeedEventAsync(eventId, otherOrgId);
        var hub = CreateHub();

        await hub.SendSessionChange(eventId, 5, "Race", -5);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.IsFalse(await db.Sessions.AnyAsync(), "a relay must not be able to create sessions on another org's event");
        Assert.IsEmpty(redis.StreamWrites);
    }

    [TestMethod]
    public async Task SendSessionChange_ForANewSession_PersistsItAndPublishesItToTheEventStream()
    {
        var orgId = await SeedOrganizationAsync();
        var eventId = NewEventId();
        await SeedEventAsync(eventId, orgId);
        var hub = CreateHub();

        await hub.SendSessionChange(eventId, 5, "Group 3 Qualifying", -5);

        await using var db = await dbFactory.CreateDbContextAsync();
        var stored = await db.Sessions.SingleAsync();
        Assert.AreEqual(5, stored.Id);
        Assert.AreEqual(eventId, stored.EventId);
        Assert.IsTrue(stored.IsLive);
        Assert.AreEqual(-5d, stored.LocalTimeZoneOffset);
        Assert.IsTrue(stored.IsPracticeQualifying, "the session name classifies it as qualifying");

        var write = redis.StreamWrites.Single();
        Assert.AreEqual(string.Format(Consts.EVENT_SESSION_CHANGED, eventId, 5), write.Field);
        Assert.AreEqual(5, JsonSerializer.Deserialize<Session>(write.Value)!.Id);
    }

    [TestMethod]
    public async Task SendSessionChange_ForARaceSession_IsNotMarkedPracticeOrQualifying()
    {
        var orgId = await SeedOrganizationAsync();
        var eventId = NewEventId();
        await SeedEventAsync(eventId, orgId);
        var hub = CreateHub();

        await hub.SendSessionChange(eventId, 6, "Enduro Race", 0);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.IsFalse((await db.Sessions.SingleAsync()).IsPracticeQualifying);
    }

    /// <summary>
    /// A relay reconnecting mid-session replays the session change. The existing row must survive
    /// untouched — rewriting it would reset the start time and the name the session ran under.
    /// </summary>
    [TestMethod]
    public async Task SendSessionChange_ForAKnownSession_LeavesTheStoredSessionAlone()
    {
        var orgId = await SeedOrganizationAsync();
        var eventId = NewEventId();
        await SeedEventAsync(eventId, orgId);
        var originalStart = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            seed.Sessions.Add(new Session { Id = 5, EventId = eventId, Name = "Race", StartTime = originalStart, IsLive = true });
            await seed.SaveChangesAsync();
        }
        var hub = CreateHub();

        await hub.SendSessionChange(eventId, 5, "Renamed By Relay", 3);

        await using var db = await dbFactory.CreateDbContextAsync();
        var stored = await db.Sessions.SingleAsync();
        Assert.AreEqual("Race", stored.Name);
        Assert.AreEqual(originalStart, stored.StartTime);
        Assert.AreEqual(0d, stored.LocalTimeZoneOffset);
        Assert.HasCount(1, redis.StreamWrites, "the existing session is still republished so the processor picks it up");
    }

    [TestMethod]
    public async Task SendSessionChange_WhenTheDatabaseIsUnavailable_DoesNotFaultTheConnection()
    {
        var hub = CreateHub(factory: new ThrowingDbContextFactory());

        await hub.SendSessionChange(NewEventId(), 5, "Race", 0);

        Assert.IsEmpty(redis.StreamWrites);
    }

    #endregion

    #region X2 passings, loops and flags

    [TestMethod]
    public async Task SendPassings_StampsTheOrganizationAndSplitsIntoChunksOf25()
    {
        var orgId = await SeedOrganizationAsync();
        var eventId = NewEventId();
        var hub = CreateHub();
        var passings = Enumerable.Range(1, 60).Select(i => new Passing { Id = (uint)i }).ToList();

        await hub.SendPassings(eventId, 2, passings);

        Assert.HasCount(3, redis.StreamWrites);
        var chunkSizes = redis.StreamWrites
            .Select(w => JsonSerializer.Deserialize<List<Passing>>(w.Value)!.Count)
            .ToList();
        CollectionAssert.AreEqual(new[] { 25, 25, 10 }, chunkSizes);
        Assert.IsTrue(passings.All(p => p.OrganizationId == orgId), "the relay's org is the only trusted source of this");
        Assert.AreEqual(string.Format(Consts.EVENT_X2_PASSINGS_STREAM_FIELD, eventId, 2), redis.StreamWrites[0].Field);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(25, 1)]
    [DataRow(26, 2)]
    public async Task SendPassings_ChunkBoundaries(int passingCount, int expectedWrites)
    {
        await SeedOrganizationAsync();
        var hub = CreateHub();

        await hub.SendPassings(NewEventId(), 1, [.. Enumerable.Range(1, passingCount).Select(i => new Passing { Id = (uint)i })]);

        Assert.HasCount(expectedWrites, redis.StreamWrites);
    }

    [TestMethod]
    public async Task SendLoopChange_StampsTheOrganizationAndPublishesTheWholeSetAtOnce()
    {
        var orgId = await SeedOrganizationAsync();
        var eventId = NewEventId();
        var hub = CreateHub();
        var loops = new List<Loop> { new() { Id = 1, Name = "S/F" }, new() { Id = 2, Name = "Pit In" } };

        await hub.SendLoopChange(eventId, loops);

        var write = redis.StreamWrites.Single();
        Assert.AreEqual(string.Format(Consts.EVENT_X2_LOOPS_STREAM_FIELD, eventId), write.Field);
        Assert.IsTrue(loops.All(l => l.OrganizationId == orgId));
        Assert.HasCount(2, JsonSerializer.Deserialize<List<Loop>>(write.Value)!);
    }

    [TestMethod]
    public async Task SendFlags_PublishesTheFlagsForTheSession()
    {
        var eventId = NewEventId();
        var hub = CreateHub();
        var flags = new List<FlagDuration> { new() { Flag = Flags.Green, StartTime = DateTime.UtcNow } };

        await hub.SendFlags(eventId, 4, flags);

        var write = redis.StreamWrites.Single();
        Assert.AreEqual(string.Format(Consts.EVENT_FLAGS_STREAM_FIELD, eventId, 4), write.Field);
        Assert.AreEqual(Flags.Green, JsonSerializer.Deserialize<List<FlagDuration>>(write.Value)!.Single().Flag);
    }

    #endregion

    #region Competitor metadata

    [TestMethod]
    public async Task SendCompetitorMetadata_ForAnEventTheClientDoesNotOwn_IsIgnored()
    {
        var otherOrgId = await SeedOrganizationAsync("some-other-relay");
        var eventId = NewEventId();
        await SeedEventAsync(eventId, otherOrgId);
        var hub = CreateHub();

        await hub.SendCompetitorMetadata(eventId, [new CompetitorMetadata { EventId = eventId, CarNumber = "42" }]);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.IsFalse(await db.CompetitorMetadata.AnyAsync());
        Assert.IsEmpty(redis.StreamWrites);
    }

    [TestMethod]
    public async Task SendCompetitorMetadata_PersistsNewCompetitorsAndDropsTheirCachedCopies()
    {
        var orgId = await SeedOrganizationAsync();
        var eventId = NewEventId();
        await SeedEventAsync(eventId, orgId);
        var hub = CreateHub();

        await hub.SendCompetitorMetadata(eventId,
        [
            new CompetitorMetadata { EventId = eventId, CarNumber = "42", Make = "Mazda", LastUpdated = DateTime.UtcNow },
            new CompetitorMetadata { EventId = eventId, CarNumber = "7", Make = "Honda", LastUpdated = DateTime.UtcNow },
        ]);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.HasCount(2, await db.CompetitorMetadata.ToListAsync());
        Assert.AreEqual(string.Format(Consts.EVENT_COMPETITORS, eventId), redis.StreamWrites.Single().Field);
        Assert.Contains(string.Format(Consts.COMPETITOR_METADATA, "42", eventId), redis.KeyDeletes);
        Assert.Contains(string.Format(Consts.COMPETITOR_METADATA, "7", eventId), redis.KeyDeletes);
    }

    [TestMethod]
    public async Task SendCompetitorMetadata_WithANewerTimestamp_OverwritesTheStoredRow()
    {
        var orgId = await SeedOrganizationAsync();
        var eventId = NewEventId();
        await SeedEventAsync(eventId, orgId);
        var stamp = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            seed.CompetitorMetadata.Add(new CompetitorMetadata { EventId = eventId, CarNumber = "42", Make = "Mazda", LastUpdated = stamp });
            await seed.SaveChangesAsync();
        }
        var hub = CreateHub();

        await hub.SendCompetitorMetadata(eventId,
            [new CompetitorMetadata { EventId = eventId, CarNumber = "42", Make = "Porsche", LastUpdated = stamp.AddMinutes(1) }]);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual("Porsche", (await db.CompetitorMetadata.SingleAsync()).Make);
    }

    /// <summary>
    /// Relays replay their whole competitor list periodically, so a stale copy arriving after an
    /// edit must not undo it.
    /// </summary>
    [TestMethod]
    public async Task SendCompetitorMetadata_WithAnOlderTimestamp_LeavesTheStoredRow()
    {
        var orgId = await SeedOrganizationAsync();
        var eventId = NewEventId();
        await SeedEventAsync(eventId, orgId);
        var stamp = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        await using (var seed = await dbFactory.CreateDbContextAsync())
        {
            seed.CompetitorMetadata.Add(new CompetitorMetadata { EventId = eventId, CarNumber = "42", Make = "Porsche", LastUpdated = stamp });
            await seed.SaveChangesAsync();
        }
        var hub = CreateHub();

        await hub.SendCompetitorMetadata(eventId,
            [new CompetitorMetadata { EventId = eventId, CarNumber = "42", Make = "Mazda", LastUpdated = stamp.AddMinutes(-1) }]);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.AreEqual("Porsche", (await db.CompetitorMetadata.SingleAsync()).Make);
    }

    #endregion

    #region Relay logging

    [TestMethod]
    public async Task SaveLoggerMessage_TruncatesFieldsToTheColumnWidths()
    {
        var orgId = await SeedOrganizationAsync();
        var hub = CreateHub();

        await hub.SaveLoggerMessage(DateTime.UtcNow, new string('L', 25), new string('S', 400), new string('E', 2000));

        await using var db = await dbFactory.CreateDbContextAsync();
        var log = await db.RelayLogs.SingleAsync();
        Assert.AreEqual(20, log.Level.Length);
        Assert.AreEqual(300, log.State.Length);
        Assert.AreEqual(1024, log.Exception.Length);
        Assert.AreEqual(orgId, log.OrganizationId);
    }

    [TestMethod]
    public async Task SaveLoggerMessage_WithNullFields_StoresTheNonNullDefaults()
    {
        await SeedOrganizationAsync();
        var hub = CreateHub();

        await hub.SaveLoggerMessage(DateTime.UtcNow, null!, null!, null!);

        await using var db = await dbFactory.CreateDbContextAsync();
        var log = await db.RelayLogs.SingleAsync();
        Assert.AreEqual("Unknown", log.Level);
        Assert.AreEqual(string.Empty, log.State);
        Assert.AreEqual(string.Empty, log.Exception);
    }

    [TestMethod]
    public async Task SaveLoggerMessage_AccumulatesABatchForTheEmailReport()
    {
        var orgId = await SeedOrganizationAsync();
        var hub = CreateHub();
        var batchKey = string.Format(Consts.RELAY_LOG_BATCH, ClientId);

        await hub.SaveLoggerMessage(DateTime.UtcNow, "Error", "boom", "");
        await hub.SaveLoggerMessage(DateTime.UtcNow, "Error", "boom again", "");

        Assert.AreEqual("2", redis.GetHashValue(batchKey, "count"));
        Assert.AreEqual("2", redis.GetHashValue(batchKey, "level_Error"));
        Assert.AreEqual(orgId.ToString(), redis.GetHashValue(batchKey, "orgId"));
        Assert.Contains((Consts.RELAY_LOG_BATCH_TRACKING, ClientId), redis.SetAdds);
        // The batch has to expire on its own in case the reporting service is down.
        Assert.HasCount(2, redis.KeyExpires);
        Assert.IsTrue(redis.KeyExpires.All(e => e.Key == batchKey && e.Expiry == TimeSpan.FromMinutes(10)));
    }

    /// <summary>The log row is the point of the call, so a failure tracking the batch must not lose it.</summary>
    [TestMethod]
    public async Task SaveLoggerMessage_WhenBatchTrackingFails_StillStoresTheLog()
    {
        await SeedOrganizationAsync();
        redis.FailHashWrites();
        var hub = CreateHub();

        await hub.SaveLoggerMessage(DateTime.UtcNow, "Error", "boom", "");

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.HasCount(1, await db.RelayLogs.ToListAsync());
    }

    #endregion

    #region Organization lookup

    [TestMethod]
    public async Task GetOrganizationIdAsync_ForAnUnknownClient_ReturnsZero()
    {
        var hub = CreateHub();

        Assert.AreEqual(0, await hub.GetOrganizationIdAsync("never-registered"));
    }

    [TestMethod]
    public async Task GetOrganizationIdAsync_IsCachedPerClientId()
    {
        var orgId = await SeedOrganizationAsync();
        var hub = CreateHub();

        Assert.AreEqual(orgId, await hub.GetOrganizationIdAsync(ClientId));
        Assert.AreEqual(orgId, await hub.GetOrganizationIdAsync(ClientId));

        CollectionAssert.AreEqual(new[] { string.Format(Consts.CLIENT_ID, ClientId) }, hcache.FactoryInvocations,
            "every relay message resolves the org, so it must not be a database round trip each time");
    }

    #endregion

    private sealed class ThrowingDbContextFactory : IDbContextFactory<TsContext>
    {
        public TsContext CreateDbContext() => throw new InvalidOperationException("database unavailable");

        public ValueTask<TsContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("database unavailable");
    }
}
