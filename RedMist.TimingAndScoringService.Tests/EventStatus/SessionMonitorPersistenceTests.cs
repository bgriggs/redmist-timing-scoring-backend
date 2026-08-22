using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.SessionMonitoring;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;

namespace RedMist.EventProcessor.Tests.EventStatus;

/// <summary>
/// A session ends once, and the monitor is the only thing that ever writes its results. Everything
/// here is about not losing them: the write-out rules that decide whether an existing result is
/// replaced, the bounded queue that holds results back while the database is unreachable, and the
/// session-change path staying on its feet when one of its steps fails.
/// </summary>
[TestClass]
public class SessionMonitorPersistenceTests
{
    private const int EventId = 1;
    private const int SessionId = 36;

    /// <summary>The session that really ran, for the tests about what an empty one hands over.</summary>
    private const int RanSessionId = 4;

    public TestContext TestContext { get; set; } = null!;

    #region Writing the results out

    /// <summary>
    /// The session row is what tells the events API the session is over; the result row is the
    /// session's only permanent record.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistFinishedSession_ForASessionThatEnded_RetiresTheRowAndWritesTheResults()
    {
        var harness = await CreateHarnessAsync(controlLogEntries: 2);

        var saved = harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 3, entries: 2, flags: 1)));

        Assert.IsTrue(saved);
        await using var db = harness.CreateDb();
        var session = db.Sessions.Single(s => s.Id == SessionId && s.EventId == EventId);
        Assert.IsFalse(session.IsLive, "The session ended, so it is no longer the live one.");
        Assert.IsNotNull(session.EndTime);

        var result = db.SessionResults.Single(r => r.EventId == EventId && r.SessionId == SessionId);
        Assert.HasCount(3, result.SessionState!.CarPositions);
        Assert.HasCount(2, result.ControlLogs);
        Assert.AreEqual(session.StartTime, result.Start);
    }

    /// <summary>
    /// The timing system announces a scratch run of its own at every run change, and one that lands
    /// at the end of an event never has anything applied to it. Writing results for it puts a second,
    /// empty entry beside the session that actually ran.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistFinishedSession_ForASessionThatSawNoCars_RetiresTheRowWithoutWritingResults()
    {
        var harness = await CreateHarnessAsync(controlLogEntries: 2);

        var saved = harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 0, entries: 0)));

        Assert.IsTrue(saved, "There was nothing to write, which is not a failure to write it.");
        await using var db = harness.CreateDb();
        var session = db.Sessions.Single(s => s.Id == SessionId && s.EventId == EventId);
        Assert.IsFalse(session.IsLive, "The session still ended, whether or not it produced anything.");
        Assert.IsNotNull(session.EndTime);
        Assert.IsEmpty(db.SessionResults.Where(r => r.EventId == EventId && r.SessionId == SessionId),
            "A session that saw no cars has nothing to show, and its control log belongs to the session that ran.");
    }

    /// <summary>
    /// The control log keeps growing after the session that earned it has been written out - a
    /// penalty posted minutes after the checkered flag is ordinary. Those late entries used to be
    /// kept only because the scratch run that follows was written out later and took a fresher copy
    /// of the log with it, so with nothing written for it they have to go to the session that ran.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistFinishedSession_ForASessionThatSawNoCars_CarriesAFresherControlLogToTheSessionThatRan()
    {
        var harness = await CreateHarnessAsync(controlLogEntries: 6);
        await SeedResultsForTheSessionThatRanAsync(harness, controlLogEntries: 2);

        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 0, entries: 0)));

        await using var db = harness.CreateDb();
        Assert.IsEmpty(db.SessionResults.Where(r => r.SessionId == SessionId));
        Assert.HasCount(6, db.SessionResults.Single(r => r.SessionId == RanSessionId).ControlLogs,
            "The entries that arrived after the real session was written out should have been kept.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistFinishedSession_ForASessionThatSawNoCars_DoesNotShortenTheSavedControlLog()
    {
        var harness = await CreateHarnessAsync(controlLogEntries: 1);
        await SeedResultsForTheSessionThatRanAsync(harness, controlLogEntries: 5);

        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 0, entries: 0)));

        await using var db = harness.CreateDb();
        Assert.HasCount(5, db.SessionResults.Single(r => r.SessionId == RanSessionId).ControlLogs,
            "A log that has since been trimmed must not replace the longer saved one.");
    }

    /// <summary>
    /// There is nothing to save for a session that saw no cars, so a cache that cannot be reached
    /// must not stop its row being retired - no retry would have anything to come back for, and the
    /// row would be left live, which is exactly what puts the empty entry back on the results list.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistFinishedSession_ForASessionThatSawNoCars_WhenTheCacheIsUnreachable_StillRetiresTheRow()
    {
        var harness = await CreateHarnessAsync();
        harness.Cache.Setup(x => x.StringGet(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Throws(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        var saved = harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 0, entries: 0)));

        Assert.IsTrue(saved);
        await using var db = harness.CreateDb();
        var session = db.Sessions.Single(s => s.Id == SessionId && s.EventId == EventId);
        Assert.IsFalse(session.IsLive);
        Assert.IsNotNull(session.EndTime);
    }

    /// <summary>
    /// Skipping the write leaves no row behind, so a second finalize that does have data takes the
    /// insert path rather than the update path.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistFinishedSession_AfterASkippedEmptyWrite_StillWritesTheResultsThatFollow()
    {
        var harness = await CreateHarnessAsync();
        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 0, entries: 0)));

        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 3, entries: 3)));

        await using var db = harness.CreateDb();
        Assert.HasCount(3, db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!.CarPositions);
    }

    /// <summary>
    /// The field being registered is enough to be worth keeping: an entry list with no positions is
    /// a session where the cars were entered but never took to the track, not a phantom.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistFinishedSession_ForASessionWithEntriesButNoPositions_WritesTheResults()
    {
        var harness = await CreateHarnessAsync();

        var saved = harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 0, entries: 4)));

        Assert.IsTrue(saved);
        await using var db = harness.CreateDb();
        Assert.HasCount(4, db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!.EventEntries);
    }

    /// <summary>
    /// The write runs with the session-state lock released, so a session change naming the same
    /// session can land in between and mark it live again. Retiring the row here would leave the
    /// event with no live session at all.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistFinishedSession_WhenTheSessionHasSinceBeenPickedUpAgain_LeavesTheRowLive()
    {
        var harness = await CreateHarnessAsync();
        await harness.Monitor.ProcessAsync(SessionId, TestContext.CancellationToken);

        var saved = harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 3)));

        Assert.IsTrue(saved);
        await using var db = harness.CreateDb();
        Assert.IsTrue(db.Sessions.Single(s => s.Id == SessionId).IsLive, "The session was picked back up before the write landed.");
        Assert.IsNull(db.Sessions.Single(s => s.Id == SessionId).EndTime);
        Assert.HasCount(3, db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!.CarPositions);
    }

    /// <summary>
    /// A session can be written out twice - the shutdown signal and the finish check both do it.
    /// The second write is only allowed to win when it is at least as complete, or a shutdown that
    /// caught a half-built state would replace a full set of results with it.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistFinishedSession_WithLessDataThanTheSavedResult_DoesNotOverwriteIt()
    {
        var harness = await CreateHarnessAsync();
        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 30, entries: 30, flags: 4)));

        var saved = harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 2, entries: 2, flags: 1)));

        Assert.IsTrue(saved);
        await using var db = harness.CreateDb();
        var result = db.SessionResults.Single(r => r.SessionId == SessionId);
        Assert.HasCount(30, result.SessionState!.CarPositions, "The fuller set of results has to stand.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistFinishedSession_WithMoreDataThanTheSavedResult_ReplacesIt()
    {
        var harness = await CreateHarnessAsync();
        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 2, entries: 2, flags: 1)));

        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 30, entries: 30, flags: 4)));

        await using var db = harness.CreateDb();
        Assert.HasCount(30, db.SessionResults.Single(r => r.SessionId == SessionId).SessionState!.CarPositions);
    }

    /// <summary>
    /// The control log arrives from a different service on its own schedule, so it is compared
    /// separately: a re-write that carries a shorter log must keep the longer saved one even while
    /// it replaces the session state.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistFinishedSession_ComparesControlLogsSeparatelyFromTheSessionState()
    {
        var harness = await CreateHarnessAsync(controlLogEntries: 5);
        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 2, entries: 2, flags: 1)));

        // The control log has since been trimmed, but the session state is more complete.
        harness.ControlLogEntryCount = 1;
        harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 30, entries: 30, flags: 4)));

        await using var db = harness.CreateDb();
        var result = db.SessionResults.Single(r => r.SessionId == SessionId);
        Assert.HasCount(30, result.SessionState!.CarPositions, "The fuller session state should have been taken.");
        Assert.HasCount(5, result.ControlLogs, "The longer control log should have been kept.");
    }

    /// <summary>
    /// The caller uses the answer to decide whether to hold the results for a later attempt, so a
    /// failed write must report itself rather than throwing or claiming success.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task PersistFinishedSession_WhenTheCacheIsUnreachable_ReportsFailureWithoutThrowing()
    {
        var harness = await CreateHarnessAsync();
        harness.Cache.Setup(x => x.StringGet(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Throws(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        var saved = harness.Monitor.CallPersist(new SessionMonitor.FinishedSession(SessionId, StateWith(cars: 3)));

        Assert.IsFalse(saved);
        await using var db = harness.CreateDb();
        Assert.IsEmpty(db.SessionResults.ToList());
    }

    #endregion

    #region Holding results back while the database is down

    /// <summary>
    /// Each held result is a whole session's state, so an event that cannot reach its database must
    /// not accumulate them without limit. The oldest go first, and what is left is still written in
    /// the order the sessions finished.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task UnsavedResults_AreCappedByDroppingTheOldestAndRetriedInOrder()
    {
        var attempts = new List<int>();
        var savingWorks = false;
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory())
        {
            OnPersistFinishedSession = f => { attempts.Add(f.SessionId); return savingWorks; }
        };

        // Six session changes end six sessions - the first ends the id the monitor starts on.
        for (int sessionId = 1; sessionId <= 6; sessionId++)
            await monitor.ProcessAsync(sessionId, TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, attempts);

        attempts.Clear();
        savingWorks = true;
        await monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5 }, attempts,
            "Only the four most recent should have been held, and they should go in the order they finished.");
    }

    /// <summary>
    /// A retry that fails again puts the result back at the front rather than the back: it is the
    /// oldest, and it stays the oldest. Working through the rest against a database that just
    /// refused one is also pointless, so the pass stops there.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task UnsavedResults_WhenARetryFailsAgain_KeepTheirOrderAndStopThePass()
    {
        var attempts = new List<int>();
        Func<SessionMonitor.FinishedSession, bool> persist = _ => false;
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory())
        {
            OnPersistFinishedSession = f => { attempts.Add(f.SessionId); return persist(f); }
        };

        for (int sessionId = 1; sessionId <= 3; sessionId++)
            await monitor.ProcessAsync(sessionId, TestContext.CancellationToken);
        attempts.Clear();

        // The database takes the first one and then refuses again.
        persist = f => f.SessionId == 0;
        await monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { 0, 1 }, attempts, "The pass should stop at the first result that still fails.");

        attempts.Clear();
        persist = _ => true;
        await monitor.RunCheckForFinishedAsync(TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { 1, 2 }, attempts, "The failed result must stay ahead of the ones behind it.");
    }

    #endregion

    #region The shutdown signal

    [TestMethod]
    [Timeout(30_000)]
    public async Task EventShutdown_NamingThisEvent_WritesTheRunningSessionOut()
    {
        var (monitor, attempts) = await MonitorOnASessionAsync();

        monitor.HandleEventShutdown(JsonSerializer.Serialize(new[] { 99, EventId }));

        CollectionAssert.AreEqual(new[] { SessionId }, attempts.ToArray());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task EventShutdown_NamingOtherEvents_LeavesThisSessionRunning()
    {
        var (monitor, attempts) = await MonitorOnASessionAsync();

        monitor.HandleEventShutdown(JsonSerializer.Serialize(new[] { EventId + 1 }));

        Assert.IsEmpty(attempts);
        Assert.AreEqual(SessionId, monitor.SessionId, "The session must still be running.");
    }

    /// <summary>
    /// The signal is broadcast to every processor on the cluster; one that cannot be read must not
    /// take a running event's monitor down with it.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task EventShutdown_WithAnUnreadablePayload_IsIgnored()
    {
        var (monitor, attempts) = await MonitorOnASessionAsync();

        monitor.HandleEventShutdown("not json at all");

        Assert.IsEmpty(attempts);
        Assert.AreEqual(SessionId, monitor.SessionId);
    }

    private async Task<(DebugSessionMonitor Monitor, List<int> Attempts)> MonitorOnASessionAsync()
    {
        var attempts = new List<int>();
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory());
        await monitor.ProcessAsync(SessionId, TestContext.CancellationToken);
        monitor.OnPersistFinishedSession = f => { attempts.Add(f.SessionId); return true; };
        return (monitor, attempts);
    }

    #endregion

    #region Session change steps failing independently

    /// <summary>
    /// The session id is taken here, so every later session-change message for it returns early on
    /// the keep-alive path - nothing gets a second attempt. One step failing must not carry off the
    /// others: without the cached session id a later restart cannot tell a resume from a new
    /// session and takes the destructive path, losing the lap history.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task NewSession_WhenAdoptingItOnTheContextFails_StillTakesTheSessionAndCachesIt()
    {
        var probe = CreateSessionContextProbe();
        probe.Context.Setup(x => x.NewSessionWithLockHeldAsync(It.IsAny<int>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("database is down"));
        var cache = CreateCache(currentSessionId: null);
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory(), probe.Context.Object, cache.Mux);

        await monitor.ProcessAsync(SessionChange(42, "Race"));

        Assert.AreEqual(42, monitor.SessionId);
        Assert.AreEqual("42", cache.CachedCurrentSession, "The session still has to be cached for the next restart.");
    }

    /// <summary>
    /// Guessing "resume" on a cache fault would keep a finished session's data for the rest of the
    /// event; falling through to a new session costs only the lap history, which comes back a lap
    /// later.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task FirstSessionChange_WhenTheCachedSessionCannotBeRead_TakesTheNewSessionPath()
    {
        var probe = CreateSessionContextProbe();
        var cache = CreateCache(currentSessionId: 42);
        cache.Database.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory(), probe.Context.Object, cache.Mux);

        await monitor.ProcessAsync(SessionChange(42, "Race"));

        CollectionAssert.AreEqual(new[] { (42, "Race") }, probe.Created.ToArray());
        Assert.IsEmpty(probe.Resumed);
    }

    /// <summary>
    /// Caching the current session is a best effort; a cache that will not take it costs the next
    /// restart its lap history but must not stop the session being adopted.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task NewSession_WhenTheCurrentSessionCannotBeCached_StillAdoptsTheSession()
    {
        var probe = CreateSessionContextProbe();
        var cache = CreateCache(currentSessionId: null, writesFail: true);
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory(), probe.Context.Object, cache.Mux);

        await monitor.ProcessAsync(SessionChange(42, "Race"));

        Assert.AreEqual(42, monitor.SessionId);
        CollectionAssert.AreEqual(new[] { (42, "Race") }, probe.Created.ToArray());
    }

    /// <summary>
    /// 999999 is the timing system's "no session"; taking it would finalize the running session and
    /// clear the field.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task SessionChange_ToThePlaceholderSession_IsIgnored()
    {
        var probe = CreateSessionContextProbe();
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory(), probe.Context.Object, CreateCache(null).Mux);
        await monitor.ProcessAsync(SessionChange(42, "Race"));
        probe.Created.Clear();

        await monitor.ProcessAsync(SessionChange(999999, "None"));

        Assert.AreEqual(42, monitor.SessionId);
        Assert.IsEmpty(probe.Created);
    }

    #endregion

    #region Startup

    /// <summary>
    /// The shutdown signal is broadcast when the orchestrator retires a processor, and reacting to
    /// it is the only chance a running session gets to be written out before the pod goes away.
    /// </summary>
    /// <remarks>
    /// Scope: this covers the subscribe/unsubscribe pair only, not the handler wiring.
    /// <c>ISubscriber.SubscribeAsync</c> is mocked and returns null, so the <c>ch.OnMessage(HandleEventShutdown)</c>
    /// that follows it in <c>SessionMonitor.EnsureEventShutdownSubscriptionAsync</c> throws a
    /// NullReferenceException, which that method's catch logs and swallows. A regression that dropped the
    /// OnMessage call would not fail this test. <c>ChannelMessageQueue</c> is sealed with no public
    /// constructor, so there is nothing to hand back in its place; the handler itself is covered directly
    /// by the EventShutdown_* tests above, which call HandleEventShutdown.
    /// </remarks>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Start_SubscribesToTheEventShutdownSignal()
    {
        var probe = CreateStartupProbe(CreateSessionContextProbe().Context.Object);

        await probe.RunStartupAsync();

        probe.Subscriber.Verify(x => x.UnsubscribeAsync(
            It.Is<RedisChannel>(c => c == Consts.EVENT_SHUTDOWN_SIGNAL), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()), Times.Once,
            "The previous subscription has to be dropped so a reconnect does not leave two handlers.");
        probe.Subscriber.Verify(x => x.SubscribeAsync(
            It.Is<RedisChannel>(c => c == Consts.EVENT_SHUTDOWN_SIGNAL), It.IsAny<CommandFlags>()), Times.Once);
    }

    /// <summary>
    /// Class colors come from the database and are cosmetic. A database that is not up yet at start
    /// must not stop the monitor, which is what finalizes every session for the event.
    /// </summary>
    /// <remarks>Same scope limit as <see cref="Start_SubscribesToTheEventShutdownSignal"/>.</remarks>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Start_WhenClassMetadataCannotBeLoaded_StillSubscribesToTheShutdownSignal()
    {
        var contextProbe = CreateSessionContextProbe();
        contextProbe.Context.Setup(x => x.SetSessionClassMetadata()).Throws(new InvalidOperationException("database is down"));
        var probe = CreateStartupProbe(contextProbe.Context.Object);

        await probe.RunStartupAsync();

        probe.Subscriber.Verify(x => x.SubscribeAsync(
            It.Is<RedisChannel>(c => c == Consts.EVENT_SHUTDOWN_SIGNAL), It.IsAny<CommandFlags>()), Times.Once);
    }

    /// <summary>
    /// Drives the monitor's startup and stops it as the subscription is taken out, so the
    /// finish-check loop - which is otherwise on a five second timer - exits on its own condition
    /// without anything waiting on the clock.
    /// </summary>
    private sealed class StartupProbe
    {
        public required DebugSessionMonitor Monitor { get; init; }
        public required Mock<ISubscriber> Subscriber { get; init; }
        public required CancellationTokenSource Stopping { get; init; }

        public async Task RunStartupAsync()
        {
            await Monitor.StartAsync(Stopping.Token);
            await Monitor.ExecuteTask!;
        }
    }

    private static StartupProbe CreateStartupProbe(SessionContext sessionContext)
    {
        var stopping = new CancellationTokenSource();
        var subscriber = new Mock<ISubscriber>();
        subscriber.Setup(x => x.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<CommandFlags>()))
            .Callback(() => stopping.Cancel());

        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(new Mock<IDatabase>().Object);
        mux.Setup(x => x.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);

        return new StartupProbe
        {
            Monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory(), sessionContext, mux.Object),
            Subscriber = subscriber,
            Stopping = stopping,
        };
    }

    #endregion

    #region Helpers

    private static TimingMessage SessionChange(int sessionId, string name)
    {
        var session = new Session { Id = sessionId, EventId = EventId, Name = name, IsLive = true };
        return new TimingMessage(Consts.EVENT_SESSION_CHANGED_TYPE, JsonSerializer.Serialize(session), sessionId, DateTime.UtcNow);
    }

    /// <summary>
    /// Adds the results of the session that really ran, which is where the control log of an event
    /// belongs. Started before the harness's own session, so it is the one a carried-over log lands
    /// on regardless of the order the rows come back in.
    /// </summary>
    private async Task SeedResultsForTheSessionThatRanAsync(PersistenceHarness harness, int controlLogEntries)
    {
        await using var db = harness.CreateDb();
        db.Sessions.Add(new Session
        {
            Id = RanSessionId,
            EventId = EventId,
            Name = "Race",
            StartTime = new DateTime(2026, 4, 26, 8, 0, 0, DateTimeKind.Utc),
        });
        db.SessionResults.Add(new RedMist.Database.Models.SessionResult
        {
            EventId = EventId,
            SessionId = RanSessionId,
            Start = new DateTime(2026, 4, 26, 8, 0, 0, DateTimeKind.Utc),
            SessionState = StateWith(cars: 3, entries: 3),
            ControlLogs = [.. Enumerable.Range(0, controlLogEntries).Select(i => new ControlLogEntry { OrderId = i })],
        });
        await db.SaveChangesAsync(TestContext.CancellationToken);
    }

    private static SessionState StateWith(int cars = 0, int entries = 0, int flags = 0) => new()
    {
        EventId = EventId,
        SessionId = SessionId,
        CarPositions = [.. Enumerable.Range(0, cars).Select(i => new CarPosition { Number = i.ToString() })],
        EventEntries = [.. Enumerable.Range(0, entries).Select(i => new EventEntry { Number = i.ToString() })],
        FlagDurations = [.. Enumerable.Range(0, flags).Select(_ => new FlagDuration { Flag = Flags.Green })],
    };

    /// <summary>
    /// A monitor whose result writing is the real one, over an in-memory database seeded with the
    /// session row the write looks for.
    /// </summary>
    private sealed class PersistingSessionMonitor : SessionMonitor
    {
        public PersistingSessionMonitor(IConfiguration configuration, IDbContextFactory<TsContext> tsContext,
            SessionContext sessionContext, IConnectionMultiplexer cacheMux)
            : base(configuration, tsContext, new DebugLoggerFactory(), sessionContext, cacheMux) { }

        public bool CallPersist(FinishedSession finished) => PersistFinishedSession(finished);

        protected override Task SetSessionAsLiveAsync(int eventId, int sessionId) => Task.CompletedTask;

        protected override Task SaveLastUpdatedTimestampAsync(int eventId, int sessionId, CancellationToken stoppingToken = default)
            => Task.CompletedTask;
    }

    private sealed class PersistenceHarness
    {
        public required PersistingSessionMonitor Monitor { get; init; }
        public required Mock<IDatabase> Cache { get; init; }
        public required Func<TsContext> CreateDb { get; init; }
        public int ControlLogEntryCount { get; set; }
    }

    private async Task<PersistenceHarness> CreateHarnessAsync(int controlLogEntries = 0)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        var factory = new TestDbContextFactory(optionsBuilder.Options);

        await using (var seed = factory.CreateDbContext())
        {
            seed.Sessions.Add(new Session
            {
                Id = SessionId,
                EventId = EventId,
                Name = "Race",
                StartTime = new DateTime(2026, 4, 26, 9, 0, 0, DateTimeKind.Utc),
                IsLive = true,
            });
            await seed.SaveChangesAsync(TestContext.CancellationToken);
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", EventId.ToString() } })
            .Build();
        var sessionContext = new SessionContext(configuration, factory, new DebugLoggerFactory(),
            new Mock<ICarLapHistoryService>().Object);

        var database = new Mock<IDatabase>();
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);

        var harness = new PersistenceHarness
        {
            Monitor = new PersistingSessionMonitor(configuration, factory, sessionContext, mux.Object),
            Cache = database,
            CreateDb = factory.CreateDbContext,
            ControlLogEntryCount = controlLogEntries,
        };

        database.Setup(x => x.StringGet(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns(() => JsonSerializer.Serialize(new CarControlLogs
            {
                CarNumber = "1",
                ControlLogEntries = [.. Enumerable.Range(0, harness.ControlLogEntryCount).Select(i => new ControlLogEntry { OrderId = i })],
            }));

        return harness;
    }

    private sealed record SessionContextProbe(
        Mock<SessionContext> Context,
        ConcurrentQueue<(int, string)> Created,
        ConcurrentQueue<(int, string)> Resumed);

    private static SessionContextProbe CreateSessionContextProbe()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", EventId.ToString() } })
            .Build();

        var mock = new Mock<SessionContext>(config, CreateDbContextFactory(), new DebugLoggerFactory(),
            new Mock<ICarLapHistoryService>().Object, TimeProvider.System)
        {
            CallBase = true
        };

        var created = new ConcurrentQueue<(int, string)>();
        var resumed = new ConcurrentQueue<(int, string)>();
        mock.Setup(x => x.NewSessionWithLockHeldAsync(It.IsAny<int>(), It.IsAny<string>()))
            .Callback<int, string>((id, name) => created.Enqueue((id, name)))
            .Returns(Task.CompletedTask);
        mock.Setup(x => x.ResumeSessionWithLockHeldAsync(It.IsAny<int>(), It.IsAny<string>()))
            .Callback<int, string>((id, name) => resumed.Enqueue((id, name)))
            .Returns(Task.CompletedTask);
        mock.Setup(x => x.ClearLapHistoryAsync()).Returns(Task.CompletedTask);
        mock.Setup(x => x.SetSessionClassMetadata());
        return new SessionContextProbe(mock, created, resumed);
    }

    private sealed class CacheProbe
    {
        public required IConnectionMultiplexer Mux { get; init; }
        public required Mock<IDatabase> Database { get; init; }
        public string? CachedCurrentSession { get; set; }
    }

    private static CacheProbe CreateCache(int? currentSessionId, bool writesFail = false)
    {
        var database = new Mock<IDatabase>();
        database.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(currentSessionId.HasValue ? (RedisValue)currentSessionId.Value.ToString() : RedisValue.Null);

        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        var probe = new CacheProbe { Mux = mux.Object, Database = database };

        if (writesFail)
        {
            database.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));
            database.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
                .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));
        }
        else
        {
            database.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((_, value, _, _, _, _) => probe.CachedCurrentSession = value.ToString())
                .ReturnsAsync(true);
            database.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
                .Callback<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags>((_, value, _, _, _) => probe.CachedCurrentSession = value.ToString())
                .ReturnsAsync(true);
        }

        return probe;
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        return new TestDbContextFactory(optionsBuilder.Options);
    }

    #endregion
}
