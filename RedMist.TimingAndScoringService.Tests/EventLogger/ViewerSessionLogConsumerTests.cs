using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventLogger.Services;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingAndScoringService.Tests.Shared;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventLogger;

/// <summary>
/// Covers turning the status API's viewer session stream into rows: what opens a session, what
/// closes one, and the several ways the same entry can arrive twice.
/// </summary>
[TestClass]
public class ViewerSessionLogConsumerTests
{
    private const int EventId = 4321;

    private FakeRedisDatabase redis = null!;
    private TestDbContextFactory dbFactory = null!;
    private FakeTimeProvider clock = null!;
    private ViewerSessionLogConsumer consumer = null!;

    [TestInitialize]
    public void Setup()
    {
        redis = new FakeRedisDatabase();
        dbFactory = NewDbFactory();
        clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 14, 0, 0, TimeSpan.Zero));
        consumer = CreateConsumer();
    }

    private static TestDbContextFactory NewDbFactory()
        => new(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"ViewerSessionLogConsumerTests_{Guid.NewGuid()}")
            .Options);

    private ViewerSessionLogConsumer CreateConsumer(bool isSimulation = false)
    {
        using (var db = dbFactory.CreateDbContext())
        {
            var existing = db.Events.FirstOrDefault(e => e.Id == EventId);
            if (existing == null)
            {
                db.Events.Add(new TimingCommon.Models.Configuration.Event
                {
                    Id = EventId,
                    OrganizationId = 7,
                    Name = "Test Event",
                    IsSimulation = isSimulation,
                });
            }
            else
            {
                existing.IsSimulation = isSimulation;
            }
            db.SaveChanges();
        }

        return new ViewerSessionLogConsumer(new DebugLoggerFactory(), redis.Mux.Object,
            RedisStreamTestHarness.ConfigForEvent(EventId), dbFactory, clock);
    }

    private static StreamEntry EntryFor(string id, ViewerSessionEvent viewerEvent)
        => RedisStreamTestHarness.Entry(id, (Consts.VIEWER_SESSION_TYPE, JsonSerializer.Serialize(viewerEvent)));

    private ViewerSessionEvent Start(string connectionId, string clientType = "Android", DateTime? at = null, bool inCar = false)
        => new()
        {
            Kind = ViewerSessionEventKind.Start,
            EventId = EventId,
            ConnectionId = connectionId,
            ClientType = clientType,
            TimestampUtc = at ?? clock.GetUtcNow().UtcDateTime,
            IsInCar = inCar,
            CarNumber = inCar ? "42" : null,
        };

    private ViewerSessionEvent End(string connectionId, ViewerSessionEndReason reason, DateTime? at = null)
        => new()
        {
            Kind = ViewerSessionEventKind.End,
            EventId = EventId,
            ConnectionId = connectionId,
            TimestampUtc = at ?? clock.GetUtcNow().UtcDateTime,
            Reason = reason,
        };

    private Task ProcessAsync(params StreamEntry[] entries)
        => consumer.ProcessBatchAsync(redis.Db.Object, entries, CancellationToken.None);

    private List<EventViewerSession> Sessions()
    {
        using var db = dbFactory.CreateDbContext();
        return [.. db.EventViewerSessions.OrderBy(s => s.Id)];
    }

    [TestMethod]
    public async Task Start_OpensASessionWithTheClientTypeAndTimestampFromThePayload()
    {
        var at = new DateTime(2026, 9, 19, 14, 5, 0, DateTimeKind.Utc);

        await ProcessAsync(EntryFor("1-0", Start("conn-a", "Android", at)));

        var session = Sessions().Single();
        Assert.AreEqual(EventId, session.EventId);
        Assert.AreEqual("conn-a", session.ConnectionId);
        Assert.AreEqual("Android", session.ClientType);
        Assert.AreEqual(at, session.StartUtc);
        Assert.IsNull(session.EndUtc);
        Assert.IsFalse(session.StartInferred);
    }

    [TestMethod]
    public async Task Start_CarriesInCarModeAndTheCarNumber()
    {
        await ProcessAsync(EntryFor("1-0", Start("conn-a", inCar: true)));

        var session = Sessions().Single();
        Assert.IsTrue(session.IsInCar);
        Assert.AreEqual("42", session.CarNumber);
    }

    /// <summary>
    /// The event id comes from this pod's configuration, not from the payload. One logger serves one
    /// event, and a payload claiming another event would mean the streams were crossed.
    /// </summary>
    [TestMethod]
    public async Task Start_TakesTheEventIdFromConfigurationRatherThanThePayload()
    {
        var mismatched = Start("conn-a");
        mismatched.EventId = 999;

        await ProcessAsync(EntryFor("1-0", mismatched));

        Assert.AreEqual(EventId, Sessions().Single().EventId);
    }

    [TestMethod]
    public async Task DuplicateStart_DoesNotOpenASecondSession()
    {
        var start = Start("conn-a");

        await ProcessAsync(EntryFor("1-0", start), EntryFor("1-1", start));

        Assert.HasCount(1, Sessions());
    }

    /// <summary>
    /// Both copies land in one batch, so the guard has to see the addition that is still only pending
    /// on the change tracker rather than only what the database already holds.
    /// </summary>
    [TestMethod]
    public async Task DuplicateStartWithinOneBatch_DoesNotOpenASecondSession()
    {
        await ProcessAsync(EntryFor("1-0", Start("conn-a")), EntryFor("1-1", Start("conn-a", at: clock.GetUtcNow().UtcDateTime.AddSeconds(1))));

        Assert.HasCount(1, Sessions());
    }

    /// <summary>
    /// The identical entry delivered twice, after its end has already been applied. This is the case
    /// that reaches the unique index on (EventId, ConnectionId, StartUtc), and the one the in-memory
    /// provider cannot enforce - so the guard has to be in the code, not in the schema.
    /// </summary>
    [TestMethod]
    public async Task StartRedeliveredAfterItsEndWasApplied_DoesNotInsertADuplicateRow()
    {
        var start = Start("conn-a");
        var at = start.TimestampUtc;

        await ProcessAsync(
            EntryFor("1-0", start),
            EntryFor("1-1", End("conn-a", ViewerSessionEndReason.Disconnected, at.AddMinutes(5))));

        await ProcessAsync(EntryFor("1-0", start));

        var sessions = Sessions();
        Assert.HasCount(1, sessions,
            "The replayed start produced a second row sharing (EventId, ConnectionId, StartUtc). "
            + "The in-memory provider allows that; PostgreSQL rejects it and takes the whole batch down with it.");
        Assert.AreEqual(at.AddMinutes(5), sessions[0].EndUtc, "The original session must keep its end time.");
    }

    [TestMethod]
    public async Task StartAfterTheSessionWasClosed_OpensANewSession()
    {
        var first = clock.GetUtcNow().UtcDateTime;
        await ProcessAsync(
            EntryFor("1-0", Start("conn-a", at: first)),
            EntryFor("1-1", End("conn-a", ViewerSessionEndReason.Disconnected, first.AddMinutes(5))),
            EntryFor("1-2", Start("conn-a", at: first.AddMinutes(10))));

        var sessions = Sessions();
        Assert.HasCount(2, sessions);
        Assert.IsNotNull(sessions[0].EndUtc);
        Assert.IsNull(sessions[1].EndUtc);
    }

    [TestMethod]
    public async Task End_ClosesTheOpenSessionWithItsReason()
    {
        var start = clock.GetUtcNow().UtcDateTime;
        await ProcessAsync(
            EntryFor("1-0", Start("conn-a", at: start)),
            EntryFor("1-1", End("conn-a", ViewerSessionEndReason.Unsubscribed, start.AddMinutes(12))));

        var session = Sessions().Single();
        Assert.AreEqual(start.AddMinutes(12), session.EndUtc);
        Assert.AreEqual(ViewerSessionEndReason.Unsubscribed, session.EndReason);
    }

    /// <summary>
    /// The Start was lost, or the session was already closed. Either way there is nothing to act on
    /// and the entry must still be acknowledged rather than retried forever.
    /// </summary>
    [TestMethod]
    public async Task EndForAnUnknownConnection_IsANoOp()
    {
        await ProcessAsync(EntryFor("1-0", End("ghost", ViewerSessionEndReason.Disconnected)));

        Assert.IsEmpty(Sessions());
        Assert.Contains("1-0", redis.StreamAcknowledgements.Select(a => a.Id));
    }

    /// <summary>
    /// An End redelivered after the reconciler already closed the session must not move the end time
    /// it settled on, or the session silently grows every time the entry is replayed.
    /// </summary>
    [TestMethod]
    public async Task EndTwice_LeavesTheFirstEndTimeIntact()
    {
        var start = clock.GetUtcNow().UtcDateTime;
        await ProcessAsync(
            EntryFor("1-0", Start("conn-a", at: start)),
            EntryFor("1-1", End("conn-a", ViewerSessionEndReason.Unsubscribed, start.AddMinutes(5))));

        await ProcessAsync(EntryFor("1-2", End("conn-a", ViewerSessionEndReason.Disconnected, start.AddHours(3))));

        var session = Sessions().Single();
        Assert.AreEqual(start.AddMinutes(5), session.EndUtc);
        Assert.AreEqual(ViewerSessionEndReason.Unsubscribed, session.EndReason);
    }

    [TestMethod]
    public async Task MalformedPayload_IsContainedAndTheEntryIsStillAcknowledged()
    {
        await ProcessAsync(
            RedisStreamTestHarness.Entry("1-0", (Consts.VIEWER_SESSION_TYPE, "{ not json")),
            EntryFor("1-1", Start("conn-a")));

        // The good entry in the same batch is unaffected, and neither entry is left to be retried.
        Assert.HasCount(1, Sessions());
        CollectionAssert.AreEquivalent(new[] { "1-0", "1-1" }, redis.StreamAcknowledgements.Select(a => a.Id).ToArray());
    }

    [TestMethod]
    public async Task UnexpectedField_IsIgnoredAndAcknowledged()
    {
        await ProcessAsync(RedisStreamTestHarness.Entry("1-0", ("something-else", "{}")));

        Assert.IsEmpty(Sessions());
        Assert.Contains("1-0", redis.StreamAcknowledgements.Select(a => a.Id));
    }

    /// <summary>
    /// A simulation is still watched, and capture does not judge what it is watching. The flag
    /// decides who gets told about an event afterwards, not whether the event is recorded:
    /// PostEventReportJob leaves simulations out of its candidates, and SimulatedEventPurgeService
    /// deletes their rows a day later, so nothing reaches an organizer and nothing accumulates.
    /// </summary>
    /// <remarks>
    /// Capture used to drop these. That cost the only realistic way to exercise the whole path -
    /// a simulation with a browser attached to it is exactly how this feature gets tested - and it
    /// disagreed with the reconciler, which had no such rule and reconstructed the sessions from
    /// the connection hash anyway. The accurate transitions were discarded and the guesses kept.
    /// </remarks>
    [TestMethod]
    public async Task SimulationEvent_IsRecordedLikeAnyOther()
    {
        dbFactory = NewDbFactory();
        consumer = CreateConsumer(isSimulation: true);

        await ProcessAsync(EntryFor("1-0", Start("conn-a")));

        var session = Sessions().Single();
        Assert.AreEqual("conn-a", session.ConnectionId);
        Assert.IsFalse(session.StartInferred, "The consumer's own record was replaced by a guess.");
        Assert.Contains("1-0", redis.StreamAcknowledgements.Select(a => a.Id));
    }

    /// <summary>
    /// The solution runs with Npgsql's legacy timestamp behavior, where a DateTime is stored
    /// according to its Kind, so a value that arrives without one must not be written as if it were
    /// local time. Nothing catches that until a report is hours wrong, and it would not show up in
    /// production at all: the containers run UTC, so the shift only appears on a developer machine.
    /// </summary>
    [TestMethod]
    public async Task Timestamp_ArrivingAsUtc_IsStoredUnchanged()
    {
        var at = new DateTime(2026, 9, 19, 14, 30, 0, DateTimeKind.Utc);
        var start = Start("conn-a");
        start.TimestampUtc = at;

        await ProcessAsync(EntryFor("1-0", start));

        var session = Sessions().Single();
        Assert.AreEqual(DateTimeKind.Utc, session.StartUtc.Kind);
        Assert.AreEqual(at, session.StartUtc);
    }

    /// <summary>
    /// A payload whose Kind was lost in transit lost its Kind, not its meaning: the producer only
    /// ever stamps UTC, so the clock reading is taken at face value rather than shifted by whatever
    /// timezone the reading machine happens to be in.
    /// </summary>
    [TestMethod]
    public async Task Timestamp_ArrivingWithoutAKind_IsTakenAsUtcRatherThanShifted()
    {
        var unspecified = new DateTime(2026, 9, 19, 14, 30, 0, DateTimeKind.Unspecified);
        var start = Start("conn-a");
        start.TimestampUtc = unspecified;

        await ProcessAsync(EntryFor("1-0", start));

        var session = Sessions().Single();
        Assert.AreEqual(DateTimeKind.Utc, session.StartUtc.Kind);
        Assert.AreEqual(14, session.StartUtc.Hour);
        Assert.AreEqual(30, session.StartUtc.Minute);
    }

    /// <summary>
    /// The consumer group has to start at the beginning of the stream, not at "$" like the sibling
    /// consumers. This pod is started after the relay connects, which can be well after the first
    /// viewers subscribed, and "$" would silently discard every Start published in between.
    /// </summary>
    [TestMethod]
    public async Task EnsureStream_CreatesTheConsumerGroupAtTheBeginningOfTheStream()
    {
        var created = new List<string>();
        redis.Db.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(false);
        redis.Db.Setup(x => x.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                It.IsAny<RedisValue?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue group, RedisValue? position, bool createStream, CommandFlags flags) =>
            {
                created.Add(position?.ToString() ?? "<null>");
                return Task.FromResult(true);
            });

        using var cts = new CancellationTokenSource();
        RedisStreamTestHarness.SetupReads(redis.Db, cts);
        await RedisStreamTestHarness.RunAsync(
            token => new TestableViewerSessionLogConsumer(new DebugLoggerFactory(), redis.Mux.Object,
                RedisStreamTestHarness.ConfigForEvent(EventId), dbFactory, clock).RunAsync(token),
            cts.Token);

        Assert.AreEqual(StreamPosition.Beginning.ToString(), created.Single(),
            "The consumer group must be created at the beginning of the stream, not at the default \"new messages only\" "
            + "position the sibling consumers use. See EnsureStreamAsync for why.");
    }
}
