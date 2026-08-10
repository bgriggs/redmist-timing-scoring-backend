using BigMission.TestHelpers.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Services;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.DriverInformation;
using RedMist.EventProcessor.EventStatus.FlagData;
using RedMist.EventProcessor.EventStatus.Flagtronics;
using RedMist.EventProcessor.EventStatus.InCarDriverMode;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.Multiloop;
using RedMist.EventProcessor.EventStatus.PenaltyEnricher;
using RedMist.EventProcessor.EventStatus.PipelineBlocks;
using RedMist.EventProcessor.EventStatus.PositionEnricher;
using RedMist.EventProcessor.EventStatus.RMonitor;
using RedMist.EventProcessor.EventStatus.SessionMonitoring;
using RedMist.EventProcessor.EventStatus.Video;
using RedMist.EventProcessor.EventStatus.X2;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;

namespace RedMist.EventProcessor.Tests.EventStatus;

/// <summary>
/// Every byte of timing data for an event enters the processor here: the relay writes to a Redis
/// stream and this service is the only reader. What it drops, mis-routes or fails to acknowledge is
/// lost outright, and the endpoint it registers is how the rest of the cluster finds this event.
/// </summary>
[TestClass]
public class EventAggregatorServiceTests
{
    private const int EventId = 7;
    private static readonly string StreamKey = string.Format(Backend.Shared.Consts.EVENT_STATUS_STREAM_KEY, EventId);

    #region Service name

    /// <summary>
    /// The registered endpoint is how the status API reaches this event's processor, so the name
    /// has to be derived from whichever of the two configurations the deployment supplies.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    [DataRow("job_name", "evt-7", "evt-7-service", DisplayName = "Kubernetes job name")]
    [DataRow("ASPNETCORE_URLS", "http://0.0.0.0:8080", "localhost:8080", DisplayName = "Wildcard bind resolves to localhost")]
    [DataRow("ASPNETCORE_URLS", "http://timing-host:5001", "timing-host:5001", DisplayName = "Explicit host")]
    public async Task Startup_RegistersTheEndpointUnderTheConfiguredServiceName(string key, string value, string expected)
    {
        var harness = CreateHarness(new Dictionary<string, string?> { { key, value } });

        await harness.RunAsync();

        Assert.AreEqual(string.Format(Backend.Shared.Consts.EVENT_SERVICE_ENDPOINT, EventId), harness.RegisteredEndpointKey);
        Assert.AreEqual(expected, harness.RegisteredEndpointValue);
    }

    /// <summary>
    /// A processor nothing can address is worse than one that will not start: it would consume the
    /// stream while every client kept asking a service that never answers.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public void Constructor_WithNoWayToDetermineTheServiceName_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CreateHarness([]));
    }

    #endregion

    #region Stream setup

    /// <summary>
    /// Without the consumer group the read fails on every pass and no timing data is ever taken.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Startup_WhenTheStreamDoesNotExist_CreatesItWithTheConsumerGroup()
    {
        var harness = CreateHarness();
        harness.StreamKeyExists = false;

        await harness.RunAsync();

        harness.Database.Verify(x => x.StreamCreateConsumerGroupAsync(
            It.Is<RedisKey>(k => k == StreamKey), It.Is<RedisValue>(g => g == "processor"),
            It.IsAny<RedisValue?>(), true, It.IsAny<CommandFlags>()), Times.Once);
    }

    /// <summary>
    /// The stream outlives the processor - the relay writes to it whether or not anything is
    /// reading - so an existing stream that has lost its consumer group still needs one.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Startup_WhenTheStreamExistsWithoutTheConsumerGroup_CreatesTheGroup()
    {
        var harness = CreateHarness();
        harness.StreamKeyExists = true;

        await harness.RunAsync();

        harness.Database.Verify(x => x.StreamCreateConsumerGroupAsync(
            It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(), true, It.IsAny<CommandFlags>()), Times.Once);
    }

    /// <summary>
    /// A Redis that will not answer must not stop the service coming up; the read loop re-ensures
    /// the stream after each failure.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Startup_WhenTheStreamCannotBeChecked_StillComesUp()
    {
        var harness = CreateHarness();
        harness.Database.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        await harness.RunAsync();

        Assert.IsNotNull(harness.RegisteredEndpointValue, "Startup should have carried on to registering the endpoint.");
    }

    /// <summary>
    /// The relay only sends a full data set when asked. Without this the processor would come up
    /// holding nothing until each car next changed.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Startup_AsksTheRelayForAFullDataSet()
    {
        var harness = CreateHarness();

        await harness.RunAsync();

        Assert.HasCount(1, harness.RelayResets);
        Assert.AreEqual(EventId, harness.RelayResets[0].EventId);
        Assert.IsFalse(harness.RelayResets[0].ForceTimingDataReset,
            "Startup asks for the cached data set, not a reconnect of the timing system.");
    }

    #endregion

    #region Reading the stream

    /// <summary>
    /// The message tag is the only thing that says what kind of data an entry holds and which
    /// session it belongs to. This drives one through to the session state to show it is routed and
    /// not merely accepted.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ReadLoop_RoutesATaggedEntryToTheProcessingPipeline()
    {
        var harness = CreateHarness();
        harness.Entries =
        [
            Entry("1-1", ("rmonitor-7-36", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"")),
        ];

        await harness.RunAsync();

        Assert.AreEqual(Flags.Green, harness.SessionContext.SessionState.CurrentFlag);
        CollectionAssert.AreEqual(new[] { "1-1" }, harness.AcknowledgedIds.ToArray());
    }

    /// <summary>
    /// A tag that cannot be read names neither a type nor a session, so there is nothing to route
    /// it to. It still has to be acknowledged: leaving it pending would have it redelivered on
    /// every pass forever.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ReadLoop_WithAnUnreadableTag_SkipsTheFieldButStillAcknowledgesTheEntry()
    {
        var harness = CreateHarness();
        harness.Entries =
        [
            Entry("1-1", ("rmonitor-7", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"")),
        ];

        await harness.RunAsync();

        Assert.AreEqual(Flags.Unknown, harness.SessionContext.SessionState.CurrentFlag, "Nothing should have been applied.");
        CollectionAssert.AreEqual(new[] { "1-1" }, harness.AcknowledgedIds.ToArray());
    }

    /// <summary>
    /// One unreadable field must not cost the rest of the batch: the relay packs several updates
    /// into an entry and they are all the data there is for that moment.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ReadLoop_WithAMixedEntry_StillRoutesTheReadableFields()
    {
        var harness = CreateHarness();
        harness.Entries =
        [
            Entry("1-1",
                ("garbage", "ignored"),
                ("rmonitor-7-36", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"")),
        ];

        await harness.RunAsync();

        Assert.AreEqual(Flags.Green, harness.SessionContext.SessionState.CurrentFlag);
    }

    /// <summary>
    /// Acknowledging an entry before its contents are processed would lose them to a crash; the ack
    /// is the only record that the entry was handled.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ReadLoop_AcknowledgesEachEntryOnlyAfterItsFieldsAreProcessed()
    {
        var harness = CreateHarness();
        harness.Entries =
        [
            Entry("1-1", ("rmonitor-7-36", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"")),
        ];
        Flags flagAtAck = Flags.Unknown;
        harness.OnAcknowledge = () => flagAtAck = harness.SessionContext.SessionState.CurrentFlag;

        await harness.RunAsync();

        Assert.AreEqual(Flags.Green, flagAtAck, "The entry was acknowledged before its data had been applied.");
    }

    /// <summary>
    /// The cancellation token has to reach the session context: everything downstream takes the
    /// pipeline's lock against it, and a shutdown that never reaches them hangs the service.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Startup_HandsTheStoppingTokenToTheSessionContext()
    {
        var harness = CreateHarness();

        await harness.RunAsync();

        Assert.IsTrue(harness.SessionContext.CancellationToken.IsCancellationRequested,
            "The context should be holding the stopping token, which was canceled to stop the service.");
    }

    #endregion

    #region Harness

    private static StreamEntry Entry(string id, params (string Name, string Value)[] fields)
        => new(id, [.. fields.Select(f => new NameValueEntry(f.Name, f.Value))]);

    private static Harness CreateHarness(Dictionary<string, string?>? extraConfiguration = null)
    {
        var settings = new Dictionary<string, string?> { { "event_id", EventId.ToString() } };
        foreach (var kvp in extraConfiguration ?? new Dictionary<string, string?> { { "job_name", "evt-7" } })
            settings[kvp.Key] = kvp.Value;
        return new Harness(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    /// <summary>
    /// Drives the real service over a stubbed Redis stream and a real processing pipeline, so a
    /// message that is routed can be seen arriving in the session state rather than only on a mock.
    /// </summary>
    private sealed class Harness
    {
        public Mock<IDatabase> Database { get; } = new();
        public SessionContext SessionContext { get; }
        public List<RelayResetRequest> RelayResets { get; } = [];
        public List<string> AcknowledgedIds { get; } = [];
        public bool StreamKeyExists { get; set; }
        /// <summary>
        /// What the stream serves. The default is a single entry carrying nothing routable, which is
        /// enough to take the loop through one pass and stop it on the acknowledgement.
        /// </summary>
        public StreamEntry[] Entries { get; set; } = [Entry("0-1", ("noise", "ignored"))];
        public Action? OnAcknowledge { get; set; }
        public string? RegisteredEndpointKey { get; private set; }
        public string? RegisteredEndpointValue { get; private set; }

        private readonly EventAggregatorService _service;
        private readonly CancellationTokenSource _cts = new();

        public Harness(IConfiguration configuration)
        {
            var loggerFactory = new DebugLoggerFactory();
            var timeProvider = new FakeTimeProvider();
            var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
            optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
            var dbFactory = new TestDbContextFactory(optionsBuilder.Options);

            var mux = new Mock<IConnectionMultiplexer>();
            mux.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(Database.Object);
            SetupRedis();

            var hubContext = CreateHubContext();
            var lapHistory = new InMemoryCarLapHistoryService(null, EventId);
            SessionContext = new SessionContext(configuration, dbFactory, loggerFactory, lapHistory, timeProvider);

            var mediator = new Mock<IMediator>();
            mediator.Setup(x => x.Publish(It.IsAny<RelayResetRequest>(), It.IsAny<CancellationToken>()))
                .Callback<RelayResetRequest, CancellationToken>((r, _) => RelayResets.Add(r))
                .Returns(Task.CompletedTask);

            var pitProcessor = new PitProcessor(dbFactory, loggerFactory, SessionContext);
            var telemetrySignalTracker = new TelemetrySignalTracker(loggerFactory, SessionContext, timeProvider);
            var flagtronicsProcessor = new FlagtronicsProcessor(loggerFactory, SessionContext, timeProvider, telemetrySignalTracker);
            var trackMapService = new TrackMapService(SessionContext, dbFactory, mux.Object, loggerFactory, timeProvider);

            var pipeline = new SessionStateProcessingPipeline(
                SessionContext,
                loggerFactory,
                new RMonitorDataProcessor(loggerFactory, SessionContext,
                    new ResetProcessor(SessionContext, hubContext, loggerFactory),
                    new StartingPositionProcessor(SessionContext, loggerFactory, dbFactory),
                    mediator.Object),
                new MultiloopProcessor(loggerFactory, SessionContext),
                pitProcessor,
                new FlagProcessor(dbFactory, loggerFactory, SessionContext),
                new SessionMonitor(configuration, dbFactory, loggerFactory, SessionContext, mux.Object),
                new PositionDataEnricher(loggerFactory, SessionContext),
                new ControlLogEnricher(loggerFactory, mux.Object, configuration, SessionContext),
                new DriverModeProcessor(hubContext, loggerFactory, new FakeHybridCache(), dbFactory, mux.Object, SessionContext),
                new LapProcessor(loggerFactory, dbFactory, SessionContext, mux.Object, pitProcessor, flagtronicsProcessor, lapHistory, timeProvider),
                new DriverEnricher(SessionContext, loggerFactory, mux.Object),
                new VideoEnricher(SessionContext, loggerFactory, mux.Object),
                new FastestPaceEnricher(loggerFactory, lapHistory, SessionContext),
                new ProjectedLapTimeEnricher(loggerFactory, lapHistory, SessionContext),
                trackMapService,
                new GpsLapPositionEnricher(loggerFactory, trackMapService, SessionContext, timeProvider),
                new StaleCarEnricher(loggerFactory, SessionContext),
                new UpdateConsolidator(SessionContext, loggerFactory, new StatusAggregator(hubContext, loggerFactory, SessionContext)),
                flagtronicsProcessor,
                telemetrySignalTracker);

            _service = new EventAggregatorService(loggerFactory, mux.Object, configuration, mediator.Object,
                hubContext, pipeline, SessionContext, pitProcessor);
        }

        /// <summary>
        /// Runs the service over <see cref="Entries"/> exactly once. The service is stopped as the
        /// last entry is acknowledged, so the loop exits on its own condition with the startup work
        /// and one full read behind it, and nothing waits on a timer.
        /// </summary>
        /// <remarks>
        /// The acknowledgement is the only thing that stops the loop, and it stops it only for as long
        /// as one particular <c>StreamAcknowledgeAsync</c> overload keeps matching the production call.
        /// If it stops matching - a StackExchange.Redis overload change, or an argument added at the call
        /// site - the read keeps returning entries and the loop spins with nothing to slow it down. The
        /// read budget turns that into a failure in milliseconds instead of a hot spin that runs until the
        /// 30 second timeout, which MSTest abandons rather than kills, leaving the loop running for the
        /// rest of the suite.
        ///
        /// The failsafe covers the other half: if a <c>StreamReadGroupAsync</c> setup stops matching,
        /// <see cref="ServeEntries"/> is never reached, so the budget cannot trip. The loop then retries
        /// behind its real five second throttle and would otherwise also run to the timeout.
        /// </remarks>
        public async Task RunAsync()
        {
            using var failsafe = new CancellationTokenSource(FailsafeTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, failsafe.Token);

            await _service.StartAsync(linked.Token);
            try
            {
                await _service.ExecuteTask!;
            }
            catch (OperationCanceledException) when (_readBudgetExhausted || failsafe.IsCancellationRequested)
            {
                // Reachable only on the two stop-it-from-outside paths below: the loop was still mid-poll
                // when it was stopped, so its inter-poll delay observes the canceled token. Both are
                // asserted on immediately after, and the normal exit throws nothing.
            }

            Assert.IsFalse(failsafe.IsCancellationRequested,
                $"The read loop did not stop within {FailsafeTimeout.TotalSeconds:0}s. The mocked stream read is "
                + "no longer matching the production call, so the loop never saw an entry to acknowledge.");
            Assert.IsFalse(_readBudgetExhausted,
                $"The read loop made more than {MaxReadPasses} passes. Nothing acknowledged an entry, so the "
                + "StreamAcknowledgeAsync setup is no longer matching the production call and the loop had "
                + "no way to stop.");
        }

        /// <summary>Backstop for a loop that never reaches <see cref="ServeEntries"/>. See <see cref="RunAsync"/>.</summary>
        private static readonly TimeSpan FailsafeTimeout = TimeSpan.FromSeconds(15);

        /// <summary>A healthy run reads once: the batch is served, acknowledged, and the loop exits.</summary>
        private const int MaxReadPasses = 5;
        private int _readPasses;
        private volatile bool _readBudgetExhausted;

        /// <summary>Serves <see cref="Entries"/>, stopping the run if the loop will not stop itself.</summary>
        private StreamEntry[] ServeEntries()
        {
            if (Interlocked.Increment(ref _readPasses) > MaxReadPasses)
            {
                _readBudgetExhausted = true;
                _cts.Cancel();
                return [];
            }
            return Entries;
        }

        private void SetupRedis()
        {
            Database.Setup(x => x.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(() => StreamKeyExists);
            Database.Setup(x => x.StreamGroupInfoAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync([]);
            Database.Setup(x => x.StreamCreateConsumerGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);

            // The client library offers several shapes of these calls; which one the service binds to
            // is an overload-resolution detail, so each is stubbed to the same behavior.
            Database.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
                .Callback<RedisKey, RedisValue, TimeSpan?, When>((key, value, _, _) => CaptureEndpoint(key, value))
                .ReturnsAsync(true);
            Database.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                    It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((key, value, _, _, _, _) => CaptureEndpoint(key, value))
                .ReturnsAsync(true);
            Database.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(),
                    It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
                .Callback<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags>((key, value, _, _, _) => CaptureEndpoint(key, value))
                .ReturnsAsync(true);

            Database.Setup(x => x.StreamReadGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(ServeEntries);
            Database.Setup(x => x.StreamReadGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(ServeEntries);
            Database.Setup(x => x.StreamReadGroupAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(ServeEntries);

            Database.Setup(x => x.StreamAcknowledgeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Callback<RedisKey, RedisValue, RedisValue, CommandFlags>((_, _, id, _) =>
                {
                    OnAcknowledge?.Invoke();
                    AcknowledgedIds.Add(id.ToString());
                    // Stop after the batch is fully handled so the loop exits on its own check.
                    _cts.Cancel();
                })
                .ReturnsAsync(1L);
        }

        private void CaptureEndpoint(RedisKey key, RedisValue value)
        {
            if (key.ToString() == string.Format(Backend.Shared.Consts.EVENT_SERVICE_ENDPOINT, EventId))
            {
                RegisteredEndpointKey = key.ToString();
                RegisteredEndpointValue = value.ToString();
            }
        }

        private static IHubContext<StatusHub> CreateHubContext()
        {
            var hubContext = new Mock<IHubContext<StatusHub>>();
            var clients = new Mock<IHubClients>();
            var clientProxy = new Mock<IClientProxy>();
            var groups = new Mock<IGroupManager>();
            hubContext.Setup(x => x.Clients).Returns(clients.Object);
            hubContext.Setup(x => x.Groups).Returns(groups.Object);
            clients.Setup(x => x.Group(It.IsAny<string>())).Returns(clientProxy.Object);
            clients.Setup(x => x.All).Returns(clientProxy.Object);
            clientProxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            return hubContext.Object;
        }
    }

    #endregion
}
