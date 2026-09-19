using BigMission.TestHelpers.Testing;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventOrchestration.Services;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingAndScoringService.Tests.EventOrchestration.Utilities;
using RedMist.TimingAndScoringService.Tests.Shared;

namespace RedMist.TimingAndScoringService.Tests.EventOrchestration.Services;

/// <summary>
/// Covers the orchestrator's backstop for viewer sessions still open when an event is torn down.
/// </summary>
/// <remarks>
/// The event's logger pod normally closes its own sessions when it sees the shutdown signal, and
/// normally gets there first. This is what happens when it does not - it was never scheduled, it had
/// already crashed, or it was killed before the signal reached it - because a session left open is
/// never closed by anything afterwards and grows the event's viewer-minutes without bound.
/// </remarks>
[TestClass]
public class OrchestrationViewerSessionTests
{
    private const int EventId = 42;
    private const string Namespace = "timing-test";
    private static readonly DateTime Origin = new(2026, 9, 19, 14, 0, 0, DateTimeKind.Utc);

    private FakeRedisDatabase redis = null!;
    private TestDbContextFactory dbFactory = null!;
    private FakeKubernetes k8s = null!;

    [TestInitialize]
    public void Setup()
    {
        redis = new FakeRedisDatabase();
        dbFactory = new TestDbContextFactory(new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase($"OrchestrationViewerSessionTests_{Guid.NewGuid()}")
            .Options);
        k8s = new FakeKubernetes();
    }

    private OrchestrationService CreateService()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var eventsChecker = new Mock<EventsChecker>(redis.Mux.Object, dbFactory, new FakeHybridCache());
        return new OrchestrationService(new DebugLoggerFactory(), redis.Mux.Object, dbFactory,
            eventsChecker.Object, configuration, () => k8s.Object);
    }

    private void SeedSession(string connectionId, int eventId = EventId, DateTime? endUtc = null,
        ViewerSessionEndReason? endReason = null)
    {
        using var db = dbFactory.CreateDbContext();
        db.EventViewerSessions.Add(new EventViewerSession
        {
            EventId = eventId,
            ConnectionId = connectionId,
            ClientType = "Web",
            StartUtc = Origin.AddMinutes(-30),
            EndUtc = endUtc,
            EndReason = endReason,
        });
        db.SaveChanges();
    }

    private List<EventViewerSession> Sessions()
    {
        using var db = dbFactory.CreateDbContext();
        return [.. db.EventViewerSessions.OrderBy(s => s.Id)];
    }

    private Task DisposeEventAsync() => CreateService().DisposeEventAsync(
        new RelayConnectionEventEntry { EventId = EventId, OrganizationId = 7 },
        k8s.Object, Namespace, new V1JobList { Items = [] }, CancellationToken.None);

    [TestMethod]
    public async Task DisposeEventAsync_ClosesOpenViewerSessionsForTheExpiredEvent()
    {
        SeedSession("conn-a");
        SeedSession("conn-b");

        await DisposeEventAsync();

        var sessions = Sessions();
        Assert.IsTrue(sessions.All(s => s.EndUtc != null));
        Assert.IsTrue(sessions.All(s => s.EndReason == ViewerSessionEndReason.EventTeardown));
    }

    /// <summary>
    /// The logger normally gets there first. Its end time is the accurate one, and reasserting a
    /// teardown over it would both move the time and lose why the session actually ended.
    /// </summary>
    [TestMethod]
    public async Task DisposeEventAsync_LeavesAlreadyClosedSessionsAlone()
    {
        var closedAt = Origin.AddMinutes(-5);
        SeedSession("conn-done", endUtc: closedAt, endReason: ViewerSessionEndReason.Unsubscribed);

        await DisposeEventAsync();

        var session = Sessions().Single();
        Assert.AreEqual(closedAt, session.EndUtc);
        Assert.AreEqual(ViewerSessionEndReason.Unsubscribed, session.EndReason);
    }

    /// <summary>
    /// Events expire independently and several run at once, so tearing one down must not close
    /// another's sessions.
    /// </summary>
    [TestMethod]
    public async Task DisposeEventAsync_LeavesAnotherEventsSessionsOpen()
    {
        SeedSession("conn-other", eventId: EventId + 1);

        await DisposeEventAsync();

        Assert.IsNull(Sessions().Single().EndUtc);
    }

    [TestMethod]
    public async Task DisposeEventAsync_WithNoOpenSessions_DoesNothing()
    {
        await DisposeEventAsync();

        Assert.IsEmpty(Sessions());
    }
}
