using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;

namespace RedMist.EventProcessor.Tests.EventStatus;

/// <summary>
/// After a restart the relay replays its cache, which opens with a session change naming the session
/// that was already running. Treating that as a new session re-creates it and clears the lap history -
/// the only source of each car's last lap time on a restart. These cover telling the two apart.
/// </summary>
[TestClass]
public class SessionMonitorResumeTests
{
    private const int EventId = 1;

    [TestMethod]
    public async Task FirstSessionChange_MatchingCachedSession_ResumesWithoutRecreatingSession()
    {
        var ctx = CreateSessionContext();
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory(), ctx.Context.Object, CreateCacheMux(currentSessionId: 42));

        await monitor.ProcessAsync(SessionChange(42, "Race"));

        Assert.AreEqual(42, monitor.SessionId);
        await WaitForAsync(() => ctx.Resumed.Count == 1);
        CollectionAssert.AreEqual(new[] { (42, "Race") }, ctx.Resumed.ToArray());
        Assert.IsEmpty(ctx.Created, "A resume must not re-create the session");
    }

    [TestMethod]
    public async Task FirstSessionChange_DifferentCachedSession_StartsNewSession()
    {
        var ctx = CreateSessionContext();
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory(), ctx.Context.Object, CreateCacheMux(currentSessionId: 41));

        await monitor.ProcessAsync(SessionChange(42, "Race"));

        Assert.AreEqual(42, monitor.SessionId);
        await WaitForAsync(() => ctx.Created.Count == 1);
        CollectionAssert.AreEqual(new[] { (42, "Race") }, ctx.Created.ToArray());
        Assert.IsEmpty(ctx.Resumed);
    }

    /// <summary>
    /// Nothing cached means nothing to resume - a first deploy, or a flushed cache. Falling back to the
    /// new-session path costs the lap history, which is recoverable a lap later.
    /// </summary>
    [TestMethod]
    public async Task FirstSessionChange_NoCachedSession_StartsNewSession()
    {
        var ctx = CreateSessionContext();
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory(), ctx.Context.Object, CreateCacheMux(currentSessionId: null));

        await monitor.ProcessAsync(SessionChange(42, "Race"));

        await WaitForAsync(() => ctx.Created.Count == 1);
        CollectionAssert.AreEqual(new[] { (42, "Race") }, ctx.Created.ToArray());
        Assert.IsEmpty(ctx.Resumed);
    }

    /// <summary>
    /// The outgoing session's lap history has to be gone before this returns. The relay sends the
    /// session change immediately ahead of its cached data set, and if that batch were applied while
    /// the history was still there it would restore the old session's lap times onto the new cars.
    /// </summary>
    [TestMethod]
    public async Task NewSession_ClearsLapHistoryBeforeReturning()
    {
        var ctx = CreateSessionContext();
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory(), ctx.Context.Object, CreateCacheMux(currentSessionId: 41));

        await monitor.ProcessAsync(SessionChange(42, "Race"));

        ctx.Context.Verify(x => x.ClearLapHistoryAsync(), Times.AtLeastOnce);
    }

    /// <summary>
    /// Only the first session change after startup can be a resume. A later one is a real session
    /// change and must clear the previous session's data even if the cache happens to name it.
    /// </summary>
    [TestMethod]
    public async Task LaterSessionChange_MatchingCachedSession_StartsNewSession()
    {
        var ctx = CreateSessionContext();
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory(), ctx.Context.Object, CreateCacheMux(currentSessionId: 43));

        // First change is not the cached session, so it is adopted as a new session.
        await monitor.ProcessAsync(SessionChange(42, "Practice"));
        await monitor.ProcessAsync(SessionChange(43, "Race"));

        Assert.AreEqual(43, monitor.SessionId);
        await WaitForAsync(() => ctx.Created.Count >= 2);
        // Unordered: each adoption is dispatched onto its own task, so which lands first is not fixed.
        CollectionAssert.AreEquivalent(new[] { (42, "Practice"), (43, "Race") }, ctx.Created.ToArray());
        Assert.IsEmpty(ctx.Resumed);
    }

    /// <summary>
    /// Repeats of the session already being run stay on the keep-alive path rather than resuming again.
    /// </summary>
    [TestMethod]
    public async Task RepeatedSessionChange_ForCurrentSession_DoesNotReadoptSession()
    {
        var ctx = CreateSessionContext();
        var monitor = new DebugSessionMonitor(EventId, CreateDbContextFactory(), ctx.Context.Object, CreateCacheMux(currentSessionId: 42));

        await monitor.ProcessAsync(SessionChange(42, "Race"));
        await WaitForAsync(() => ctx.Resumed.Count == 1);
        await monitor.ProcessAsync(SessionChange(42, "Race"));

        await Task.Delay(100);
        Assert.HasCount(1, ctx.Resumed, "The session was already adopted, so it must not be adopted again");
        Assert.IsEmpty(ctx.Created);
    }

    private static TimingMessage SessionChange(int sessionId, string name)
    {
        var session = new Session { Id = sessionId, EventId = EventId, Name = name, IsLive = true };
        return new TimingMessage(Consts.EVENT_SESSION_CHANGED_TYPE, JsonSerializer.Serialize(session), sessionId, DateTime.UtcNow);
    }

    /// <summary>
    /// The session context is adopted on a background task, since the caller holds the pipeline's write
    /// lock, so the assertions have to wait for it rather than assume it has already run.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        Assert.Fail("Timed out waiting for the session context to be updated");
    }

    /// <summary>
    /// Adoptions are recorded into thread-safe lists as they happen rather than read back off the
    /// mock afterwards: they run on background tasks, and Moq's invocation list is not safe to
    /// enumerate while those are still appending to it.
    /// </summary>
    private sealed record SessionContextProbe(
        Mock<SessionContext> Context,
        ConcurrentQueue<(int, string)> Created,
        ConcurrentQueue<(int, string)> Resumed);

    private static SessionContextProbe CreateSessionContext()
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
        mock.Setup(x => x.NewSessionAsync(It.IsAny<int>(), It.IsAny<string>()))
            .Callback<int, string>((id, name) => created.Enqueue((id, name)))
            .Returns(Task.CompletedTask);
        mock.Setup(x => x.ResumeSessionAsync(It.IsAny<int>(), It.IsAny<string>()))
            .Callback<int, string>((id, name) => resumed.Enqueue((id, name)))
            .Returns(Task.CompletedTask);
        mock.Setup(x => x.ClearLapHistoryAsync()).Returns(Task.CompletedTask);
        mock.Setup(x => x.SetSessionClassMetadata());
        return new SessionContextProbe(mock, created, resumed);
    }

    private static IConnectionMultiplexer CreateCacheMux(int? currentSessionId)
    {
        var database = new Mock<IDatabase>();
        database.Setup(x => x.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(currentSessionId.HasValue ? (RedisValue)currentSessionId.Value.ToString() : RedisValue.Null);

        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        return mux.Object;
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        return new TestDbContextFactory(optionsBuilder.Options);
    }
}
