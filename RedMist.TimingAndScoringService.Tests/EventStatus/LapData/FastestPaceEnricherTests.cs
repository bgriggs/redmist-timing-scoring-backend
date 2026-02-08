using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.LapData;

[TestClass]
public class FastestPaceEnricherTests
{
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private Mock<IConnectionMultiplexer> _mockConnectionMultiplexer = null!;
    private Mock<IDatabase> _mockDatabase = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private SessionContext _sessionContext = null!;
    private FakeTimeProvider _timeProvider = null!;
    private Mock<CarLapHistoryService> _mockCarLapHistoryService = null!;
    private FastestPaceEnricher _enricher = null!;
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

        _mockCarLapHistoryService = new Mock<CarLapHistoryService>(
            _mockLoggerFactory.Object,
            _mockConnectionMultiplexer.Object,
            _sessionContext);

        _enricher = new FastestPaceEnricher(
            _mockLoggerFactory.Object,
            _mockCarLapHistoryService.Object,
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
        bool fastestPace = false, string lastLapTime = "00:01:30.000")
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
            LastLapCompleted = 1,
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
            IsStale = false,
            TrackFlag = Flags.Green,
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
            InClassFastestAveragePace = fastestPace,
            LastLapTime = lastLapTime,
            TotalTime = "00:00:00.000"
        };
    }

    private static TimingMessage CreateLapCompletedMessage(string carNumber, int lapNumber, string carClass)
    {
        var lapCompleted = new LapCompleted(carNumber, lapNumber, carClass, DateTime.UtcNow);
        return new TimingMessage(
            Consts.LAP_COMPLETED_TYPE,
            JsonSerializer.Serialize(lapCompleted),
            SessionId,
            DateTime.UtcNow);
    }

    #region ProcessAsync Tests

    [TestMethod]
    public void Deserialization_LapCompletedMessage_Works()
    {
        // Arrange
        var message = CreateLapCompletedMessage("1", 5, "GT3");

        // Act
        var lapCompleted = JsonSerializer.Deserialize<LapCompleted>(message.Data);

        // Assert
        Assert.IsNotNull(lapCompleted);
        Assert.AreEqual("1", lapCompleted.CarNumber);
        Assert.AreEqual(5, lapCompleted.LapNumber);
        Assert.AreEqual("GT3", lapCompleted.Class);
    }

    [TestMethod]
    public async Task ProcessAsync_NonLapCompletedMessage_ReturnsEmptyList()
    {
        // Arrange
        var message = new TimingMessage(
            Consts.RMONITOR_TYPE,
            "{}",
            SessionId,
            DateTime.UtcNow);

        // Act
        var result = await _enricher.ProcessAsync(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task ProcessAsync_NoClassCars_ReturnsEmptyList()
    {
        // Arrange
        var message = CreateLapCompletedMessage("1", 5, "GT3");

        // Act
        var result = await _enricher.ProcessAsync(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task ProcessAsync_SingleCarInClass_SetsFastestPaceTrue()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "GT3", false);
        _sessionContext.SessionState.CarPositions = [car1];

        var laps = CreateLapHistory("1", 5, 90.0);
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("1"))
            .ReturnsAsync(laps);

        var message = CreateLapCompletedMessage("1", 5, "GT3");

        // Act
        var result = await _enricher.ProcessAsync(message);

        // Assert
        Assert.HasCount(1, result);
        Assert.IsTrue(result[0].InClassFastestAveragePace);
        Assert.IsTrue(car1.InClassFastestAveragePace, "Car should be updated with fastest pace");
    }

    [TestMethod]
    public async Task ProcessAsync_TwoCarsInClass_SetsFastestCorrectly()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "GT3", false);
        var car2 = CreateTestCarPosition("2", "GT3", false);
        _sessionContext.SessionState.CarPositions = [car1, car2];

        // Car 1 has faster average (85 seconds)
        var laps1 = CreateLapHistory("1", 5, 85.0);
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("1"))
            .ReturnsAsync(laps1);

        // Car 2 has slower average (90 seconds)
        var laps2 = CreateLapHistory("2", 5, 90.0);
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("2"))
            .ReturnsAsync(laps2);

        var message = CreateLapCompletedMessage("1", 5, "GT3");

        // Act
        var result = await _enricher.ProcessAsync(message);

        // Assert - Only car 1 changes (false → true), car 2 stays false
        Assert.HasCount(1, result, "Only car 1 should have a patch since only it changed");

        var car1Patch = result.FirstOrDefault(p => p.Number == "1");

        Assert.IsNotNull(car1Patch, "Car 1 should have a patch");
        Assert.IsTrue(car1Patch.InClassFastestAveragePace, "Car 1 should be fastest");
        Assert.IsTrue(car1.InClassFastestAveragePace, "Car 1 object should be updated");
        Assert.IsFalse(car2.InClassFastestAveragePace, "Car 2 object should remain false");
    }

    [TestMethod]
    public async Task ProcessAsync_CarBecomesSlower_UpdatesPatchesCorrectly()
    {
        // Arrange - Car 1 was previously fastest
        var car1 = CreateTestCarPosition("1", "GT3", true);
        var car2 = CreateTestCarPosition("2", "GT3", false);
        _sessionContext.SessionState.CarPositions = [car1, car2];

        // Verify session context has the cars
        var carsInClass = _sessionContext.GetClassCarPositions("GT3");
        Assert.HasCount(2, carsInClass, "Session should have 2 GT3 cars");
        Assert.AreEqual("1", carsInClass[0].Number);
        Assert.AreEqual("2", carsInClass[1].Number);
        Assert.IsTrue(carsInClass[0].InClassFastestAveragePace, "Car 1 should start as fastest");
        Assert.IsFalse(carsInClass[1].InClassFastestAveragePace, "Car 2 should start as not fastest");

        // Now car 2 has faster average
        var laps1 = CreateLapHistory("1", 5, 90.0);
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("1"))
            .ReturnsAsync(laps1)
            .Verifiable();

        var laps2 = CreateLapHistory("2", 5, 85.0);
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("2"))
            .ReturnsAsync(laps2)
            .Verifiable();

        var message = CreateLapCompletedMessage("2", 5, "GT3");

        // Act
        var result = await _enricher.ProcessAsync(message);

        // Assert - Verify mocks were called first
        _mockCarLapHistoryService.Verify(x => x.GetLapsAsync("1"), Times.AtLeastOnce, "Should query car 1 lap history");
        _mockCarLapHistoryService.Verify(x => x.GetLapsAsync("2"), Times.AtLeastOnce, "Should query car 2 lap history");

        Assert.AreEqual(2, result.Count, "Both cars should have patches since both changed");

        var car1Patch = result.FirstOrDefault(p => p.Number == "1");
        var car2Patch = result.FirstOrDefault(p => p.Number == "2");

        Assert.IsNotNull(car1Patch);
        Assert.IsNotNull(car2Patch);
        Assert.IsFalse(car1Patch.InClassFastestAveragePace, "Car 1 should lose fastest");
        Assert.IsTrue(car2Patch.InClassFastestAveragePace, "Car 2 should become fastest");
        Assert.IsFalse(car1.InClassFastestAveragePace, "Car 1 object should be updated");
        Assert.IsTrue(car2.InClassFastestAveragePace, "Car 2 object should be updated");
    }

    [TestMethod]
    public async Task ProcessAsync_NoChangeInFastest_ReturnsEmptyList()
    {
        // Arrange - Car 1 is already fastest
        var car1 = CreateTestCarPosition("1", "GT3", true);
        var car2 = CreateTestCarPosition("2", "GT3", false);
        _sessionContext.SessionState.CarPositions = [car1, car2];

        // Car 1 still has faster average
        var laps1 = CreateLapHistory("1", 5, 85.0);
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("1"))
            .ReturnsAsync(laps1);

        var laps2 = CreateLapHistory("2", 5, 90.0);
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("2"))
            .ReturnsAsync(laps2);

        var message = CreateLapCompletedMessage("1", 5, "GT3");

        // Act
        var result = await _enricher.ProcessAsync(message);

        // Assert
        Assert.IsEmpty(result, "No patches should be returned when nothing changes");
    }

    [TestMethod]
    public async Task ProcessAsync_MultipleClasses_OnlyProcessesCorrectClass()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "GT3", false);
        var car2 = CreateTestCarPosition("2", "GT3", false);
        var car3 = CreateTestCarPosition("3", "LMP2", false);
        _sessionContext.SessionState.CarPositions = [car1, car2, car3];

        var laps1 = CreateLapHistory("1", 5, 85.0);
        var laps2 = CreateLapHistory("2", 5, 90.0);
        var laps3 = CreateLapHistory("3", 5, 80.0);

        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("1")).ReturnsAsync(laps1);
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("2")).ReturnsAsync(laps2);
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("3")).ReturnsAsync(laps3);

        var message = CreateLapCompletedMessage("1", 5, "GT3");

        // Act
        var result = await _enricher.ProcessAsync(message);

        // Assert - Only car 1 changes (false → true), car 2 stays false
        Assert.HasCount(1, result, "Only car 1 should have a patch (GT3 winner)");
        Assert.AreEqual("1", result[0].Number, "Car 1 should be the one with a patch");
        Assert.IsTrue(result[0].InClassFastestAveragePace, "Car 1 should be marked as fastest");

        // Verify only GT3 cars were queried, not LMP2
        _mockCarLapHistoryService.Verify(x => x.GetLapsAsync("1"), Times.Once);
        _mockCarLapHistoryService.Verify(x => x.GetLapsAsync("2"), Times.Once);
        _mockCarLapHistoryService.Verify(x => x.GetLapsAsync("3"), Times.Never, "LMP2 car should not be queried");
    }

    [TestMethod]
    public async Task ProcessAsync_CarWithNotEnoughLaps_IsNotConsideredForFastest()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "GT3", false);
        var car2 = CreateTestCarPosition("2", "GT3", false);
        _sessionContext.SessionState.CarPositions = [car1, car2];

        // Car 1 has only 3 laps (not enough for average)
        var laps1 = CreateLapHistory("1", 3, 80.0);
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("1"))
            .ReturnsAsync(laps1);

        // Car 2 has 5 laps
        var laps2 = CreateLapHistory("2", 5, 90.0);
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("2"))
            .ReturnsAsync(laps2);

        var message = CreateLapCompletedMessage("1", 3, "GT3");

        // Act
        var result = await _enricher.ProcessAsync(message);

        // Assert
        Assert.HasCount(1, result, "Only car 2 should get a patch");
        var car2Patch = result.First();
        Assert.AreEqual("2", car2Patch.Number);
        Assert.IsTrue(car2Patch.InClassFastestAveragePace);
    }

    [TestMethod]
    public async Task ProcessAsync_NullLapHistory_CarIsIgnored()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", "GT3", false);
        var car2 = CreateTestCarPosition("2", "GT3", false);
        _sessionContext.SessionState.CarPositions = [car1, car2];

        // Car 1 returns null lap history
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("1"))
            .ReturnsAsync((List<CarPosition>)null!);

        var laps2 = CreateLapHistory("2", 5, 90.0);
        _mockCarLapHistoryService.Setup(x => x.GetLapsAsync("2"))
            .ReturnsAsync(laps2);

        var message = CreateLapCompletedMessage("1", 5, "GT3");

        // Act
        var result = await _enricher.ProcessAsync(message);

        // Assert
        Assert.HasCount(1, result);
        Assert.AreEqual("2", result[0].Number);
        Assert.IsTrue(result[0].InClassFastestAveragePace);
    }

    #endregion

    #region CalculateAverageLapTime Tests

    [TestMethod]
    public void CalculateAverageLapTime_LessThanFiveLaps_ReturnsNull()
    {
        // Arrange
        var laps = CreateLapHistory("1", 4, 90.0);

        // Act
        var result = FastestPaceEnricher.CalculateAverageLapTime(laps);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void CalculateAverageLapTime_ExactlyFiveLaps_ReturnsAverage()
    {
        // Arrange
        var laps = new List<CarPosition>
        {
            CreateTestCarPosition("1", "GT3", false, "00:01:30.000"), // 90 seconds
            CreateTestCarPosition("1", "GT3", false, "00:01:32.000"), // 92 seconds
            CreateTestCarPosition("1", "GT3", false, "00:01:28.000"), // 88 seconds
            CreateTestCarPosition("1", "GT3", false, "00:01:31.000"), // 91 seconds
            CreateTestCarPosition("1", "GT3", false, "00:01:29.000")  // 89 seconds
        };
        // Average = (90 + 92 + 88 + 91 + 89) / 5 = 450 / 5 = 90 seconds = 90000ms

        // Act
        var result = FastestPaceEnricher.CalculateAverageLapTime(laps);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(90000, result.Value);
    }

    [TestMethod]
    public void CalculateAverageLapTime_MoreThanFiveLaps_OnlyUsesFirstFive()
    {
        // Arrange - Most recent 5 should be used
        var laps = new List<CarPosition>
        {
            CreateTestCarPosition("1", "GT3", false, "00:01:30.000"), // 90 (should be used)
            CreateTestCarPosition("1", "GT3", false, "00:01:30.000"), // 90 (should be used)
            CreateTestCarPosition("1", "GT3", false, "00:01:30.000"), // 90 (should be used)
            CreateTestCarPosition("1", "GT3", false, "00:01:30.000"), // 90 (should be used)
            CreateTestCarPosition("1", "GT3", false, "00:01:30.000"), // 90 (should be used)
            CreateTestCarPosition("1", "GT3", false, "00:02:00.000"), // 120 (should NOT be used)
            CreateTestCarPosition("1", "GT3", false, "00:02:00.000")  // 120 (should NOT be used)
        };

        // Act
        var result = FastestPaceEnricher.CalculateAverageLapTime(laps);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(90000, result.Value, "Should only average first 5 laps");
    }

    [TestMethod]
    public void CalculateAverageLapTime_WithInvalidLapTimes_IgnoresInvalid()
    {
        // Arrange - 5 laps total, 1 invalid, should average the 4 valid ones
        var laps = new List<CarPosition>
        {
            CreateTestCarPosition("1", "GT3", false, "00:01:30.000"), // 90 seconds = 90000ms
            CreateTestCarPosition("1", "GT3", false, "00:01:30.000"), // 90 seconds = 90000ms
            CreateTestCarPosition("1", "GT3", false, "invalid"),      // Invalid - skipped
            CreateTestCarPosition("1", "GT3", false, "00:01:30.000"), // 90 seconds = 90000ms
            CreateTestCarPosition("1", "GT3", false, "00:01:30.000")  // 90 seconds = 90000ms
        };
        // Total: 4 valid * 90000ms = 360000ms
        // Average: 360000 / 4 = 90000ms

        // Act
        var result = FastestPaceEnricher.CalculateAverageLapTime(laps);

        // Assert - Should calculate average of 4 valid laps
        Assert.IsNotNull(result);
        Assert.AreEqual(90000, result.Value, "Should average 4 valid laps and ignore the invalid one");
    }

    [TestMethod]
    public void CalculateAverageLapTime_AllInvalidTimes_ReturnsZero()
    {
        // Arrange
        var laps = new List<CarPosition>
        {
            CreateTestCarPosition("1", "GT3", false, "invalid"),
            CreateTestCarPosition("1", "GT3", false, ""),
            CreateTestCarPosition("1", "GT3", false, "abc"),
            CreateTestCarPosition("1", "GT3", false, null!),
            CreateTestCarPosition("1", "GT3", false, "xyz")
        };

        // Act
        var result = FastestPaceEnricher.CalculateAverageLapTime(laps);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Value);
    }

    [TestMethod]
    public void CalculateAverageLapTime_EmptyList_ReturnsNull()
    {
        // Arrange
        var laps = new List<CarPosition>();

        // Act
        var result = FastestPaceEnricher.CalculateAverageLapTime(laps);

        // Assert
        Assert.IsNull(result);
    }

    #endregion

    #region ParseRMTime Tests

    [TestMethod]
    public void ParseRMTime_ValidTimeWithMilliseconds_ParsesCorrectly()
    {
        // Arrange
        var timeString = "00:01:30.500";

        // Act
        var result = FastestPaceEnricher.ParseRMTime(timeString);

        // Assert
        Assert.AreEqual(TimeSpan.FromSeconds(90.5), result);
    }

    [TestMethod]
    public void ParseRMTime_ValidTimeWithoutMilliseconds_ParsesCorrectly()
    {
        // Arrange
        var timeString = "00:01:30";

        // Act
        var result = FastestPaceEnricher.ParseRMTime(timeString);

        // Assert
        Assert.AreEqual(TimeSpan.FromSeconds(90), result);
    }

    [TestMethod]
    public void ParseRMTime_HoursMinutesSecondsMilliseconds_ParsesCorrectly()
    {
        // Arrange
        var timeString = "01:23:45.678";

        // Act
        var result = FastestPaceEnricher.ParseRMTime(timeString);

        // Assert
        Assert.AreEqual(new TimeSpan(0, 1, 23, 45, 678), result);
    }

    [TestMethod]
    public void ParseRMTime_ZeroTime_ParsesCorrectly()
    {
        // Arrange
        var timeString = "00:00:00.000";

        // Act
        var result = FastestPaceEnricher.ParseRMTime(timeString);

        // Assert
        Assert.AreEqual(TimeSpan.Zero, result);
    }

    [TestMethod]
    public void ParseRMTime_InvalidFormat_ReturnsDefault()
    {
        // Arrange
        var timeString = "invalid";

        // Act
        var result = FastestPaceEnricher.ParseRMTime(timeString);

        // Assert
        Assert.AreEqual(TimeSpan.Zero, result);
    }

    [TestMethod]
    public void ParseRMTime_EmptyString_ReturnsDefault()
    {
        // Arrange
        var timeString = "";

        // Act
        var result = FastestPaceEnricher.ParseRMTime(timeString);

        // Assert
        Assert.AreEqual(TimeSpan.Zero, result);
    }

    [TestMethod]
    public void ParseRMTime_NullString_ReturnsDefault()
    {
        // Arrange
        string? timeString = null;

        // Act
        var result = FastestPaceEnricher.ParseRMTime(timeString!);

        // Assert
        Assert.AreEqual(TimeSpan.Zero, result);
    }

    #endregion

    #region Helper Methods

    private static List<CarPosition> CreateLapHistory(string carNumber, int lapCount, double avgLapTimeSeconds)
    {
        var laps = new List<CarPosition>();
        for (int i = 0; i < lapCount; i++)
        {
            var timeSpan = TimeSpan.FromSeconds(avgLapTimeSeconds);
            var timeString = $"{timeSpan.Hours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}.{timeSpan.Milliseconds:D3}";
            laps.Add(CreateTestCarPosition(carNumber, "GT3", false, timeString));
        }
        return laps;
    }

    #endregion
}
