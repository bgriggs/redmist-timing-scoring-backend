using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.PositionEnricher;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.LapData;

[TestClass]
public class StaleCarEnricherTests
{
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private Mock<IConnectionMultiplexer> _mockConnectionMultiplexer = null!;
    private Mock<IDatabase> _mockDatabase = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private SessionContext _sessionContext = null!;
    private FakeTimeProvider _timeProvider = null!;
    private StaleCarEnricher _enricher = null!;
    private const int EventId = 1;
    private const int SessionId = 1;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", EventId.ToString() } })
            .Build();

        _dbContextFactory = CreateDbContextFactory();
        _timeProvider = new FakeTimeProvider();
        _sessionContext = new SessionContext(configuration, _dbContextFactory, _timeProvider);
        _sessionContext.SessionState.SessionId = SessionId;

        SetupRedisMock();

        _enricher = new StaleCarEnricher(
            _mockLoggerFactory.Object,
            _sessionContext);
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var databaseName = $"TestDatabase_{Guid.NewGuid()}";
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase(databaseName);
        var options = optionsBuilder.Options;
        return new TestDbContextFactory(options);
    }

    private void SetupRedisMock()
    {
        _mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();

        _mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDatabase.Object);
    }

    private static CarPosition CreateTestCarPosition(string carNumber, string carClass,
        bool isStale = false, string lastLapTime = "00:01:30.000", string totalTime = "00:05:00.000",
        int lastLapCompleted = 3, Flags trackFlag = Flags.Green)
    {
        return new CarPosition
        {
            Number = carNumber,
            Class = carClass,
            OverallPosition = 1,
            TransponderId = 12345,
            EventId = "1",
            SessionId = "1",
            BestLap = 0,
            LastLapCompleted = lastLapCompleted,
            OverallStartingPosition = 1,
            InClassStartingPosition = 1,
            OverallPositionsGained = CarPosition.InvalidPosition,
            InClassPositionsGained = CarPosition.InvalidPosition,
            ClassPosition = 1,
            PenalityLaps = 0,
            PenalityWarnings = 0,
            BlackFlags = 0,
            IsEnteredPit = false,
            IsPitStartFinish = false,
            IsExitedPit = false,
            IsInPit = false,
            LapIncludedPit = false,
            LastLoopName = string.Empty,
            IsStale = isStale,
            TrackFlag = trackFlag,
            LocalFlag = Flags.Green,
            CompletedSections = [],
            ProjectedLapTimeMs = 0,
            LapStartTime = TimeOnly.MinValue,
            DriverName = "Test Driver",
            DriverId = "DRV1",
            CurrentStatus = "Active",
            ImpactWarning = false,
            IsBestTime = false,
            IsBestTimeClass = false,
            IsOverallMostPositionsGained = false,
            IsClassMostPositionsGained = false,
            InClassFastestAveragePace = false,
            LastLapTime = lastLapTime,
            TotalTime = totalTime
        };
    }

    #region ProcessAsync Tests

    [TestMethod]
    public async Task ProcessAsync_NoCars_ReturnsEmptyList()
    {
        // Arrange
        _sessionContext.SessionState.CarPositions = [];

        // Act
        var result = await _enricher.ProcessAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task ProcessAsync_CurrentLapLessThanThree_ReturnsEmptyList()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "GT3", lastLapCompleted: 2);
        var car2 = CreateTestCarPosition("2", "GT3", lastLapCompleted: 1);
        _sessionContext.SessionState.CarPositions = [car1, car2];

        // Act
        var result = await _enricher.ProcessAsync();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsEmpty(result, "Should not check for stale cars before lap 3");
    }

    [TestMethod]
    public async Task ProcessAsync_CarWithEmptyNumber_IsIgnored()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "GT3", lastLapCompleted: 5);
        var car2 = CreateTestCarPosition("", "GT3", lastLapCompleted: 5);
        _sessionContext.SessionState.CarPositions = [car1, car2];
        _sessionContext.SessionState.RunningRaceTime = "00:10:00.000";

        // Act
        var result = await _enricher.ProcessAsync();

        // Assert
        // Car with empty number should be ignored and not processed
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task ProcessAsync_CarWithZeroLapsCompleted_MarkedAsStale()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "GT3", lastLapCompleted: 0);
        var car2 = CreateTestCarPosition("2", "GT3", lastLapCompleted: 5); // Need at least one car with lap 3+ for check to run
        car1.IsStale = false;
        _sessionContext.SessionState.CarPositions = [car1, car2];
        _sessionContext.SessionState.RunningRaceTime = "00:10:00.000";

        // Act
        var result = await _enricher.ProcessAsync();

        // Assert
        Assert.HasCount(1, result);
        Assert.AreEqual("1", result[0].Number);
        Assert.IsTrue(result[0].IsStale);
        Assert.IsTrue(car1.IsStale, "Car should be updated");
    }

    [TestMethod]
    public async Task ProcessAsync_CarExceedsThreshold_ChecksForStale()
    {
        // Arrange
        var car = CreateTestCarPosition("1", "GT3", 
            isStale: false, 
            lastLapTime: "00:01:30.000", 
            totalTime: "00:05:00.000",
            lastLapCompleted: 5);
        _sessionContext.SessionState.CarPositions = [car];
        _sessionContext.SessionState.RunningRaceTime = "00:08:00.000"; // 3 minutes since last lap = stale
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Act
        var result = await _enricher.ProcessAsync();

        // Assert
        Assert.HasCount(1, result, "Car should be marked as stale");
        Assert.AreEqual("1", result[0].Number);
        Assert.IsTrue(result[0].IsStale);
    }

    [TestMethod]
    public async Task ProcessAsync_NoChangeInStaleStatus_ReturnsNoPatch()
    {
        // Arrange - Car is already stale and should remain stale
        var car = CreateTestCarPosition("1", "GT3",
            isStale: true,
            lastLapTime: "00:01:30.000",
            totalTime: "00:05:00.000",
            lastLapCompleted: 5);
        _sessionContext.SessionState.CarPositions = [car];
        _sessionContext.SessionState.RunningRaceTime = "00:08:00.000";
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Act
        var result = await _enricher.ProcessAsync();

        // Assert
        Assert.IsEmpty(result, "No patch should be returned when stale status doesn't change");
    }

    [TestMethod]
    public async Task ProcessAsync_MultipleCars_ChecksEachCar()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "GT3",
            isStale: false,
            lastLapTime: "00:01:30.000",
            totalTime: "00:05:00.000",
            lastLapCompleted: 5);
        var car2 = CreateTestCarPosition("2", "GT3",
            isStale: false,
            lastLapTime: "00:01:30.000",
            totalTime: "00:08:30.000",
            lastLapCompleted: 5);

        _sessionContext.SessionState.CarPositions = [car1, car2];
        _sessionContext.SessionState.RunningRaceTime = "00:09:00.000";
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Car 1: Last lap at 5:00, race time 9:00 = 4 minutes since last lap (stale)
        // Car 2: Last lap at 8:30, race time 9:00 = 30 seconds since last lap (not stale)

        // Act
        var result = await _enricher.ProcessAsync();

        // Assert
        Assert.HasCount(1, result, "Only car 1 should be marked as stale");
        Assert.AreEqual("1", result[0].Number);
        Assert.IsTrue(result[0].IsStale);
    }

    #endregion

    #region CheckForStale Tests

    [TestMethod]
    public void CheckForStale_RedFlag_ReturnsFalse()
    {
        // Arrange
        var car = CreateTestCarPosition("1", "GT3",
            lastLapTime: "00:01:30.000",
            totalTime: "00:05:00.000");
        var raceTime = new DateTime(1, 1, 1, 0, 10, 0, 0); // 10 minutes

        // Act
        var result = _enricher.CheckForStale(car, Flags.Red, raceTime);

        // Assert
        Assert.IsFalse(result, "Should not mark cars as stale during red flag");
    }

    [TestMethod]
    public void CheckForStale_CheckeredFlag_ReturnsFalse()
    {
        // Arrange
        var car = CreateTestCarPosition("1", "GT3",
            lastLapTime: "00:01:30.000",
            totalTime: "00:05:00.000");
        var raceTime = new DateTime(1, 1, 1, 0, 10, 0, 0); // 10 minutes

        // Act
        var result = _enricher.CheckForStale(car, Flags.Checkered, raceTime);

        // Assert
        Assert.IsFalse(result, "Should not mark cars as stale during checkered flag");
    }

    [TestMethod]
    public void CheckForStale_NullTotalTime_ReturnsFalse()
    {
        // Arrange
        var car = CreateTestCarPosition("1", "GT3",
            lastLapTime: "00:01:30.000");
        car.TotalTime = null;
        var raceTime = new DateTime(1, 1, 1, 0, 10, 0, 0); // 10 minutes

        // Act
        var result = _enricher.CheckForStale(car, Flags.Green, raceTime);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void CheckForStale_NullLastLapTime_ReturnsFalse()
    {
        // Arrange
        var car = CreateTestCarPosition("1", "GT3", totalTime: "00:05:00.000");
        car.LastLapTime = null;
        var raceTime = new DateTime(1, 1, 1, 0, 10, 0, 0); // 10 minutes

        // Act
        var result = _enricher.CheckForStale(car, Flags.Green, raceTime);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void CheckForStale_TimeDiffLessThanOneSecond_ReturnsFalse()
    {
        // Arrange
        var car = CreateTestCarPosition("1", "GT3",
            lastLapTime: "00:01:30.000",
            totalTime: "00:05:00.000");
        // Race time is only 0.5 seconds after car's total time
        var raceTime = new DateTime(1, 1, 1, 0, 5, 0, 500); // 5:00.500

        // Act
        var result = _enricher.CheckForStale(car, Flags.Green, raceTime);

        // Assert
        Assert.IsFalse(result, "Should not check staleness if time diff is less than 1 second");
    }

    [TestMethod]
    public void CheckForStale_GreenFlag_ExceedsThreshold_ReturnsTrue()
    {
        // Arrange - Last lap was 90 seconds, car should be marked stale after 117 seconds (90 * 1.3)
        var car = CreateTestCarPosition("1", "GT3",
            lastLapTime: "00:01:30.000", // 90 seconds
            totalTime: "00:05:00.000", // 5 minutes
            trackFlag: Flags.Green);
        // Race time is 7:00 = 2 minutes (120 seconds) since last lap
        var raceTime = new DateTime(1, 1, 1, 0, 7, 0, 0);

        // Act
        var result = _enricher.CheckForStale(car, Flags.Green, raceTime);

        // Assert
        Assert.IsTrue(result, "Car taking 120s when last lap was 90s should be stale (threshold: 117s)");
    }

    [TestMethod]
    public void CheckForStale_GreenFlag_WithinThreshold_ReturnsFalse()
    {
        // Arrange - Last lap was 90 seconds, threshold is 117 seconds (90 * 1.3)
        var car = CreateTestCarPosition("1", "GT3",
            lastLapTime: "00:01:30.000", // 90 seconds
            totalTime: "00:05:00.000", // 5 minutes
            trackFlag: Flags.Green);
        // Race time is 6:30 = 90 seconds since last lap (within threshold)
        var raceTime = new DateTime(1, 1, 1, 0, 6, 30, 0);

        // Act
        var result = _enricher.CheckForStale(car, Flags.Green, raceTime);

        // Assert
        Assert.IsFalse(result, "Car taking 90s when last lap was 90s should not be stale");
    }

    [TestMethod]
    public void CheckForStale_YellowFlag_ExceedsThreshold_ReturnsTrue()
    {
        // Arrange - Car's last lap was under green, now track is yellow. Threshold is 189 seconds (90 * 2.1)
        var car = CreateTestCarPosition("1", "GT3",
            lastLapTime: "00:01:30.000", // 90 seconds
            totalTime: "00:05:00.000",
            trackFlag: Flags.Green); // Car's last lap was under green
        // Race time is 8:30 = 210 seconds since last lap (exceeds 189s threshold)
        var raceTime = new DateTime(1, 1, 1, 0, 8, 30, 0);

        // Act
        var result = _enricher.CheckForStale(car, Flags.Yellow, raceTime);

        // Assert
        Assert.IsTrue(result, "Car taking 210s when last lap was 90s should be stale (green to yellow threshold: 189s)");
    }

    [TestMethod]
    public void CheckForStale_YellowFlag_WithinThreshold_ReturnsFalse()
    {
        // Arrange - Car's last lap was under green, now track is yellow. Threshold is 189 seconds
        var car = CreateTestCarPosition("1", "GT3",
            lastLapTime: "00:01:30.000", // 90 seconds
            totalTime: "00:05:00.000",
            trackFlag: Flags.Green); // Car's last lap was under green
        // Race time is 8:00 = 180 seconds since last lap (within 189s threshold)
        var raceTime = new DateTime(1, 1, 1, 0, 8, 0, 0);

        // Act
        var result = _enricher.CheckForStale(car, Flags.Yellow, raceTime);

        // Assert
        Assert.IsFalse(result, "Car taking 180s when last lap was 90s should not be stale (green to yellow threshold: 189s)");
    }

    [TestMethod]
    public void CheckForStale_TransitionGreenToYellow_AllowsMoreTime()
    {
        // Arrange - Car was under green, now yellow. Last lap 90s, threshold is 189s (90 * 2.1)
        var car = CreateTestCarPosition("1", "GT3",
            lastLapTime: "00:01:30.000", // 90 seconds
            totalTime: "00:05:00.000",
            trackFlag: Flags.Green); // Car was on green
        // Race time is 8:30 = 210 seconds since last lap (exceeds 189s threshold)
        var raceTime = new DateTime(1, 1, 1, 0, 8, 30, 0);

        // Act - Track is now yellow
        var result = _enricher.CheckForStale(car, Flags.Yellow, raceTime);

        // Assert
        Assert.IsTrue(result, "Car taking 210s when transitioning from green to yellow should be stale (threshold: 189s)");
    }

    [TestMethod]
    public void CheckForStale_TransitionYellowToGreen_TighterThreshold()
    {
        // Arrange - Car was under yellow, now green. Last lap 90s, threshold is 94.5s (90 * 1.05)
        var car = CreateTestCarPosition("1", "GT3",
            lastLapTime: "00:01:30.000", // 90 seconds
            totalTime: "00:05:00.000",
            trackFlag: Flags.Yellow); // Car was on yellow
        // Race time is 6:36 = 96 seconds since last lap (exceeds 94.5s threshold)
        var raceTime = new DateTime(1, 1, 1, 0, 6, 36, 0);

        // Act - Track is now green
        var result = _enricher.CheckForStale(car, Flags.Green, raceTime);

        // Assert
        Assert.IsTrue(result, "Car taking 96s when transitioning from yellow to green should be stale (threshold: 94.5s)");
    }

    [TestMethod]
    public void CheckForStale_WhiteFlag_UsesGreenThreshold()
    {
        // Arrange - White flag uses 30% threshold like green
        var car = CreateTestCarPosition("1", "GT3",
            lastLapTime: "00:01:30.000", // 90 seconds
            totalTime: "00:05:00.000",
            trackFlag: Flags.Green);
        // Race time is 7:00 = 120 seconds since last lap
        var raceTime = new DateTime(1, 1, 1, 0, 7, 0, 0);

        // Act
        var result = _enricher.CheckForStale(car, Flags.White, raceTime);

        // Assert
        Assert.IsTrue(result, "White flag should use same threshold as green (117s)");
    }

    [TestMethod]
    public void CheckForStale_ZeroLastLapTime_ReturnsFalse()
    {
        // Arrange
        var car = CreateTestCarPosition("1", "GT3",
            lastLapTime: "00:00:00.000",
            totalTime: "00:05:00.000");
        var raceTime = new DateTime(1, 1, 1, 0, 10, 0, 0);

        // Act
        var result = _enricher.CheckForStale(car, Flags.Green, raceTime);

        // Assert
        Assert.IsFalse(result, "Should not mark car as stale if last lap time is zero");
    }

    [TestMethod]
    public void Debug_ParseTimeFormats()
    {
        // Test parsing to verify it works
        var lastLapTime = FastestPaceEnricher.ParseRMTime("00:01:30.000");
        var totalTime = PositionMetadataProcessor.ParseRMTime("00:05:00.000");
        var raceTime = new DateTime(1, 1, 1, 0, 7, 0, 0);

        Console.WriteLine($"LastLapTime: {lastLapTime.TotalSeconds}s");
        Console.WriteLine($"TotalTime: {totalTime}");
        Console.WriteLine($"RaceTime: {raceTime}");

        var diff = raceTime - totalTime;
        Console.WriteLine($"Diff: {diff.TotalSeconds}s");

        Assert.AreEqual(90, lastLapTime.TotalSeconds, "Last lap should be 90 seconds");
        Assert.AreEqual(new DateTime(1, 1, 1, 0, 5, 0, 0), totalTime, "Total time should be 5 minutes");
        Assert.AreEqual(120, diff.TotalSeconds, "Diff should be 120 seconds");
        Assert.IsTrue(diff.TotalSeconds > lastLapTime.TotalSeconds * 1.3, "120 > 117");
    }

    #endregion

    #region Helper Methods

    private static List<CarPosition> CreateLapHistory(string carNumber, int lapCount,
        string lastLapTime = "00:01:30.000", Flags trackFlag = Flags.Green, string totalTime = "00:05:00.000")
    {
        var laps = new List<CarPosition>();
        for (int i = 0; i < lapCount; i++)
        {
            laps.Add(CreateTestCarPosition(carNumber, "GT3", false, lastLapTime, totalTime, 1, trackFlag));
        }
        return laps;
    }

    #endregion
}
