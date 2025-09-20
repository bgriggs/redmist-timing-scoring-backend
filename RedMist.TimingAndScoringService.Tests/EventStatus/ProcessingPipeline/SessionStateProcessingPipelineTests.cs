using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Hubs;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.EventStatus.FlagData;
using RedMist.TimingAndScoringService.EventStatus.LapData;
using RedMist.TimingAndScoringService.EventStatus.Multiloop;
using RedMist.TimingAndScoringService.EventStatus.PipelineBlocks;
using RedMist.TimingAndScoringService.EventStatus.PositionEnricher;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.SessionMonitoring;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.ProcessingPipeline;

[TestClass]
public class SessionStateProcessingPipelineTests
{
    private SessionStateProcessingPipeline _pipeline = null!;
    private SessionContext _sessionContext = null!;
    private IConfiguration _configuration = null!;

    // Core dependencies - these remain mocked as they are infrastructure
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private Mock<IHubContext<StatusHub>> _mockHubContext = null!;

    // Real processor instances for end-to-end testing
    private RMonitorDataProcessorV2 _rMonitorProcessor = null!;
    private MultiloopProcessor _multiloopProcessor = null!;
    private PitProcessorV2 _pitProcessor = null!;
    private FlagProcessorV2 _flagProcessor = null!;
    private SessionMonitorV2 _sessionMonitor = null!;
    private PositionDataEnricher _positionEnricher = null!;
    private ResetProcessor _resetProcessor = null!;
    private LapProcessor _lapProcessor = null!;
    private UpdateConsolidator _updateConsolidator = null!;
    private StatusAggregatorV2 _statusAggregator = null!;
    private StartingPositionProcessor _startingPositionProcessor = null!;
    private Mock<IConnectionMultiplexer> _mockConnectionMultiplexer = null!;

    const string FilePrefix = "EventStatus/ProcessingPipeline/";

    #region Initialization

    [TestInitialize]
    public void Setup()
    {
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

        // Setup mock Redis connection
        var mockDatabase = new Mock<IDatabase>();
        _mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(mockDatabase.Object);
        mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
        mockDatabase.Setup(x => x.StreamAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);
    }

    private void SetupSessionContext()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();

        _sessionContext = new SessionContext(_configuration);
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
        _rMonitorProcessor = new RMonitorDataProcessorV2(_mockLoggerFactory.Object, _sessionContext, _resetProcessor, _startingPositionProcessor);
        _multiloopProcessor = new MultiloopProcessor(_mockLoggerFactory.Object, _sessionContext);
        _pitProcessor = new PitProcessorV2(_dbContextFactory, _mockLoggerFactory.Object, _sessionContext);
        _flagProcessor = new FlagProcessorV2(_dbContextFactory, _mockLoggerFactory.Object, _sessionContext);
        _sessionMonitor = new SessionMonitorV2(_configuration, _dbContextFactory, _mockLoggerFactory.Object, _sessionContext);
        _positionEnricher = new PositionDataEnricher(_dbContextFactory, _mockLoggerFactory.Object, _sessionContext);
        _lapProcessor = new LapProcessor(_mockLoggerFactory.Object, _dbContextFactory, _sessionContext, _mockConnectionMultiplexer.Object, _pitProcessor);
        _statusAggregator = new StatusAggregatorV2(_mockHubContext.Object, _mockLoggerFactory.Object, _sessionContext);
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
            _resetProcessor,
            _lapProcessor,
            _updateConsolidator,
            _statusAggregator
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
        await _pipeline.Post(tm);

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
            await _pipeline.Post(tm);
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
        Assert.AreEqual("Round 3 Racing", car70.DriverName);
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
            await _pipeline.Post(tm);
        }
        while (!initialPositionData.IsFinished)
        {
            var d = initialPositionData.GetNextRecord();
            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.Post(tm);
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
            await _pipeline.Post(tm);
        }
        while (!initialPositionData.IsFinished)
        {
            var d = initialPositionData.GetNextRecord();
            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.Post(tm);
        }
        while (!startData.IsFinished)
        {
            var d = startData.GetNextRecord();
            var tm = new TimingMessage(d.type, d.data, 1, d.ts);
            await _pipeline.Post(tm);
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
    private class TestDbContextFactory : IDbContextFactory<TsContext>
    {
        private readonly DbContextOptions<TsContext> _options;

        public TestDbContextFactory(DbContextOptions<TsContext> options)
        {
            _options = options;
        }

        public TsContext CreateDbContext()
        {
            return new TsContext(_options);
        }

        public async ValueTask<TsContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return await Task.FromResult(new TsContext(_options));
        }
    }

    public TestContext TestContext { get; set; }

    #endregion
}