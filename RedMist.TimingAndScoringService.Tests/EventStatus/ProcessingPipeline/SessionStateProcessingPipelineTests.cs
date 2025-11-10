using Google.Apis.Sheets.v4.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Utilities;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.EventStatus.DriverInformation;
using RedMist.TimingAndScoringService.EventStatus.FlagData;
using RedMist.TimingAndScoringService.EventStatus.InCarDriverMode;
using RedMist.TimingAndScoringService.EventStatus.LapData;
using RedMist.TimingAndScoringService.EventStatus.Multiloop;
using RedMist.TimingAndScoringService.EventStatus.PenaltyEnricher;
using RedMist.TimingAndScoringService.EventStatus.PipelineBlocks;
using RedMist.TimingAndScoringService.EventStatus.PositionEnricher;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.SessionMonitoring;
using RedMist.TimingAndScoringService.EventStatus.Video;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Diagnostics;
using System.Text.Json;


namespace RedMist.TimingAndScoringService.Tests.EventStatus.ProcessingPipeline;

[TestClass]
public class SessionStateProcessingPipelineTests
{
    private SessionStateProcessingPipeline _pipeline = null!;
    private SessionContext _sessionContext = null!;
    private IConfiguration _configuration = null!;
    private readonly FakeTimeProvider _timeProvider = new();

    // Core dependencies - these remain mocked as they are infrastructure
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private Mock<IHubContext<StatusHub>> _mockHubContext = null!;
    private Mock<HybridCache> _mockHybridCache = null!;

    // Real processor instances for end-to-end testing
    private RMonitorDataProcessor _rMonitorProcessor = null!;
    private MultiloopProcessor _multiloopProcessor = null!;
    private PitProcessorV2 _pitProcessor = null!;
    private FlagProcessorV2 _flagProcessor = null!;
    private SessionMonitorV2 _sessionMonitor = null!;
    private PositionDataEnricher _positionEnricher = null!;
    private ControlLogEnricher _controlLogEnricher = null!;
    private ResetProcessor _resetProcessor = null!;
    private DriverModeProcessor _driverModeProcessor = null!;
    private LapProcessor _lapProcessor = null!;
    private DriverEnricher _driverEnricher = null!;
    private VideoEnricher _videoEnricher = null!;
    private UpdateConsolidator _updateConsolidator = null!;
    private StatusAggregator _statusAggregator = null!;
    private StartingPositionProcessor _startingPositionProcessor = null!;
    private Mock<IConnectionMultiplexer> _mockConnectionMultiplexer = null!;
    private RedisLapCapture _redisLapCapture = null!;

    const string FilePrefix = "EventStatus/ProcessingPipeline/";

    #region Initialization

    [TestInitialize]
    public void Setup()
    {
        _redisLapCapture = new RedisLapCapture();
        SetupBasicMocks();
        SetupSessionContext();
        SetupDbContextFactory();
        CreateProcessorInstances();
        CreatePipeline();
        InitializeDatabase();
    }

    private void SetupBasicMocks()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockHubContext = new Mock<IHubContext<StatusHub>>();
        _mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
        _mockHybridCache = new Mock<HybridCache>();

        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        // Setup mock hub context for SignalR operations
        var mockClients = new Mock<IHubClients>();
        var mockClientProxy = new Mock<IClientProxy>();
        var mockGroupManager = new Mock<IGroupManager>();

        _mockHubContext.Setup(x => x.Clients).Returns(mockClients.Object);
        _mockHubContext.Setup(x => x.Groups).Returns(mockGroupManager.Object);
        mockClients.Setup(x => x.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);

        // Fix for extension method mocking - use SendCoreAsync instead of SendAsync
        mockClientProxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockGroupManager.Setup(x => x.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Setup mock Redis connection with lap capture
        var mockDatabase = new Mock<IDatabase>();
        _mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(mockDatabase.Object);

        // Capture StreamAddAsync calls with name/value pairs
        mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
       .ReturnsAsync(RedisValue.Null);

        // Capture StreamAddAsync calls with field/value (for lap logging) - 3 parameter simplified version
        // This matches: StreamAddAsync(RedisKey key, RedisValue field, RedisValue value, RedisValue? messageId = null, long? maxLength = null, bool useApproximateMaxLength = false, long? limit = null, StreamTrimMode trimMode = default, CommandFlags flags = default)
        mockDatabase.Setup(x => x.StreamAddAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(),
            null,  // messageId default
            null,  // maxLength default
            false, // useApproximateMaxLength default
            null,  // limit default
            default(StreamTrimMode),  // trimMode default
            CommandFlags.None)) // flags default
            .Callback<RedisKey, RedisValue, RedisValue, RedisValue?, long?, bool, long?, StreamTrimMode, CommandFlags>(
                (key, field, value, messageId, maxLength, useApproximateMaxLength, limit, trimMode, flags) =>
            {
                _redisLapCapture.CaptureStreamAdd(field, value);
            })
            .ReturnsAsync(RedisValue.Null);

        // Capture StreamAddAsync calls with field/value (for lap logging) - 7 parameter version (if needed)
        mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, RedisValue, RedisValue?, int?, bool, CommandFlags>(
            (key, field, value, messageId, maxLength, useApproximateMaxLength, flags) =>
            {
                _redisLapCapture.CaptureStreamAdd(field, value);
            })
            .ReturnsAsync(RedisValue.Null);

        mockDatabase.Setup(x => x.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));

        // Note: HybridCache mocking is complex due to optional parameters in expression trees
        // For now, we'll rely on the DriverModeProcessor to handle null returns gracefully
    }

    private void SetupSessionContext()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();

        _sessionContext = new SessionContext(_configuration, _timeProvider);
    }

    private void SetupDbContextFactory()
    {
        // Create a real in-memory database factory for testing
        var databaseName = $"TestDatabase_{Guid.NewGuid()}";
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase(databaseName);
        var options = optionsBuilder.Options;

        // Create a simple implementation of IDbContextFactory
        _dbContextFactory = new TestDbContextFactory(options);
    }

    private void CreateProcessorInstances()
    {
        // Create real processor instances for end-to-end testing
        _resetProcessor = new ResetProcessor(_sessionContext, _mockHubContext.Object, _mockLoggerFactory.Object);
        _startingPositionProcessor = new StartingPositionProcessor(_sessionContext, _mockLoggerFactory.Object);
        _rMonitorProcessor = new RMonitorDataProcessor(_mockLoggerFactory.Object, _sessionContext, _resetProcessor, _startingPositionProcessor);
        _multiloopProcessor = new MultiloopProcessor(_mockLoggerFactory.Object, _sessionContext);
        _pitProcessor = new PitProcessorV2(_dbContextFactory, _mockLoggerFactory.Object, _sessionContext);
        _controlLogEnricher = new ControlLogEnricher(_mockLoggerFactory.Object, _mockConnectionMultiplexer.Object, _configuration, _sessionContext);
        _flagProcessor = new FlagProcessorV2(_dbContextFactory, _mockLoggerFactory.Object, _sessionContext);
        _sessionMonitor = new SessionMonitorV2(_configuration, _dbContextFactory, _mockLoggerFactory.Object, _sessionContext);
        _driverModeProcessor = new DriverModeProcessor(
            _mockHubContext.Object,
            _mockLoggerFactory.Object,
            _mockHybridCache.Object,
            _dbContextFactory,
            _mockConnectionMultiplexer.Object,
            _sessionContext);
        _positionEnricher = new PositionDataEnricher(_mockLoggerFactory.Object, _sessionContext);
        _lapProcessor = new LapProcessor(_mockLoggerFactory.Object, _dbContextFactory, _sessionContext, _mockConnectionMultiplexer.Object, _pitProcessor, _timeProvider);
        _driverEnricher = new DriverEnricher(_sessionContext, _mockLoggerFactory.Object, _mockConnectionMultiplexer.Object);
        _videoEnricher = new VideoEnricher(_sessionContext, _mockLoggerFactory.Object, _mockConnectionMultiplexer.Object);
        _statusAggregator = new StatusAggregator(_mockHubContext.Object, _mockLoggerFactory.Object, _sessionContext);
        _updateConsolidator = new UpdateConsolidator(_sessionContext, _mockLoggerFactory.Object, _statusAggregator);
    }

    private void CreatePipeline()
    {
        _pipeline = new SessionStateProcessingPipeline(
            _sessionContext,
            _mockLoggerFactory.Object,
            _rMonitorProcessor,
            _multiloopProcessor,
            _pitProcessor,
            _flagProcessor,
            _sessionMonitor,
            _positionEnricher,
            _controlLogEnricher,
            _resetProcessor,
            _driverModeProcessor,
            _lapProcessor,
            _driverEnricher,
            _videoEnricher,
            _updateConsolidator
        );
    }

    #endregion

    [TestMethod]
    public async Task ResetCommand_Test()
    {
        // Arrange
        var initialCarPositions = new List<CarPosition>
        {
            new() { Number = "1", DriverName = "Driver A", Class = "Class 1", TransponderId = 1001 },
            new() { Number = "2", DriverName = "Driver B", Class = "Class 1", TransponderId = 1002 }
        };
        _sessionContext.SessionState.CarPositions.AddRange(initialCarPositions);

        // Act
        var tm = new TimingMessage("rmonitor", "$I,\"07:29:44\",\"26 Apr 25\"", 1, DateTime.Now);
        await _pipeline.PostAsync(tm);

        // Assert
        Assert.IsEmpty(_sessionContext.SessionState.CarPositions);
    }

    [TestMethod]
    public async Task RMonitor_EntriesClassesTrackSession_PreEvent_Test()
    {
        // Arrange
        var data = new RMonitorTestDataHelper(FilePrefix + "TestEntries.txt");
        await data.LoadAsync();

        // Act
        while (!data.IsFinished)
        {
            var d = data.GetNextRecord();
            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.PostAsync(tm);
        }

        // Assert
        Assert.AreEqual(48, _sessionContext.SessionState.CarPositions.Count);
        Assert.AreEqual(48, _sessionContext.SessionState.EventEntries.Count);
        Assert.AreEqual(67, _sessionContext.SessionState.SessionId);
        Assert.AreEqual("Saturday 8 Hour", _sessionContext.SessionState.SessionName);
        Assert.AreEqual(9999, _sessionContext.SessionState.LapsToGo);
        Assert.AreEqual("07:29:44", _sessionContext.SessionState.LocalTimeOfDay);
        Assert.AreEqual("08:00:00", _sessionContext.SessionState.TimeToGo);
        Assert.AreEqual("00:00:00", _sessionContext.SessionState.RunningRaceTime);

        var car70 = _sessionContext.GetCarByNumber("70");
        Assert.IsNotNull(car70);
        Assert.AreEqual((uint)58488, car70!.TransponderId);
        Assert.AreEqual("GTO", car70.Class);
        //Assert.AreEqual("67", car70.SessionId);

        var entry70 = _sessionContext.SessionState.EventEntries.Single(e => e.Number == "70");
        Assert.AreEqual("Round 3 Racing", entry70.Name);
        Assert.AreEqual("Trim-Tex", entry70.Team);
        Assert.AreEqual("GTO", entry70.Class);
    }

    [TestMethod]
    public async Task RMonitor_InitialPositions_PreEvent_Test()
    {
        // Arrange
        var entriesData = new RMonitorTestDataHelper(FilePrefix + "TestEntries.txt");
        await entriesData.LoadAsync();
        var initialPositionData = new RMonitorTestDataHelper(FilePrefix + "TestInitialPositions.txt");
        await initialPositionData.LoadAsync();

        // Act
        while (!entriesData.IsFinished)
        {
            var d = entriesData.GetNextRecord();
            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.PostAsync(tm);
        }
        while (!initialPositionData.IsFinished)
        {
            var d = initialPositionData.GetNextRecord();
            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.PostAsync(tm);
        }

        // Assert
        Assert.AreEqual(48, _sessionContext.SessionState.CarPositions.Count);
        Assert.AreEqual(48, _sessionContext.SessionState.EventEntries.Count);

        var car70 = _sessionContext.GetCarByNumber("70");
        Assert.IsNotNull(car70);
        Assert.AreEqual(1, car70!.OverallPosition);
        Assert.AreEqual(1, car70!.ClassPosition);

        var car149 = _sessionContext.GetCarByNumber("149");
        Assert.IsNotNull(car149);
        Assert.AreEqual(47, car149!.OverallPosition);
        Assert.AreEqual(7, car149!.ClassPosition);
    }

    [TestMethod]
    public async Task RMonitor_Start_Test()
    {
        // Arrange
        var entriesData = new RMonitorTestDataHelper(FilePrefix + "TestEntries.txt");
        await entriesData.LoadAsync();
        var initialPositionData = new RMonitorTestDataHelper(FilePrefix + "TestInitialPositions.txt");
        await initialPositionData.LoadAsync();
        var startData = new RMonitorTestDataHelper(FilePrefix + "TestStart.txt");
        await startData.LoadAsync();

        // Act
        while (!entriesData.IsFinished)
        {
            var d = entriesData.GetNextRecord();
            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.PostAsync(tm);
        }
        while (!initialPositionData.IsFinished)
        {
            var d = initialPositionData.GetNextRecord();
            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.PostAsync(tm);
        }
        while (!startData.IsFinished)
        {
            var d = startData.GetNextRecord();
            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.PostAsync(tm);
        }

        // Assert
        var car70 = _sessionContext.GetCarByNumber("70");
        Assert.IsNotNull(car70);
        Assert.AreEqual(2, car70!.OverallPosition);
        Assert.AreEqual(2, car70!.ClassPosition);
        Assert.AreEqual("00:02:23.425", car70!.LastLapTime);
        Assert.AreEqual("00:08:05.341", car70!.TotalTime);
        Assert.AreEqual(Flags.Green, car70!.TrackFlag);
        Assert.AreEqual(2, car70!.BestLap);
        Assert.AreEqual("00:02:21.740", car70!.BestTime);
        Assert.AreEqual("0.787", car70!.InClassDifference);
        Assert.AreEqual("0.787", car70!.InClassGap);
        Assert.AreEqual("0.787", car70!.OverallDifference);
        Assert.AreEqual("0.787", car70!.OverallGap);

        var car2 = _sessionContext.GetCarByNumber("2");
        Assert.IsNotNull(car2);
        Assert.AreEqual(1, car2!.OverallPosition);
        Assert.AreEqual(1, car2!.ClassPosition);

        var car149 = _sessionContext.GetCarByNumber("149");
        Assert.IsNotNull(car149);
        Assert.AreEqual(47, car149!.OverallPosition);
        Assert.AreEqual(9, car149!.ClassPosition);
        Assert.AreEqual("00:02:52.476", car149!.LastLapTime);
        Assert.AreEqual("00:07:53.682", car149!.TotalTime);
        Assert.AreEqual(Flags.Green, car149!.TrackFlag);
        Assert.AreEqual(2, car149!.BestLap);
        Assert.AreEqual("00:02:52.476", car149!.BestTime);
        Assert.AreEqual("27.411", car149!.InClassDifference);
        Assert.AreEqual("15.925", car149!.InClassGap);
        Assert.AreEqual("1 lap", car149!.OverallDifference);
        Assert.AreEqual("15.925", car149!.OverallGap);
    }

    [TestMethod]
    public async Task RMonitor_SessionChanges_Test()
    {
        // Arrange
        var entriesData = new RMonitorTestDataHelper(FilePrefix + "TestSessionChanges.txt");
        await entriesData.LoadAsync();
        int finishedCount = 0;
        _sessionMonitor.InnerSessionMonitor.FinalizedSession += () => finishedCount++;

        // Act
        int i = 0;
        int lastSession = 0;
        while (!entriesData.IsFinished)
        {
            var d = entriesData.GetNextRecord();
            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.PostAsync(tm);

            var lines = d.data.Split('\n');
            foreach (var line in lines)
            {
                var lt = line.Trim();
                if (lt.StartsWith("$F"))
                {
                    // Advance time to simulate passage between records
                    _timeProvider.Advance(TimeSpan.FromSeconds(1));
                }
                else if (lt.StartsWith("$B"))
                {
                    // Extract session details from $B record
                    var parts = lt.Split(',');
                    var s = new Session
                    {
                        Id = int.Parse(parts[1]),
                        EventId = 1,
                        Name = parts[2],
                        IsLive = true,
                        StartTime = DateTime.UtcNow,
                        LastUpdated = DateTime.UtcNow,
                        LocalTimeZoneOffset = -4,
                        IsPracticeQualifying = SessionHelper.IsPracticeOrQualifyingSession(parts[2])
                    };

                    if (s.Id != lastSession)
                    {
                        lastSession = s.Id;
                        Console.WriteLine($"Session change to {s.Id} - {s.Name} at {d.ts}");

                        var sJson = JsonSerializer.Serialize(s);

                        // Simulate session change message to SessionMonitorV2
                        var sessionChangeTm = new TimingMessage(Backend.Shared.Consts.EVENT_SESSION_CHANGED_TYPE, sJson, s.Id, d.ts);
                        await _pipeline.PostAsync(sessionChangeTm);
                    }
                }
            }

            i++;
            if (i % 10 == 0) // Run session monitor periodically similar to task wait 5 seconds
            {
                await _sessionMonitor.RunCheckForFinished(_sessionContext.CancellationToken);
            }
        }

        // Assert
        Assert.AreEqual(11, finishedCount); // There are 11 session changes in the test data
    }


    [TestMethod]
    public async Task RMonitor_Reset_KeepLapTimes_Test()
    {
        // Arrange
        var entriesData = new RMonitorTestDataHelper(FilePrefix + "TestResetRetainLapTime.txt");
        await entriesData.LoadAsync();

        // Act
        while (!entriesData.IsFinished)
        {
            var d = entriesData.GetNextRecord();
            if (d.data.StartsWith("$F"))
            {
                // Advance time to simulate passage between records
                _timeProvider.Advance(TimeSpan.FromSeconds(1));
            }

            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.PostAsync(tm);
        }

        // Assert
        Assert.AreEqual("00:02:25.077", _sessionContext.SessionState.CarPositions.Single(c => c.Number == "70").LastLapTime);
        Assert.AreEqual(null, _sessionContext.SessionState.CarPositions.Single(c => c.Number == "2").LastLapTime);
        Assert.AreEqual("00:02:27.407", _sessionContext.SessionState.CarPositions.Single(c => c.Number == "74").LastLapTime);
        Assert.AreEqual("00:02:30.314", _sessionContext.SessionState.CarPositions.Single(c => c.Number == "99").LastLapTime);
    }

    [TestMethod]
    public async Task RMonitor_Reset_LosesPosition_Test()
    {
        // Arrange
        var entriesData = new RMonitorTestDataHelper(FilePrefix + "TestAbnormalReset.txt");
        await entriesData.LoadAsync();

        // Act
        while (!entriesData.IsFinished)
        {
            var d = entriesData.GetNextRecord();
            if (d.data.StartsWith("$F"))
            {
                // Advance time to simulate passage between records
                _timeProvider.Advance(TimeSpan.FromSeconds(1));
            }

            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.PostAsync(tm);
        }

        // Assert
        // Make sure there are still positions for all cars
        Assert.AreEqual(2, _sessionContext.SessionState.CarPositions.Single(c => c.Number == "70").OverallPosition);
        Assert.AreEqual(1, _sessionContext.SessionState.CarPositions.Single(c => c.Number == "2").OverallPosition);
        Assert.AreEqual(8, _sessionContext.SessionState.CarPositions.Single(c => c.Number == "74").OverallPosition);
        Assert.AreEqual(4, _sessionContext.SessionState.CarPositions.Single(c => c.Number == "99").OverallPosition);
    }

    [TestMethod]
    public async Task RMonitor_MidRaceClassChange_Test()
    {
        // Arrange
        var entriesData = new RMonitorTestDataHelper(FilePrefix + "TestMidRaceClassChange.txt");
        await entriesData.LoadAsync();

        // Act
        while (!entriesData.IsFinished)
        {
            var d = entriesData.GetNextRecord();
            if (d.data.StartsWith("$F"))
            {
                // Advance time to simulate passage between records
                _timeProvider.Advance(TimeSpan.FromSeconds(1));
            }

            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.PostAsync(tm);
        }

        // Assert
        Assert.AreEqual("GP2", _sessionContext.SessionState.CarPositions.Single(c => c.Number == "55").Class);
    }

    [TestMethod]
    public async Task RMonitor_MissingLap1And2_Test()
    {
        // Arrange
        var entriesData = new RMonitorTestDataHelper(FilePrefix + "TestMissingLaps.txt");
        await entriesData.LoadAsync();

        // Act
        while (!entriesData.IsFinished)
        {
            var d = entriesData.GetNextRecord();
            if (d.data.StartsWith("$F"))
            {
                // Advance time to simulate passage between records
                _timeProvider.Advance(TimeSpan.FromSeconds(1));
            }

            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.PostAsync(tm);
        }

        // Allow time for pending laps to be processed (LapProcessor has 1 second wait time)
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.CancellationTokenSource.Token);

        // Assert
        // Looking at the test data, we should check that all cars that completed laps have them logged

        // Check that car 484 has laps logged (appears to complete laps in the data)
        var car484Laps = _redisLapCapture.GetLapsForCar("484");
        Assert.IsGreaterThan(0, car484Laps.Count, "Car 484 should have at least one lap logged");

        // Check that lap 8 was captured
        Assert.IsTrue(_redisLapCapture.HasLap("484", 8), "Car 484 should have lap 8 logged");
    }

    //[TestMethod]
    //public async Task RMonitor_Leader_NoGapDiff_Test()
    //{
    //    // Arrange
    //    var entriesData = new RMonitorTestDataHelper(FilePrefix + "TestGapDiff.txt");
    //    await entriesData.LoadAsync();

    //    // Act
    //    DateTime? lastLeaderGapDiffFailTime = null;
    //    // Track per-car failure times for overall gap/diff
    //    var carOverallGapDiffFailTimes = new Dictionary<string, DateTime>();
    //    // Track per-car failure times for in-class gap/diff
    //    var carInClassGapDiffFailTimes = new Dictionary<string, DateTime>();

    //    while (!entriesData.IsFinished)
    //    {
    //        var d = entriesData.GetNextRecord();
    //        if (d.data.StartsWith("$F"))
    //        {
    //            // Advance time to simulate passage between records
    //            _timeProvider.Advance(TimeSpan.FromSeconds(1));
    //            if (d.data.Contains("07:22:42"))
    //            {
    //            }
    //            if (_sessionContext.SessionState.CurrentFlag == Flags.Green || _sessionContext.SessionState.CurrentFlag == Flags.Yellow)
    //            {
    //                // Check leader for gap/diff absence
    //                //var leader = _sessionContext.SessionState.CarPositions.FirstOrDefault(c => c.OverallPosition == 1);
    //                //if (leader != null)
    //                //{
    //                //    if (leader.OverallGap != string.Empty || leader.OverallDifference != string.Empty
    //                //        || leader.InClassGap != string.Empty || leader.InClassDifference != string.Empty)
    //                //    {
    //                //        if (lastLeaderGapDiffFailTime == null)
    //                //            lastLeaderGapDiffFailTime = d.ts;
    //                //        // Leave 2 second grace period for gaps/diffs to update since position changes are not atomic
    //                //        if (lastLeaderGapDiffFailTime.HasValue && (d.ts - lastLeaderGapDiffFailTime.Value).TotalSeconds > 2)
    //                //        {
    //                //            Assert.Fail($"Leader {leader.Number} gap/diff not empty:T={_sessionContext.SessionState.RunningRaceTime} Gap={leader.OverallGap} Diff={leader.OverallDifference}");
    //                //        }
    //                //        Trace.WriteLine($"Leader {leader.Number} gap/diff not empty:T={_sessionContext.SessionState.RunningRaceTime} Gap={leader.OverallGap} Diff={leader.OverallDifference}");
    //                //    }
    //                //    else
    //                //    {
    //                //        lastLeaderGapDiffFailTime = null;
    //                //    }

    //                //    // Check other cars for gap/diff presence
    //                //    foreach (var car in _sessionContext.SessionState.CarPositions)
    //                //    {
    //                //        // Check overall gap/diff for non-leaders
    //                //        if (car.OverallPosition != 1 && car.Number != null)
    //                //        {
    //                //            if (string.IsNullOrEmpty(car.OverallGap) || string.IsNullOrEmpty(car.OverallDifference))
    //                //            {
    //                //                // Set fail time for this car if not already set
    //                //                if (!carOverallGapDiffFailTimes.ContainsKey(car.Number))
    //                //                    carOverallGapDiffFailTimes[car.Number] = d.ts;

    //                //                // Check if this car has been failing for more than 2 seconds
    //                //                if (carOverallGapDiffFailTimes.TryGetValue(car.Number, out var failTime) &&
    //                //                    (d.ts - failTime).TotalSeconds > 2)
    //                //                {
    //                //                    Assert.Fail($"Car {car.Number} gap/diff empty for >2s: T={_sessionContext.SessionState.RunningRaceTime} Pos={car.OverallPosition} Gap={car.OverallGap} Diff={car.OverallDifference}");
    //                //                }
    //                //                Trace.WriteLine($"Car {car.Number} gap/diff empty:T={_sessionContext.SessionState.RunningRaceTime} Pos={car.OverallPosition} Gap={car.OverallGap} Diff={car.OverallDifference}");
    //                //            }
    //                //            else
    //                //            {
    //                //                // Clear fail time for this car since it now has gap/diff
    //                //                carOverallGapDiffFailTimes.Remove(car.Number);
    //                //            }
    //                //        }

    //                //        // Check in-class gap/diff for non-class-leaders
    //                //        if (car.ClassPosition != 1 && car.Number != null)
    //                //        {
    //                //            if (string.IsNullOrEmpty(car.InClassGap) || string.IsNullOrEmpty(car.InClassDifference))
    //                //            {
    //                //                // Set fail time for this car if not already set
    //                //                if (!carInClassGapDiffFailTimes.ContainsKey(car.Number))
    //                //                    carInClassGapDiffFailTimes[car.Number] = d.ts;

    //                //                // Check if this car has been failing for more than 2 seconds
    //                //                if (carInClassGapDiffFailTimes.TryGetValue(car.Number, out var failTime) &&
    //                //                    (d.ts - failTime).TotalSeconds > 2)
    //                //                {
    //                //                    Assert.Fail($"ClassCar {car.Number} gap/diff empty for >2s: T={_sessionContext.SessionState.RunningRaceTime} ClassPos={car.ClassPosition} Gap={car.InClassGap} Diff={car.InClassDifference}");
    //                //                }
    //                //                Trace.WriteLine($"ClassCar {car.Number} gap/diff empty:T={_sessionContext.SessionState.RunningRaceTime} ClassPos={car.ClassPosition} Gap={car.InClassGap} Diff={car.InClassDifference}");
    //                //            }
    //                //            else
    //                //            {
    //                //                // Clear fail time for this car since it now has gap/diff
    //                //                carInClassGapDiffFailTimes.Remove(car.Number);
    //                //            }
    //                //        }
    //                //    }
    //                //}
    //            }
    //        }

    //        var tm = new TimingMessage(d.type, d.data, 1, d.ts);
    //        if (d.data.Contains("$G,18,\"816\",246,\"07:21:05.918\""))
    //        {
    //        }
    //        if (d.data.Contains("$G,16,\"816\",247,\"07:22:35.029\""))
    //        {
    //        }
    //        await _pipeline.Post(tm);
    //        if (_sessionContext.SessionState.CarPositions.SingleOrDefault(c => c.Number == "816")?.OverallGap == "29:04.197")
    //        {
    //        }
    //    }

    //    //// Assert
    //    //Assert.AreEqual(string.Empty, _sessionContext.SessionState.CarPositions.Single(c => c.Number == "101").OverallGap);
    //    //Assert.AreEqual(string.Empty, _sessionContext.SessionState.CarPositions.Single(c => c.Number == "101").OverallDifference);
    //}

    #region Database Initialization

    private void InitializeDatabase()
    {
        using var context = _dbContextFactory.CreateDbContext();

        // For in-memory database, we need to ensure the database is created
        // Since in-memory databases don't support migrations, we use EnsureCreated()
        context.Database.EnsureCreated();
    }


    /// <summary>
    /// Simple implementation of IDbContextFactory for testing
    /// </summary>
    private class TestDbContextFactory(DbContextOptions<TsContext> options) : IDbContextFactory<TsContext>
    {
        public TsContext CreateDbContext()
        {
            return new TsContext(options);
        }

        public async ValueTask<TsContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return await Task.FromResult(new TsContext(options));
        }
    }

    public TestContext TestContext { get; set; }

    #endregion
}