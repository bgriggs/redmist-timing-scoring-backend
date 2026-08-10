using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Models;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.ExternalDataCollection.Clients;
using RedMist.ExternalDataCollection.Models;
using RedMist.ExternalDataCollection.Services;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.InCarVideo;
using StackExchange.Redis;
using System.Net;
using System.Reflection;

namespace RedMist.TimingAndScoringService.Tests.ExternalDataCollection.Services;

/// <summary>
/// Covers the Sentinel poll loop itself: event gating, payload mapping, SVN reachability checks,
/// cross-poll add/remove diffing and the loop's failure handling. The sibling
/// <see cref="SentinelStatusServiceTests"/> class exercises the publish helpers in isolation.
/// </summary>
[TestClass]
public class SentinelStatusServicePollingTests
{
    /// <summary>Exposes the protected background-service entry point so the loop can be driven directly.</summary>
    private sealed class TestableSentinelStatusService(ILoggerFactory loggerFactory, IConnectionMultiplexer cacheMux,
        IHubContext<StatusHub> hubContext, SentinelClient sentinelClient, EventsChecker eventsChecker,
        ExternalTelemetryClient externalTelemetryClient, IHttpClientFactory httpClientFactory)
        : SentinelStatusService(loggerFactory, cacheMux, hubContext, sentinelClient, eventsChecker,
            externalTelemetryClient, httpClientFactory)
    {
        public Task RunAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
    }

    private Mock<IConnectionMultiplexer> mockCacheMux = null!;
    private Mock<IHubContext<StatusHub>> mockHubContext = null!;
    private Mock<SentinelClient> mockSentinelClient = null!;
    private Mock<EventsChecker> mockEventsChecker = null!;
    private Mock<ExternalTelemetryClient> mockTelemetryClient = null!;
    private Mock<IHttpClientFactory> mockHttpClientFactory = null!;
    private TestableSentinelStatusService service = null!;
    private StubHttpMessageHandler svnHandler = null!;

    private readonly List<List<VideoMetadata>> publishedVideos = [];
    private readonly List<List<DriverInfo>> publishedDrivers = [];
    private readonly List<CancellationToken> publishTokens = [];

    private CancellationTokenSource? loopCts;
    private CancellationToken loopToken;
    private int iterationBudget;
    private int iterationsStarted;

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());

        mockCacheMux = new Mock<IConnectionMultiplexer>();
        mockHubContext = new Mock<IHubContext<StatusHub>>();
        mockHttpClientFactory = new Mock<IHttpClientFactory>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "SentinelApiUrl", "https://sentinel.test" },
                { "Server:ExtTelemUrl", "https://exttelem.test" },
                { "Keycloak:AuthServerUrl", "https://keycloak.test" },
                { "Keycloak:Realm", "test-realm" },
                { "Keycloak:ClientId", "test-client" },
                { "Keycloak:ClientSecret", "test-secret" }
            })
            .Build();

        mockEventsChecker = new Mock<EventsChecker>(MockBehavior.Loose, mockCacheMux.Object,
            Mock.Of<IDbContextFactory<TsContext>>(), Mock.Of<HybridCache>());
        mockSentinelClient = new Mock<SentinelClient>(configuration);
        mockTelemetryClient = new Mock<ExternalTelemetryClient>(configuration);

        mockTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), It.IsAny<CancellationToken>()))
            .Callback<List<VideoMetadata>, CancellationToken>((v, t) => { publishedVideos.Add(v); publishTokens.Add(t); })
            .ReturnsAsync(true);
        mockTelemetryClient
            .Setup(x => x.UpdateDriversAsync(It.IsAny<List<DriverInfo>>(), It.IsAny<CancellationToken>()))
            .Callback<List<DriverInfo>, CancellationToken>((d, t) =>
            {
                publishedDrivers.Add(d);
                publishTokens.Add(t);
                // The driver publish is the last thing a poll does, so stopping here lets the whole
                // poll body run against a live token, the way it does in production.
                if (iterationsStarted >= iterationBudget)
                {
                    loopCts?.Cancel();
                }
            })
            .ReturnsAsync(true);

        // Unless a test says otherwise, every SVN URL probe succeeds.
        SetSvnResponse(StubHttpMessageHandler.Status(HttpStatusCode.OK));

        service = new TestableSentinelStatusService(loggerFactory.Object, mockCacheMux.Object, mockHubContext.Object,
            mockSentinelClient.Object, mockEventsChecker.Object, mockTelemetryClient.Object, mockHttpClientFactory.Object)
        {
            // Cancellation, not the clock, ends the loop in these tests.
            UpdateInterval = TimeSpan.Zero
        };
    }

    #region Event gating

    [TestMethod]
    public async Task ExecuteAsync_NoCurrentEvents_SkipsSentinelPollAndPublish()
    {
        ArrangeStreams([StreamOf("111", "Alice", youTube: "https://youtu.be/a")]);
        var cts = ArrangeIterations(1, _ => []);

        await RunLoopAsync(cts);

        mockSentinelClient.Verify(x => x.GetStreamsAsync(), Times.Never);
        Assert.IsEmpty(publishedVideos, "Nothing should be published when no event is live");
        Assert.IsEmpty(publishedDrivers);
    }

    [TestMethod]
    public async Task ExecuteAsync_AlreadyCanceled_DoesNotPollAtAll()
    {
        var cts = ArrangeIterations(1);
        await cts.CancelAsync();

        await RunLoopAsync(cts);

        mockEventsChecker.Verify(x => x.GetCurrentEventsAsync(), Times.Never);
        mockSentinelClient.Verify(x => x.GetStreamsAsync(), Times.Never);
    }

    [TestMethod]
    public async Task ExecuteAsync_EventGoesLiveAfterAnIdlePoll_StartsPublishing()
    {
        // The service normally starts before the relay connects, so an idle poll must not stop it
        // from picking the event up on a later pass.
        ArrangeStreams([StreamOf("111", "Alice", youTube: "https://youtu.be/a")]);
        var cts = ArrangeIterations(2, iteration => iteration == 1 ? [] : OneEvent());

        await RunLoopAsync(cts);

        mockSentinelClient.Verify(x => x.GetStreamsAsync(), Times.Once);
        Assert.HasCount(1, publishedVideos);
        Assert.AreEqual(111u, publishedVideos[0].Single().TransponderId);
    }

    [TestMethod]
    public async Task ExecuteAsync_EventsRemainLive_PollsSentinelEveryIteration()
    {
        ArrangeStreams([StreamOf("111", "Alice", youTube: "https://youtu.be/a")]);
        var cts = ArrangeIterations(3);

        await RunLoopAsync(cts);

        mockSentinelClient.Verify(x => x.GetStreamsAsync(), Times.Exactly(3));
        Assert.HasCount(3, publishedVideos);
    }

    #endregion

    #region Payload mapping

    [TestMethod]
    public async Task ExecuteAsync_YouTubeStream_MapsToLiveSentinelMetadataAndDriver()
    {
        ArrangeStreams([StreamOf("1329228", "Alice Smith", youTube: "https://youtu.be/abc")]);
        var cts = ArrangeIterations(1);

        await RunLoopAsync(cts);

        Assert.HasCount(1, publishedVideos);
        var video = publishedVideos[0].Single();
        Assert.AreEqual(1329228u, video.TransponderId, "Transponder string from the feed should be parsed");
        Assert.IsTrue(video.UseTransponderId, "Sentinel streams are matched by transponder, not car number");
        Assert.AreEqual(VideoSystemType.Sentinel, video.SystemType);
        Assert.IsTrue(video.IsLive);
        var destination = video.Destinations.Single();
        Assert.AreEqual(VideoDestinationType.Youtube, destination.Type);
        Assert.AreEqual("https://youtu.be/abc", destination.Url);

        var driver = publishedDrivers[0].Single();
        Assert.AreEqual("Alice Smith", driver.DriverName);
        Assert.AreEqual(1329228u, driver.TransponderId);
    }

    [TestMethod]
    public async Task ExecuteAsync_NoUsableUrls_PublishesMetadataWithoutDestinations()
    {
        // Whitespace and empty URLs are both treated as "no stream available".
        ArrangeStreams([StreamOf("111", "Alice", youTube: "   ", svn: "")]);
        var cts = ArrangeIterations(1);

        await RunLoopAsync(cts);

        var video = publishedVideos[0].Single();
        Assert.IsEmpty(video.Destinations);
        Assert.HasCount(1, publishedDrivers[0], "Driver info is published even without a video destination");
    }

    [TestMethod]
    public async Task ExecuteAsync_SvnUrlReachable_AddsDirectSrtDestination()
    {
        ArrangeStreams([StreamOf("111", "Alice", svn: "https://svn.test/live/111")]);
        var cts = ArrangeIterations(1);

        await RunLoopAsync(cts);

        var destination = publishedVideos[0].Single().Destinations.Single();
        Assert.AreEqual(VideoDestinationType.DirectSrt, destination.Type);
        Assert.AreEqual("https://svn.test/live/111", destination.Url);
        Assert.HasCount(1, svnHandler.Requests, "The SVN URL should be probed before it is advertised");
    }

    [TestMethod]
    public async Task ExecuteAsync_SvnUrlNotFound_OmitsDirectSrtDestination()
    {
        // Sentinel routinely reports an SVN URL that 404s; it must not be advertised to the UI.
        SetSvnResponse(StubHttpMessageHandler.Status(HttpStatusCode.NotFound));
        ArrangeStreams([StreamOf("111", "Alice", svn: "https://svn.test/live/111")]);
        var cts = ArrangeIterations(1);

        await RunLoopAsync(cts);

        var video = publishedVideos[0].Single();
        Assert.IsEmpty(video.Destinations);
        Assert.HasCount(1, svnHandler.Requests);
    }

    [TestMethod]
    public async Task ExecuteAsync_SvnUrlProbeThrows_StillPublishesTheYouTubeDestination()
    {
        SetSvnResponse(StubHttpMessageHandler.Throws(new HttpRequestException("connection refused")));
        ArrangeStreams([StreamOf("111", "Alice", youTube: "https://youtu.be/a", svn: "https://svn.test/live/111")]);
        var cts = ArrangeIterations(1);

        await RunLoopAsync(cts);

        // The failed probe is swallowed: the YouTube destination still gets published.
        var video = publishedVideos[0].Single();
        var destination = video.Destinations.Single();
        Assert.AreEqual(VideoDestinationType.Youtube, destination.Type);
    }

    [TestMethod]
    public async Task ExecuteAsync_SvnUrlNotAbsolute_OmitsDirectSrtWithoutIssuingRequest()
    {
        ArrangeStreams([StreamOf("111", "Alice", svn: "not-a-url")]);
        var cts = ArrangeIterations(1);

        await RunLoopAsync(cts);

        Assert.IsEmpty(publishedVideos[0].Single().Destinations);
        Assert.IsEmpty(svnHandler.Requests, "A malformed URL fails before any request is sent");
    }

    [TestMethod]
    public async Task ExecuteAsync_BothUrlsUsable_PublishesYouTubeAndSrtDestinations()
    {
        ArrangeStreams([StreamOf("111", "Alice", youTube: "https://youtu.be/a", svn: "https://svn.test/live/111")]);
        var cts = ArrangeIterations(1);

        await RunLoopAsync(cts);

        var destinations = publishedVideos[0].Single().Destinations;
        Assert.HasCount(2, destinations);
        Assert.AreEqual(VideoDestinationType.Youtube, destinations[0].Type);
        Assert.AreEqual(VideoDestinationType.DirectSrt, destinations[1].Type);
    }

    [TestMethod]
    [DataRow("PROD")]
    [DataRow("PROD 42")]
    [DataRow("PRODUCTION CAM")]
    public async Task ExecuteAsync_ProductionCameraName_PublishesVideoButNoDriver(string driverName)
    {
        // "PROD" streams are facility cameras, not a competitor, so no driver name should be attached to the car.
        ArrangeStreams([StreamOf("111", driverName, youTube: "https://youtu.be/a")]);
        var cts = ArrangeIterations(1);

        await RunLoopAsync(cts);

        Assert.HasCount(1, publishedVideos[0], "The camera feed itself is still published");
        Assert.IsEmpty(publishedDrivers[0]);
    }

    [TestMethod]
    [DataRow("prod cam")]
    [DataRow("Prod Cam")]
    [DataRow("REPRODUCTION")]
    public async Task ExecuteAsync_NameOnlyResemblingProduction_IsTreatedAsDriver(string driverName)
    {
        // The filter is a plain StartsWith with no StringComparison - a culture-sensitive, case-sensitive
        // prefix match - so these are all real drivers.
        ArrangeStreams([StreamOf("111", driverName, youTube: "https://youtu.be/a")]);
        var cts = ArrangeIterations(1);

        await RunLoopAsync(cts);

        Assert.AreEqual(driverName, publishedDrivers[0].Single().DriverName);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("abc")]
    [DataRow("-5")]
    [DataRow("99999999999")]
    public async Task ExecuteAsync_UnparsableTransponderId_PublishesZeroId(string transponderIdStr)
    {
        // Documents the silent fallback: a bad or out-of-range id is published as 0 rather than dropped.
        ArrangeStreams([StreamOf(transponderIdStr, "Alice", youTube: "https://youtu.be/a")]);
        var cts = ArrangeIterations(1);

        await RunLoopAsync(cts);

        Assert.AreEqual(0u, publishedVideos[0].Single().TransponderId);
        Assert.AreEqual(0u, publishedDrivers[0].Single().TransponderId);
    }

    [TestMethod]
    public async Task ExecuteAsync_DuplicateTransponderIds_PublishesEachStreamSeparately()
    {
        // Two cameras reported for the same transponder are not merged or de-duplicated.
        ArrangeStreams([
            StreamOf("111", "Alice", youTube: "https://youtu.be/a"),
            StreamOf("111", "Alice", youTube: "https://youtu.be/b")]);
        var cts = ArrangeIterations(1);

        await RunLoopAsync(cts);

        Assert.HasCount(2, publishedVideos[0]);
        Assert.IsTrue(publishedVideos[0].TrueForAll(v => v.TransponderId == 111u));
        Assert.HasCount(2, publishedDrivers[0]);
    }

    #endregion

    #region Cross-poll diffing

    [TestMethod]
    public async Task ExecuteAsync_StreamDisappearsBetweenPolls_PublishesClearingEntries()
    {
        ArrangeStreams(
            [StreamOf("111", "Alice", youTube: "https://youtu.be/a"), StreamOf("222", "Bob", youTube: "https://youtu.be/b")],
            [StreamOf("111", "Alice", youTube: "https://youtu.be/a")]);
        var cts = ArrangeIterations(2);

        await RunLoopAsync(cts);

        Assert.HasCount(2, publishedVideos);
        var secondPoll = publishedVideos[1];
        Assert.HasCount(2, secondPoll, "The dropped stream is re-sent as a cleared entry");
        var cleared = secondPoll.Single(v => v.TransponderId == 222u);
        Assert.AreEqual(VideoSystemType.None, cleared.SystemType);
        Assert.IsFalse(cleared.IsLive);
        Assert.IsEmpty(cleared.Destinations);

        var clearedDriver = publishedDrivers[1].Single(d => d.TransponderId == 222u);
        Assert.AreEqual(string.Empty, clearedDriver.DriverName, "An empty name tells the consumer to drop the driver");
    }

    [TestMethod]
    public async Task ExecuteAsync_StreamsUnchangedBetweenPolls_PublishesNoClearingEntries()
    {
        var streams = new List<PublicStreams> { StreamOf("111", "Alice", youTube: "https://youtu.be/a") };
        ArrangeStreams(streams);
        var cts = ArrangeIterations(2);

        await RunLoopAsync(cts);

        Assert.HasCount(2, publishedVideos);
        Assert.HasCount(1, publishedVideos[1]);
        Assert.AreEqual(VideoSystemType.Sentinel, publishedVideos[1].Single().SystemType);
        Assert.HasCount(1, publishedDrivers[1]);
    }

    [TestMethod]
    public async Task ExecuteAsync_AllStreamsDisappear_PublishesOnlyClearingEntries()
    {
        ArrangeStreams(
            [StreamOf("111", "Alice", youTube: "https://youtu.be/a")],
            []);
        var cts = ArrangeIterations(2);

        await RunLoopAsync(cts);

        var cleared = publishedVideos[1].Single();
        Assert.AreEqual(111u, cleared.TransponderId);
        Assert.AreEqual(VideoSystemType.None, cleared.SystemType);
        Assert.AreEqual(string.Empty, publishedDrivers[1].Single().DriverName);
    }

    #endregion

    #region Failure handling

    [TestMethod]
    public async Task ExecuteAsync_SentinelRequestThrows_PublishesNothingAndRecoversOnNextPoll()
    {
        var calls = 0;
        mockSentinelClient.Setup(x => x.GetStreamsAsync()).Returns(() =>
        {
            calls++;
            if (calls == 1)
            {
                throw new HttpRequestException("sentinel unavailable");
            }
            return Task.FromResult(new List<PublicStreams> { StreamOf("111", "Alice", youTube: "https://youtu.be/a") });
        });
        var cts = ArrangeIterations(2);

        await RunLoopAsync(cts);

        Assert.HasCount(1, publishedVideos, "The failed poll publishes nothing but does not kill the loop");
        Assert.AreEqual(111u, publishedVideos[0].Single().TransponderId);
    }

    [TestMethod]
    public async Task ExecuteAsync_EventLookupThrows_RecoversOnNextPoll()
    {
        ArrangeStreams([StreamOf("111", "Alice", youTube: "https://youtu.be/a")]);
        var cts = ArrangeIterations(2, iteration => iteration == 1
            ? throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis down")
            : OneEvent());

        await RunLoopAsync(cts);

        mockSentinelClient.Verify(x => x.GetStreamsAsync(), Times.Once);
        Assert.HasCount(1, publishedVideos);
    }

    [TestMethod]
    public async Task ExecuteAsync_VideoPublishThrows_SkipsDriverPublishAndLosesTheRemoval()
    {
        // The last-published snapshot is only advanced after a successful publish, so a stream that
        // vanishes during a failed poll never gets a clearing entry.
        var videoCalls = 0;
        mockTelemetryClient
            .Setup(x => x.UpdateCarVideosAsync(It.IsAny<List<VideoMetadata>>(), It.IsAny<CancellationToken>()))
            .Returns<List<VideoMetadata>, CancellationToken>((v, _) =>
            {
                videoCalls++;
                if (videoCalls == 1)
                {
                    throw new HttpRequestException("telemetry unavailable");
                }
                publishedVideos.Add(v);
                return Task.FromResult(true);
            });
        ArrangeStreams(
            [StreamOf("111", "Alice", youTube: "https://youtu.be/a")],
            []);
        var cts = ArrangeIterations(2);

        await RunLoopAsync(cts);

        Assert.AreEqual(2, videoCalls);
        Assert.HasCount(1, publishedDrivers, "The driver publish is skipped when the video publish throws");
        Assert.HasCount(1, publishedVideos);
        Assert.IsEmpty(publishedVideos[0], "No clearing entry: the failed poll never became the baseline");
    }

    [TestMethod]
    public async Task ExecuteAsync_MalformedStreamData_DoesNotStopTheLoop()
    {
        // A null driver name from the feed blows up mapping; the poll is lost but the service keeps running.
        ArrangeStreams(
            [new PublicStreams { TransponderIdStr = "111", DriverName = null!, YouTubeUrl = "https://youtu.be/a" }],
            [StreamOf("222", "Bob", youTube: "https://youtu.be/b")]);
        var cts = ArrangeIterations(2);

        await RunLoopAsync(cts);

        Assert.HasCount(1, publishedVideos);
        Assert.AreEqual(222u, publishedVideos[0].Single().TransponderId);
    }

    [TestMethod]
    public async Task ExecuteAsync_Publishes_UsesTheStoppingToken()
    {
        ArrangeStreams([StreamOf("111", "Alice", youTube: "https://youtu.be/a")]);
        var cts = ArrangeIterations(1);

        await RunLoopAsync(cts);

        Assert.HasCount(2, publishTokens);
        Assert.IsTrue(publishTokens.TrueForAll(t => t == loopToken),
            "Publishes must observe the service stopping token so shutdown is not blocked");
    }

    #endregion

    #region UI status request replay

    // NOTE: ProcessUiStatusRequestAsync is currently unreachable in production - nothing subscribes to
    // the UI status command, so a newly connected UI never gets the cached video metadata replayed.
    // These tests pin the behavior for whenever the subscription is wired back up; they are not
    // evidence that the replay works today.

    [TestMethod]
    public async Task ProcessUiStatusRequest_WithCachedMetadata_ReplaysToRequestingConnection()
    {
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<ISingleClientProxy>();
        mockHubContext.Setup(x => x.Clients).Returns(clients.Object);
        clients.Setup(x => x.Client(It.IsAny<string>())).Returns(proxy.Object);
        proxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cached = new List<VideoMetadata> { new() { TransponderId = 111, SystemType = VideoSystemType.Sentinel } };
        SetLastVideoMetadata(cached);

        await InvokeProcessUiStatusRequestAsync("{\"EventId\":7,\"ConnectionId\":\"conn-9\"}");

        clients.Verify(x => x.Client("conn-9"), Times.Once);
        proxy.Verify(x => x.SendCoreAsync("ReceiveInCarVideoMetadata",
            It.Is<object?[]>(args => args.Length == 1 && ReferenceEquals(args[0], cached)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ProcessUiStatusRequest_NoCachedMetadata_SendsNothing()
    {
        var clients = new Mock<IHubClients>();
        mockHubContext.Setup(x => x.Clients).Returns(clients.Object);

        await InvokeProcessUiStatusRequestAsync("{\"EventId\":7,\"ConnectionId\":\"conn-9\"}");

        clients.Verify(x => x.Client(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task ProcessUiStatusRequest_NullCommand_SendsNothing()
    {
        var clients = new Mock<IHubClients>();
        mockHubContext.Setup(x => x.Clients).Returns(clients.Object);
        SetLastVideoMetadata([new VideoMetadata { TransponderId = 111 }]);

        await InvokeProcessUiStatusRequestAsync("null");

        clients.Verify(x => x.Client(It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region Helpers

    private static PublicStreams StreamOf(string transponderIdStr, string driverName, string youTube = "", string svn = "") =>
        new()
        {
            TransponderIdStr = transponderIdStr,
            DriverName = driverName,
            YouTubeUrl = youTube,
            SvnUrl = svn
        };

    private static List<RelayConnectionEventEntry> OneEvent(int eventId = 7) =>
        [new RelayConnectionEventEntry { EventId = eventId, ConnectionId = "relay-1" }];

    /// <summary>
    /// Arranges the event lookup so the poll loop runs <paramref name="iterations"/> times and then exits
    /// through cancellation rather than through the wall clock. The stop is normally requested once the
    /// last poll has published (see the driver publish callback); this only stops a run whose polls never
    /// reach a publish, such as an idle loop with no live event.
    /// </summary>
    private CancellationTokenSource ArrangeIterations(int iterations, Func<int, List<RelayConnectionEventEntry>>? events = null)
    {
        iterationBudget = iterations;
        iterationsStarted = 0;
        loopCts = new CancellationTokenSource();
        mockEventsChecker.Setup(x => x.GetCurrentEventsAsync()).Returns(() =>
        {
            iterationsStarted++;
            if (iterationsStarted > iterationBudget)
            {
                loopCts.Cancel();
            }
            return Task.FromResult(events?.Invoke(iterationsStarted) ?? OneEvent());
        });
        return loopCts;
    }

    /// <summary>
    /// Supplies the Sentinel payload for each poll; the last entry is reused for any further polls.
    /// </summary>
    private void ArrangeStreams(params List<PublicStreams>[] perIteration)
    {
        var call = 0;
        mockSentinelClient.Setup(x => x.GetStreamsAsync()).Returns(() =>
        {
            var index = Math.Min(call, perIteration.Length - 1);
            call++;
            return Task.FromResult(new List<PublicStreams>(perIteration[index]));
        });
    }

    private void SetSvnResponse(StubHttpMessageHandler handler)
    {
        svnHandler = handler;
        mockHttpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(svnHandler, disposeHandler: false));
    }

    private async Task RunLoopAsync(CancellationTokenSource cts)
    {
        // The failsafe stops a loop that ignores cancellation from hanging (or hot spinning) the suite.
        // It is asserted on so that such a regression fails the test instead of passing slowly.
        using var failsafe = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, failsafe.Token);
        loopToken = linked.Token;
        try
        {
            await service.RunAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the inter-poll delay throws once the service is stopped.
        }

        Assert.IsFalse(failsafe.IsCancellationRequested, "The poll loop did not exit when cancellation was requested");
    }

    private void SetLastVideoMetadata(List<VideoMetadata> metadata)
    {
        typeof(SentinelStatusService).GetField("lastVideoMetadata", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, metadata);
    }

    private async Task InvokeProcessUiStatusRequestAsync(string cmdJson)
    {
        var method = typeof(SentinelStatusService).GetMethod("ProcessUiStatusRequestAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(method, "ProcessUiStatusRequestAsync not found");
        await (Task)method.Invoke(service, [cmdJson])!;
    }

    #endregion
}
