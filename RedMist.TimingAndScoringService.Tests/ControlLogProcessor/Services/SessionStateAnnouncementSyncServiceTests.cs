using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RedMist.Backend.Shared;
using RedMist.ControlLogProcessor.Services;
using RedMist.ControlLogs.Announcements;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.Tests.ControlLogProcessor.Services;

/// <summary>
/// The periodic full-state sync is the safety net for announcements the stream consumer missed (it reads
/// the stream from its tail). These tests cover endpoint discovery, the URL it builds, and - importantly -
/// the cases where it must leave the store alone rather than wiping entries the stream already delivered.
/// </summary>
[TestClass]
public class SessionStateAnnouncementSyncServiceTests
{
    private const int EventId = 42;

    #region Harness

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage MessagePackResponse(SessionState state) =>
        new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(MessagePackSerializer.Serialize(state)) };

    private static SessionStateAnnouncementSyncService Service(IAnnouncementControlLogStore store,
        string? registeredEndpoint, StubHandler handler, int eventId = EventId)
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(registeredEndpoint is null ? RedisValue.Null : registeredEndpoint);
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["event_id"] = eventId.ToString() }).Build();

        return new SessionStateAnnouncementSyncService(NullLoggerFactory.Instance, mux.Object, httpClientFactory.Object, config, store);
    }

    private static StubHandler Responder(SessionState state) => new(_ => MessagePackResponse(state));

    private static Announcement Ann(string text, string ts) =>
        new() { Timestamp = DateTime.Parse(ts).ToUniversalTime(), Text = text, Priority = "Normal" };

    #endregion

    [TestMethod]
    public async Task SyncAsync_ProcessorEndpointNotRegistered_ReportsNotSyncedAndLeavesTheStore()
    {
        var store = new Mock<IAnnouncementControlLogStore>(MockBehavior.Strict);
        var handler = Responder(new SessionState());
        using var service = Service(store.Object, registeredEndpoint: null, handler);

        Assert.IsFalse(await service.SyncAsync(CancellationToken.None));
        Assert.AreEqual(0, handler.RequestedUrls.Count, "no endpoint means no HTTP call at all");
        store.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow("10.0.0.5:8080", "http://10.0.0.5:8080/status/GetStatus")]
    [DataRow("http://processor:5000", "http://processor:5000/status/GetStatus")]
    [DataRow("http://processor:5000/", "http://processor:5000/status/GetStatus")]
    public async Task SyncAsync_BuildsTheStatusUrlFromTheRegisteredEndpoint(string registered, string expectedUrl)
    {
        // The processor registers a bare host:port in Redis; the scheme is added and a trailing slash must
        // not produce a doubled separator.
        var handler = Responder(new SessionState());
        using var service = Service(new AnnouncementControlLogStore(), registered, handler);

        await service.SyncAsync(CancellationToken.None);

        Assert.AreEqual(expectedUrl, handler.RequestedUrls.Single());
    }

    [TestMethod]
    public async Task SyncAsync_ReplacesTheStoreFromTheFullStatesAnnouncementList()
    {
        var state = new SessionState
        {
            Announcements =
            [
                Ann("GREEN FLAG", "2026-06-05T21:05:00Z"),
                Ann("CAR 14 BEHIND THE WALL AT TURN 10", "2026-06-05T21:00:00Z"),
            ]
        };
        var store = new AnnouncementControlLogStore();
        store.Set([new ControlLogEntry { OrderId = 99, Note = "stale" }]);
        using var service = Service(store, "processor:5000", Responder(state));

        Assert.IsTrue(await service.SyncAsync(CancellationToken.None));

        var entries = store.Get();
        Assert.AreEqual(2, entries.Count);
        // ParseAll orders the announcements oldest-first and numbers them from 1.
        Assert.AreEqual(1, entries[0].OrderId);
        Assert.AreEqual("14", entries[0].Car1);
        Assert.AreEqual("10", entries[0].Corner);
        Assert.AreEqual(2, entries[1].OrderId);
        Assert.AreEqual(string.Empty, entries[1].Car1);
    }

    [TestMethod]
    public async Task SyncAsync_StateWithNoAnnouncements_CountsAsSyncedButDoesNotClearTheStore()
    {
        // The stream consumer may already hold entries this endpoint has not caught up to yet; an empty
        // list here must not wipe them.
        var store = new AnnouncementControlLogStore();
        store.Set([new ControlLogEntry { OrderId = 1, Note = "CODE 60" }]);
        using var service = Service(store, "processor:5000", Responder(new SessionState()));

        Assert.IsTrue(await service.SyncAsync(CancellationToken.None));

        Assert.AreEqual(1, store.Get().Count);
    }

    [TestMethod]
    public async Task SyncAsync_ProcessorNotReachable_ReportsNotSyncedAndLeavesTheStore()
    {
        var store = new AnnouncementControlLogStore();
        store.Set([new ControlLogEntry { OrderId = 1, Note = "CODE 60" }]);
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        using var service = Service(store, "processor:5000", handler);

        Assert.IsFalse(await service.SyncAsync(CancellationToken.None));

        Assert.AreEqual(1, store.Get().Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_EventIdNotConfigured_NeverConnectsToRedis()
    {
        var mux = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        var httpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["event_id"] = "0" }).Build();
        using var service = new SessionStateAnnouncementSyncService(NullLoggerFactory.Instance, mux.Object,
            httpClientFactory.Object, config, new AnnouncementControlLogStore());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        mux.VerifyNoOtherCalls();
        httpClientFactory.VerifyNoOtherCalls();
    }
}
