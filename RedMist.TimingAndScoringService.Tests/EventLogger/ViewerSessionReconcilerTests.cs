using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventLogger.Services;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingAndScoringService.Tests.Shared;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventLogger;

/// <summary>
/// Covers the half of viewership capture that decides how long people watched for when nothing ever
/// told us they left.
/// </summary>
/// <remarks>
/// The reconciler is what stands between the numbers and a status API pod that died without running
/// its disconnect handler. Both directions of getting it wrong are silent: close too eagerly and
/// every viewer's session is cut short, close too reluctantly and viewer-minutes grow without bound.
/// </remarks>
[TestClass]
public class ViewerSessionReconcilerTests
{
    private const int EventId = 4321;
    private static readonly DateTime Origin = new(2026, 9, 19, 14, 0, 0, DateTimeKind.Utc);

    private FakeRedisDatabase redis = null!;
    private TestDbContextFactory dbFactory = null!;
    private FakeTimeProvider clock = null!;
    private ViewerSessionReconciler reconciler = null!;

    [TestInitialize]
    public void Setup()
    {
        redis = new FakeRedisDatabase();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"ViewerSessionReconcilerTests_{Guid.NewGuid()}")
            .Options);
        clock = new FakeTimeProvider(new DateTimeOffset(Origin));
        reconciler = new ViewerSessionReconciler(new DebugLoggerFactory(), redis.Mux.Object,
            RedisStreamTestHarness.ConfigForEvent(EventId), dbFactory, clock);
    }

    private static string ConnectionsKey => string.Format(Consts.STATUS_EVENT_CONNECTIONS, EventId);

    /// <summary>Puts a connection in the live connection hash, as a status API replica would.</summary>
    private void SeedLive(string connectionId, string clientType = "Android")
        => redis.SeedHash(ConnectionsKey, connectionId, clientType);

    private void SeedConnectRecord(string connectionId, DateTime connectedAt)
        => redis.SeedHash(Consts.STATUS_CONNECTIONS, connectionId,
            JsonSerializer.Serialize(new StatusConnection { ConnectedTimestamp = connectedAt, SubscribedEventId = EventId }));

    private void SeedSession(string connectionId, DateTime startUtc, int eventId = EventId)
    {
        using var db = dbFactory.CreateDbContext();
        db.EventViewerSessions.Add(new EventViewerSession
        {
            EventId = eventId,
            ConnectionId = connectionId,
            ClientType = "Android",
            StartUtc = startUtc,
        });
        db.SaveChanges();
    }

    private List<EventViewerSession> Sessions()
    {
        using var db = dbFactory.CreateDbContext();
        return [.. db.EventViewerSessions.OrderBy(s => s.Id)];
    }

    private Task ReconcileAsync() => reconciler.ReconcileAsync(CancellationToken.None);

    /// <summary>
    /// Runs the observe-only pass so the tests that follow start from a reconciler that has
    /// something to compare against.
    /// </summary>
    private async Task PrimeAsync()
    {
        await ReconcileAsync();
    }

    /// <summary>
    /// A pod bounce mid-event is routine, and the viewers it was serving are still watching. Closing
    /// their sessions on the first pass would triple the session count and punch a hole in peak
    /// concurrency at exactly the moment the chart matters most.
    /// </summary>
    [TestMethod]
    public async Task FirstPass_ObservesOnlyAndChangesNothing()
    {
        SeedSession("conn-gone", Origin.AddMinutes(-10));

        await ReconcileAsync();

        Assert.IsNull(Sessions().Single().EndUtc);
    }

    [TestMethod]
    public async Task FirstPass_DoesNotBackfillEither()
    {
        SeedLive("conn-new");

        await ReconcileAsync();

        Assert.IsEmpty(Sessions());
    }

    /// <summary>
    /// The end time is when the connection was first missed, not when the reconciler got around to
    /// acting: the connection was demonstrably gone by then, so ending it there bounds the
    /// over-count at one interval rather than two.
    /// </summary>
    [TestMethod]
    public async Task ConnectionAbsentForTwoPasses_IsClosedAtTheTimeItWasFirstMissing()
    {
        SeedSession("conn-a", Origin.AddMinutes(-10));
        SeedLive("conn-a");
        await PrimeAsync();

        // Gone as of this pass, which is the first one that misses it.
        redis.SeedHashRemove(ConnectionsKey, "conn-a");
        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        var firstMissing = clock.GetUtcNow().UtcDateTime;
        await ReconcileAsync();
        Assert.IsNull(Sessions().Single().EndUtc, "One miss is not enough to close a session.");

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        var session = Sessions().Single();
        Assert.AreEqual(firstMissing, session.EndUtc);
        Assert.AreEqual(ViewerSessionEndReason.ReconciledAbsent, session.EndReason);
    }

    /// <summary>
    /// A session left open by a previous instance of this pod is closed at the observe-only pass that
    /// first saw it missing, which is the earliest moment it is known to have been gone.
    /// </summary>
    [TestMethod]
    public async Task SessionOrphanedBeforeStartup_IsClosedAtTheObservePass()
    {
        SeedSession("conn-orphan", Origin.AddMinutes(-10));

        await PrimeAsync();
        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        var session = Sessions().Single();
        Assert.AreEqual(Origin, session.EndUtc);
        Assert.AreEqual(ViewerSessionEndReason.ReconciledAbsent, session.EndReason);
    }

    /// <summary>
    /// The hub writes the live connection hash entry fire-and-forget, so a session opened moments ago
    /// can legitimately not be in the hash yet. Closing on a single miss would end sessions
    /// milliseconds after they began.
    /// </summary>
    [TestMethod]
    public async Task ConnectionAbsentForOnePassThenBack_IsNotClosed()
    {
        SeedSession("conn-a", Origin.AddMinutes(-10));
        SeedLive("conn-a");
        await PrimeAsync();

        redis.SeedHashRemove(ConnectionsKey, "conn-a");
        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        SeedLive("conn-a");
        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        Assert.IsNull(Sessions().Single().EndUtc);
    }

    /// <summary>
    /// Having come back once, the connection must be given the full two passes again before it can be
    /// closed - otherwise the stale first-missing time would end it in the past.
    /// </summary>
    [TestMethod]
    public async Task ConnectionThatReturnedThenLeaves_IsGivenTwoFreshPasses()
    {
        SeedSession("conn-a", Origin.AddMinutes(-10));
        SeedLive("conn-a");
        await PrimeAsync();

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        redis.SeedHashRemove(ConnectionsKey, "conn-a");
        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        var firstMissing = clock.GetUtcNow().UtcDateTime;
        await ReconcileAsync();
        Assert.IsNull(Sessions().Single().EndUtc);

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();
        Assert.AreEqual(firstMissing, Sessions().Single().EndUtc);
    }

    /// <summary>
    /// The single most important negative case in the feature. The live connection hash is the only
    /// evidence the reconciler has, and treating an unreachable Redis as an empty hash would read a
    /// thirty second blip as every viewer leaving at once. There is no way to reconstruct the
    /// sessions afterwards.
    /// </summary>
    [TestMethod]
    public async Task HashReadFails_LeavesEverySessionOpen()
    {
        SeedSession("conn-a", Origin.AddMinutes(-10));
        SeedSession("conn-b", Origin.AddMinutes(-10));
        SeedLive("conn-a");
        SeedLive("conn-b");
        await PrimeAsync();
        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        // Redis goes away. Two passes of silence is exactly the shape that would otherwise be read
        // as both viewers having left.
        redis.FailHashGetAll();
        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();
        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        Assert.IsTrue(Sessions().All(s => s.EndUtc == null));
    }

    /// <summary>
    /// A connection in the hash with no open session: its Start was published while this pod was down,
    /// or lost. The session is real and has to be counted, but it is marked as reconstructed.
    /// </summary>
    [TestMethod]
    public async Task ConnectionInTheHashWithNoOpenSession_IsBackfilled()
    {
        await PrimeAsync();
        SeedLive("conn-new", "iOS");

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        var session = Sessions().Single();
        Assert.AreEqual("conn-new", session.ConnectionId);
        Assert.AreEqual("iOS", session.ClientType);
        Assert.IsTrue(session.StartInferred);
        Assert.IsNull(session.EndUtc);
    }

    [TestMethod]
    public async Task Backfill_UsesTheRecordedConnectTimeWhenThereIsOne()
    {
        await PrimeAsync();
        var connectedAt = Origin.AddMinutes(3);
        SeedLive("conn-new");
        SeedConnectRecord("conn-new", connectedAt);

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        Assert.AreEqual(connectedAt, Sessions().Single().StartUtc);
    }

    [TestMethod]
    public async Task Backfill_FallsBackToNowWhenThereIsNoConnectRecord()
    {
        await PrimeAsync();
        SeedLive("conn-new");

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        var now = clock.GetUtcNow().UtcDateTime;
        await ReconcileAsync();

        Assert.AreEqual(now, Sessions().Single().StartUtc);
    }

    /// <summary>
    /// A connect time older than the cap would produce a session that is already over-length the
    /// moment it is written, and would be closed by the cap on the very next pass.
    /// </summary>
    [TestMethod]
    public async Task Backfill_ClampsAConnectTimeOlderThanTheCap()
    {
        await PrimeAsync();
        SeedLive("conn-new");
        SeedConnectRecord("conn-new", Origin.AddDays(-3));

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        var now = clock.GetUtcNow().UtcDateTime;
        await ReconcileAsync();

        Assert.AreEqual(now - ViewerSessionReconciler.MaxSessionDuration, Sessions().Single().StartUtc);
    }

    /// <summary>
    /// A connection the hash still lists but which has run past the cap. Presence in the hash is not
    /// evidence of life - the entry is removed by the disconnect handler, which is exactly what does
    /// not run when a pod is killed outright - so the cap is the only thing that will ever close it.
    /// </summary>
    [TestMethod]
    public async Task OpenSessionOlderThanTheCap_IsClosedEvenThoughItIsStillInTheHash()
    {
        SeedSession("conn-stuck", Origin.AddHours(-10));
        SeedLive("conn-stuck");
        await PrimeAsync();

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        var session = Sessions().Single();
        Assert.AreEqual(Origin.AddHours(-10) + ViewerSessionReconciler.MaxSessionDuration, session.EndUtc);
        Assert.AreEqual(ViewerSessionEndReason.CappedDuration, session.EndReason);
    }

    /// <summary>
    /// The cap fires precisely because the connection is still listed in the hash, so the pass after
    /// it sees a listed connection with no open row. Treating that as a lost start would open a
    /// second session overlapping the one just capped - doubling the viewer-minutes, and on
    /// PostgreSQL colliding with the unique key and failing every close in the pass.
    /// </summary>
    [TestMethod]
    public async Task CappedSession_IsNotReopenedOnTheFollowingPass()
    {
        SeedSession("conn-stuck", Origin.AddHours(-10));
        SeedLive("conn-stuck");
        await PrimeAsync();

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();
        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();
        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        Assert.HasCount(1, Sessions());
    }

    [TestMethod]
    public async Task LiveConnectionWithinTheCap_IsLeftAlone()
    {
        SeedSession("conn-a", Origin.AddMinutes(-30));
        SeedLive("conn-a");
        await PrimeAsync();

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        Assert.IsNull(Sessions().Single().EndUtc);
    }

    [TestMethod]
    public void ShutdownSignalForThisEvent_ClosesEveryOpenSession()
    {
        SeedSession("conn-a", Origin.AddMinutes(-30));
        SeedSession("conn-b", Origin.AddMinutes(-10));

        reconciler.HandleEventShutdown(JsonSerializer.Serialize(new List<int> { EventId }));

        var sessions = Sessions();
        Assert.IsTrue(sessions.All(s => s.EndUtc == Origin));
        Assert.IsTrue(sessions.All(s => s.EndReason == ViewerSessionEndReason.EventTeardown));
    }

    [TestMethod]
    public void ShutdownSignalForAnotherEvent_IsIgnored()
    {
        SeedSession("conn-a", Origin.AddMinutes(-30));

        reconciler.HandleEventShutdown(JsonSerializer.Serialize(new List<int> { EventId + 1 }));

        Assert.IsNull(Sessions().Single().EndUtc);
    }

    [TestMethod]
    public void ShutdownSignal_DoesNotReopenAnAlreadyClosedSession()
    {
        using (var db = dbFactory.CreateDbContext())
        {
            db.EventViewerSessions.Add(new EventViewerSession
            {
                EventId = EventId,
                ConnectionId = "conn-done",
                ClientType = "Web",
                StartUtc = Origin.AddHours(-1),
                EndUtc = Origin.AddMinutes(-40),
                EndReason = ViewerSessionEndReason.Unsubscribed,
            });
            db.SaveChanges();
        }

        reconciler.HandleEventShutdown(JsonSerializer.Serialize(new List<int> { EventId }));

        var session = Sessions().Single();
        Assert.AreEqual(Origin.AddMinutes(-40), session.EndUtc);
        Assert.AreEqual(ViewerSessionEndReason.Unsubscribed, session.EndReason);
    }

    /// <summary>
    /// Teardown closes sessions whose connections are still listed, so the same reopen trap applies
    /// as at the cap - and the pod lives on for a few seconds after the signal.
    /// </summary>
    [TestMethod]
    public async Task SessionsClosedAtTeardown_AreNotReopenedByALaterPass()
    {
        SeedSession("conn-a", Origin.AddMinutes(-30));
        SeedLive("conn-a");
        await PrimeAsync();

        reconciler.HandleEventShutdown(JsonSerializer.Serialize(new List<int> { EventId }));

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();
        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        var session = Sessions().Single();
        Assert.AreEqual(ViewerSessionEndReason.EventTeardown, session.EndReason);
    }

    /// <summary>
    /// Backfilled starts are persisted, so they carry the same UTC obligation as everything else -
    /// the legacy Npgsql timestamp behavior stores a DateTime according to its Kind.
    /// </summary>
    [TestMethod]
    public async Task Backfill_StoresTheStartAsUtc()
    {
        await PrimeAsync();
        SeedLive("conn-new");
        SeedConnectRecord("conn-new", Origin.AddMinutes(3));

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        Assert.AreEqual(DateTimeKind.Utc, Sessions().Single().StartUtc.Kind);
    }

    /// <summary>
    /// A connect record that came back from JSON without a Kind is taken at face value, not shifted
    /// by the reading machine's timezone. Invisible in production, where containers run UTC.
    /// </summary>
    [TestMethod]
    public async Task Backfill_WithAKindlessConnectRecord_DoesNotShiftTheStart()
    {
        await PrimeAsync();
        var connectedAt = DateTime.SpecifyKind(Origin.AddMinutes(3), DateTimeKind.Unspecified);
        SeedLive("conn-new");
        SeedConnectRecord("conn-new", connectedAt);

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        var start = Sessions().Single().StartUtc;
        Assert.AreEqual(DateTimeKind.Utc, start.Kind);
        Assert.AreEqual(Origin.AddMinutes(3).TimeOfDay, start.TimeOfDay);
    }

    /// <summary>
    /// Several events run at once, each with its own logger pod reading its own connection hash. A
    /// pass for one event must not touch another's rows.
    /// </summary>
    [TestMethod]
    public async Task Reconcile_NeverTouchesAnotherEventsSessions()
    {
        SeedSession("conn-other", Origin.AddMinutes(-10), eventId: EventId + 1);
        await PrimeAsync();

        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();
        clock.Advance(ViewerSessionReconciler.ReconcileInterval);
        await ReconcileAsync();

        Assert.IsNull(Sessions().Single().EndUtc);
    }
}
