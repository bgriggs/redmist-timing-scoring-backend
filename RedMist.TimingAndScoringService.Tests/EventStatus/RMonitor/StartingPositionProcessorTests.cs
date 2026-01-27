using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.Database.Models;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.RMonitor;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.RMonitor;

[TestClass]
public class StartingPositionProcessorTests
{
    private StartingPositionProcessor _processor = null!;
    private SessionContext _sessionContext = null!;
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private TsContext _dbContext = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "47" } })
            .Build();

        _dbContextFactory = CreateDbContextFactory();
        _dbContext = _dbContextFactory.CreateDbContext();
        _sessionContext = new SessionContext(config, _dbContextFactory);

        _processor = new StartingPositionProcessor(_sessionContext, _mockLoggerFactory.Object, _dbContextFactory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbContext?.Dispose();
    }

    #region GetLapNumberPriorToGreen Tests

    [TestMethod]
    public void GetLapNumberPriorToGreen_NoGreenFlag_ReturnsNegative()
    {
        // Arrange
        var laps = new List<CarPosition>
        {
            CreateCarPosition("1", 1, 0, Flags.Yellow),
            CreateCarPosition("1", 1, 1, Flags.Yellow),
            CreateCarPosition("2", 2, 0, Flags.Yellow),
            CreateCarPosition("2", 2, 1, Flags.Yellow)
        };

        // Act
        var result = StartingPositionProcessor.GetLapNumberPriorToGreen(laps);

        // Assert
        Assert.AreEqual(-1, result);
    }

    [TestMethod]
    public void GetLapNumberPriorToGreen_GreenFlagOnLapZero_ReturnsNegative()
    {
        // Arrange
        var laps = new List<CarPosition>
        {
            CreateCarPosition("1", 1, 0, Flags.Green),
            CreateCarPosition("2", 2, 0, Flags.Green)
        };

        // Act
        var result = StartingPositionProcessor.GetLapNumberPriorToGreen(laps);

        // Assert
        Assert.AreEqual(-1, result);
    }

    [TestMethod]
    public void GetLapNumberPriorToGreen_GreenFlagOnLapOne_ReturnsLapZero()
    {
        // Arrange
        var laps = new List<CarPosition>
        {
            CreateCarPosition("1", 1, 0, Flags.Yellow),
            CreateCarPosition("1", 1, 1, Flags.Green),
            CreateCarPosition("2", 2, 0, Flags.Yellow),
            CreateCarPosition("2", 2, 1, Flags.Green)
        };

        // Act
        var result = StartingPositionProcessor.GetLapNumberPriorToGreen(laps);

        // Assert
        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public void GetLapNumberPriorToGreen_GreenFlagOnLapTwo_ReturnsLapOne()
    {
        // Arrange
        var laps = new List<CarPosition>
        {
            CreateCarPosition("1", 1, 0, Flags.Yellow),
            CreateCarPosition("1", 1, 1, Flags.Yellow),
            CreateCarPosition("1", 1, 2, Flags.Green),
            CreateCarPosition("2", 2, 0, Flags.Yellow),
            CreateCarPosition("2", 2, 1, Flags.Yellow),
            CreateCarPosition("2", 2, 2, Flags.Green)
        };

        // Act
        var result = StartingPositionProcessor.GetLapNumberPriorToGreen(laps);

        // Assert
        Assert.AreEqual(1, result);
    }

    [TestMethod]
    public void GetLapNumberPriorToGreen_MultipleCarsMixedFlags_ReturnsCorrectLap()
    {
        // Arrange - Simulating a rolling start where green comes on lap 2
        var laps = new List<CarPosition>
        {
            CreateCarPosition("1", 1, 0, Flags.Yellow),
            CreateCarPosition("1", 1, 1, Flags.Yellow),
            CreateCarPosition("1", 1, 2, Flags.Green),
            CreateCarPosition("1", 1, 3, Flags.Green),
            CreateCarPosition("2", 2, 0, Flags.Yellow),
            CreateCarPosition("2", 2, 1, Flags.Yellow),
            CreateCarPosition("2", 2, 2, Flags.Green),
            CreateCarPosition("3", 3, 0, Flags.Yellow),
            CreateCarPosition("3", 3, 1, Flags.Yellow),
            CreateCarPosition("3", 3, 2, Flags.Green)
        };

        // Act
        var result = StartingPositionProcessor.GetLapNumberPriorToGreen(laps);

        // Assert
        Assert.AreEqual(1, result);
    }

    [TestMethod]
    public void GetLapNumberPriorToGreen_EmptyList_ReturnsNegative()
    {
        // Arrange
        var laps = new List<CarPosition>();

        // Act
        var result = StartingPositionProcessor.GetLapNumberPriorToGreen(laps);

        // Assert
        Assert.AreEqual(-1, result);
    }

    #endregion

    #region LoadStartingLapsAsync Tests

    [TestMethod]
    public async Task LoadStartingLapsAsync_WithLapsInDatabase_ReturnsFilteredLaps()
    {
        // Arrange
        await SeedDatabaseWithLaps(67, 5);

        // Act
        var result = await _processor.LoadStartingLapsAsync(67);

        // Assert
        Assert.IsNotNull(result);
        // Should load laps 0-4 inclusive (5 laps)
        Assert.HasCount(5, result);
        Assert.IsTrue(result.All(l => l.LastLapCompleted >= 0 && l.LastLapCompleted <= 4));
    }

    [TestMethod]
    public async Task LoadStartingLapsAsync_NoLapsInDatabase_ReturnsEmptyList()
    {
        // Arrange - No laps seeded

        // Act
        var result = await _processor.LoadStartingLapsAsync(99);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public async Task LoadStartingLapsAsync_OnlyLoadsLapsUpToLapFour()
    {
        // Arrange - Seed with laps 0-10
        await SeedDatabaseWithLaps(67, 11);

        // Act
        var result = await _processor.LoadStartingLapsAsync(67);

        // Assert
        Assert.IsNotNull(result);
        Assert.HasCount(5, result); // Only laps 0-4
        Assert.IsTrue(result.All(l => l.LastLapCompleted <= 4));
    }

    #endregion

    #region UpdateStartingPositionsFromHistoricLapsAsync Tests

    [TestMethod]
    public async Task UpdateStartingPositionsFromHistoricLaps_ValidData_UpdatesStartingPositions()
    {
        // Arrange
        await SetupSessionContextWithCars();
        await SeedDatabaseWithStartingLaps(67);

        // Act
        var result = await _processor.UpdateStartingPositionsFromHistoricLapsAsync(67);

        // Assert
        Assert.IsTrue(result);
        var startingPositions = _sessionContext.GetStartingPositions();
        Assert.IsNotEmpty(startingPositions);
    }

    [TestMethod]
    public async Task UpdateStartingPositionsFromHistoricLaps_NoGreenFlag_ReturnsFalse()
    {
        // Arrange - Seed with laps that never have a green flag
        await SeedDatabaseWithLapsNoGreen(67);

        // Act
        var result = await _processor.UpdateStartingPositionsFromHistoricLapsAsync(67);

        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task UpdateStartingPositionsFromHistoricLaps_NoLaps_ReturnsFalse()
    {
        // Arrange - No laps in database

        // Act
        var result = await _processor.UpdateStartingPositionsFromHistoricLapsAsync(67);

        // Assert
        Assert.IsFalse(result);
    }

    #endregion

    #region UpdateStartingPosition Tests

    [TestMethod]
    public void UpdateStartingPosition_YellowFlag_UpdatesPosition()
    {
        // Arrange
        // $G format: $G,position,regnum,laps,racetime
        var parts = new[] { "$G", "5", "\"1234BE\"", "0", "\"00:02:30.123\"" };
        _sessionContext.SessionState.CarPositions.Add(CreateCarPosition("1", 5, 0, Flags.Yellow));

        // Act
        _processor.UpdateStartingPosition(parts, "1", Flags.Yellow);

        // Assert
        var startingPos = _sessionContext.GetStartingPosition("1");
        Assert.IsNotNull(startingPos);
        Assert.AreEqual(5, startingPos.Value);
    }

    [TestMethod]
    public void UpdateStartingPosition_GreenFlag_UpdatesPosition()
    {
        // Arrange
        var parts = new[] { "$G", "3", "\"5678CD\"", "1", "\"00:02:30.123\"" };
        _sessionContext.SessionState.CarPositions.Add(CreateCarPosition("2", 3, 1, Flags.Green));

        // Act
        _processor.UpdateStartingPosition(parts, "2", Flags.Green);

        // Assert
        var startingPos = _sessionContext.GetStartingPosition("2");
        Assert.IsNotNull(startingPos);
        Assert.AreEqual(3, startingPos.Value);
    }

    [TestMethod]
    public void UpdateStartingPosition_RedFlag_DoesNotUpdatePosition()
    {
        // Arrange
        var parts = new[] { "$G", "1", "\"9999ZZ\"", "2", "\"00:02:30.123\"" };

        // Act
        _processor.UpdateStartingPosition(parts, "1", Flags.Red);

        // Assert
        var startingPos = _sessionContext.GetStartingPosition("1");
        Assert.IsNull(startingPos);
    }

    #endregion

    #region CheckHistoricLapStartingPositionsAsync Tests

    [TestMethod]
    public async Task CheckHistoricLapStartingPositions_AlreadyCheckedSession_ReturnsFalse()
    {
        // Arrange
        await SetupSessionContextWithCars();
        _sessionContext.SessionState.SessionId = 67;

        // Set up starting positions to simulate already checked
        _sessionContext.SetStartingPosition("1", 1);

        // Act
        var result1 = await _processor.CheckHistoricLapStartingPositionsAsync();
        var result2 = await _processor.CheckHistoricLapStartingPositionsAsync();

        // Assert
        Assert.IsFalse(result1, "First call should return false when positions already exist");
        Assert.IsFalse(result2, "Second call should return false for already checked session");
    }

    [TestMethod]
    public async Task CheckHistoricLapStartingPositions_HasStartingPositions_ReturnsFalse()
    {
        // Arrange
        await SetupSessionContextWithCars();
        _sessionContext.SessionState.SessionId = 67;
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Seed car positions so lap > 3
        foreach (var car in _sessionContext.SessionState.CarPositions)
        {
            car.LastLapCompleted = 5;
        }

        // Set up starting positions
        _sessionContext.SetStartingPosition("1", 1);
        _sessionContext.SetStartingPosition("2", 2);

        // Act
        var result = await _processor.CheckHistoricLapStartingPositionsAsync();

        // Assert
        Assert.IsFalse(result, "Should return false when starting positions already exist");
    }

    [TestMethod]
    public async Task CheckHistoricLapStartingPositions_LapTooEarly_ReturnsTrue()
    {
        // Arrange
        await SetupSessionContextWithCars();
        _sessionContext.SessionState.SessionId = 67;
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Set lap to 3 or less
        foreach (var car in _sessionContext.SessionState.CarPositions)
        {
            car.LastLapCompleted = 2;
        }

        // Act
        var result = await _processor.CheckHistoricLapStartingPositionsAsync();

        // Assert
        Assert.IsTrue(result, "Should return true but not perform check when lap <= 3");

        // Verify no starting positions were set
        var startingPositions = _sessionContext.GetStartingPositions();
        Assert.IsEmpty(startingPositions, "No starting positions should be set");
    }

    [TestMethod]
    public async Task CheckHistoricLapStartingPositions_WrongFlag_ReturnsTrue()
    {
        // Arrange
        await SetupSessionContextWithCars();
        _sessionContext.SessionState.SessionId = 67;
        _sessionContext.SessionState.CurrentFlag = Flags.Checkered; // Invalid flag

        // Seed car positions so lap > 3
        foreach (var car in _sessionContext.SessionState.CarPositions)
        {
            car.LastLapCompleted = 5;
        }

        // Act
        var result = await _processor.CheckHistoricLapStartingPositionsAsync();

        // Assert
        Assert.IsTrue(result, "Should return true but not perform check for non-racing flags");

        // Verify no starting positions were set
        var startingPositions = _sessionContext.GetStartingPositions();
        Assert.IsEmpty(startingPositions, "No starting positions should be set");
    }

    [TestMethod]
    public async Task CheckHistoricLapStartingPositions_GreenFlag_ValidLap_NoHistoricalData_ReturnsTrue()
    {
        // Arrange
        await SetupSessionContextWithCars();
        _sessionContext.SessionState.SessionId = 67;
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Seed car positions so lap > 3
        foreach (var car in _sessionContext.SessionState.CarPositions)
        {
            car.LastLapCompleted = 5;
        }

        // No historical lap data in database

        // Act
        var result = await _processor.CheckHistoricLapStartingPositionsAsync();

        // Assert
        Assert.IsTrue(result, "Should return true after attempting check");

        // Verify warning was logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Could not determine starting positions")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [TestMethod]
    public async Task CheckHistoricLapStartingPositions_YellowFlag_ValidLap_WithHistoricalData_ReturnsTrue()
    {
        // Arrange
        await SetupSessionContextWithCars();
        await SeedDatabaseWithStartingLaps(67);
        _sessionContext.SessionState.SessionId = 67;
        _sessionContext.SessionState.CurrentFlag = Flags.Yellow;

        // Seed car positions so lap > 3
        foreach (var car in _sessionContext.SessionState.CarPositions)
        {
            car.LastLapCompleted = 5;
        }

        // Act
        var result = await _processor.CheckHistoricLapStartingPositionsAsync();

        // Assert
        Assert.IsTrue(result, "Should return true after successful check");

        // Verify starting positions were set
        var startingPositions = _sessionContext.GetStartingPositions();
        Assert.IsNotEmpty(startingPositions, "Starting positions should be populated");

        // Verify info log was written
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("have been determined from historical laps")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [TestMethod]
    public async Task CheckHistoricLapStartingPositions_RedFlag_ValidLap_WithHistoricalData_ReturnsTrue()
    {
        // Arrange
        await SetupSessionContextWithCars();
        await SeedDatabaseWithStartingLaps(67);
        _sessionContext.SessionState.SessionId = 67;
        _sessionContext.SessionState.CurrentFlag = Flags.Red;

        // Seed car positions so lap > 3
        foreach (var car in _sessionContext.SessionState.CarPositions)
        {
            car.LastLapCompleted = 5;
        }

        // Act
        var result = await _processor.CheckHistoricLapStartingPositionsAsync();

        // Assert
        Assert.IsTrue(result, "Should return true after successful check");

        var startingPositions = _sessionContext.GetStartingPositions();
        Assert.IsNotEmpty(startingPositions, "Starting positions should be populated");
    }

    [TestMethod]
    public async Task CheckHistoricLapStartingPositions_Purple35Flag_ValidLap_WithHistoricalData_ReturnsTrue()
    {
        // Arrange
        await SetupSessionContextWithCars();
        await SeedDatabaseWithStartingLaps(67);
        _sessionContext.SessionState.SessionId = 67;
        _sessionContext.SessionState.CurrentFlag = Flags.Purple35;

        // Seed car positions so lap > 3
        foreach (var car in _sessionContext.SessionState.CarPositions)
        {
            car.LastLapCompleted = 5;
        }

        // Act
        var result = await _processor.CheckHistoricLapStartingPositionsAsync();

        // Assert
        Assert.IsTrue(result, "Should return true after successful check");

        var startingPositions = _sessionContext.GetStartingPositions();
        Assert.IsNotEmpty(startingPositions, "Starting positions should be populated");
    }

    [TestMethod]
    public async Task CheckHistoricLapStartingPositions_SessionChange_ResetsCheck()
    {
        // Arrange - Session 67
        await SetupSessionContextWithCars();
        await SeedDatabaseWithStartingLaps(67);
        _sessionContext.SessionState.SessionId = 67;
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        foreach (var car in _sessionContext.SessionState.CarPositions)
        {
            car.LastLapCompleted = 5;
        }

        // Act - First check for session 67
        var result1 = await _processor.CheckHistoricLapStartingPositionsAsync();
        var session67Positions = _sessionContext.GetStartingPositions().Count;

        // Change to session 68 and seed new data
        await SeedDatabaseWithStartingLaps(68);
        await _sessionContext.NewSession(68, "New Session");
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Re-setup cars for new session
        for (int i = 1; i <= 5; i++)
        {
            var car = CreateCarPosition(i.ToString(), i, 5, Flags.Green);
            _sessionContext.SessionState.CarPositions.Add(car);
        }

        // Act - Second check for session 68
        var result2 = await _processor.CheckHistoricLapStartingPositionsAsync();
        var session68Positions = _sessionContext.GetStartingPositions().Count;

        // Assert
        Assert.IsTrue(result1, "First session check should succeed");
        Assert.IsGreaterThan(0, session67Positions, "Session 67 should have starting positions");
        Assert.IsTrue(result2, "New session check should succeed");
        Assert.IsGreaterThan(0, session68Positions, "Session 68 should have starting positions");
    }

    [TestMethod]
    public async Task CheckHistoricLapStartingPositions_ExactlyLapFour_DoesNotTriggerCheck()
    {
        // Arrange
        await SetupSessionContextWithCars();
        _sessionContext.SessionState.SessionId = 67;
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Set lap to exactly 3 (boundary condition)
        foreach (var car in _sessionContext.SessionState.CarPositions)
        {
            car.LastLapCompleted = 3;
        }

        // Act
        var result = await _processor.CheckHistoricLapStartingPositionsAsync();

        // Assert
        Assert.IsTrue(result, "Should return true even when lap is exactly 3 (not > 3)");

        // Verify no starting positions were set
        var startingPositions = _sessionContext.GetStartingPositions();
        Assert.IsEmpty(startingPositions, "No starting positions should be set when lap <= 3");
    }

    [TestMethod]
    public async Task CheckHistoricLapStartingPositions_LapFourPointOne_TriggersCheck()
    {
        // Arrange
        await SetupSessionContextWithCars();
        await SeedDatabaseWithStartingLaps(67);
        _sessionContext.SessionState.SessionId = 67;
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        // Set lap to 4 (just over boundary)
        foreach (var car in _sessionContext.SessionState.CarPositions)
        {
            car.LastLapCompleted = 4;
        }

        // Act
        var result = await _processor.CheckHistoricLapStartingPositionsAsync();

        // Assert
        Assert.IsTrue(result, "Should return true when lap > 3");

        var startingPositions = _sessionContext.GetStartingPositions();
        Assert.IsNotEmpty(startingPositions, "Starting positions should be populated");
    }

    [TestMethod]
    public async Task CheckHistoricLapStartingPositions_LogsInformationWhenChecking()
    {
        // Arrange
        await SetupSessionContextWithCars();
        await SeedDatabaseWithStartingLaps(67);
        _sessionContext.SessionState.SessionId = 67;
        _sessionContext.SessionState.CurrentFlag = Flags.Green;

        foreach (var car in _sessionContext.SessionState.CarPositions)
        {
            car.LastLapCompleted = 5;
        }

        // Act
        var result = await _processor.CheckHistoricLapStartingPositionsAsync();

        // Assert
        Assert.IsTrue(result);

        // Verify "Performing historical check" log
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Performing historical check")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        // Verify "have been determined" log
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("have been determined from historical laps")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region Real Data Integration Tests

    [TestMethod]
    public async Task LoadRealCsvData_ProcessesCorrectly()
    {
        // Arrange
        var csvPath = Path.Combine("EventStatus", "RMonitor", "data-1767472981761.csv");
        await LoadCsvIntoDatabase(csvPath);
        await SetupSessionContextWithCarsFromCsv();

        // Act
        var laps = await _processor.LoadStartingLapsAsync(67);

        // Assert
        Assert.IsNotNull(laps);
        Assert.IsNotEmpty(laps);

        // Verify we have multiple cars
        var distinctCars = laps.Select(l => l.Number).Distinct().Count();
        Assert.IsGreaterThan(1, distinctCars, $"Expected multiple cars, got {distinctCars}");
    }

    [TestMethod]
    public async Task RealData_GetLapNumberPriorToGreen_ReturnsCorrectLap()
    {
        // Arrange
        var csvPath = Path.Combine("EventStatus", "RMonitor", "data-1767472981761.csv");
        await LoadCsvIntoDatabase(csvPath);

        // Act
        var laps = await _processor.LoadStartingLapsAsync(67);
        var lapNumber = StartingPositionProcessor.GetLapNumberPriorToGreen(laps);

        // Assert
        // Based on the CSV data, green flag appears on lap 1 (Flag = 1 which is Green)
        // So the lap prior to green should be lap 0
        Assert.AreEqual(0, lapNumber, "Expected lap 0 to be the lap prior to green");
    }

    [TestMethod]
    public async Task RealData_UpdateStartingPositions_SetsCorrectPositions()
    {
        // Arrange
        var csvPath = Path.Combine("EventStatus", "RMonitor", "data-1767472981761.csv");
        await LoadCsvIntoDatabase(csvPath);
        await SetupSessionContextWithCarsFromCsv();

        // Act
        var result = await _processor.UpdateStartingPositionsFromHistoricLapsAsync(67);

        // Assert
        Assert.IsTrue(result, "Should successfully update starting positions");

        var startingPositions = _sessionContext.GetStartingPositions();
        Assert.IsNotEmpty(startingPositions, "Should have starting positions");

        // Verify positions are valid (positive integers)
        foreach (var kvp in startingPositions)
        {
            Assert.IsGreaterThan(0, kvp.Value, $"Car {kvp.Key} should have positive starting position");
        }

        // Verify we have the expected cars from the CSV (sampling a few)
        Assert.IsTrue(startingPositions.ContainsKey("1"), "Should have car #1");
        Assert.IsTrue(startingPositions.ContainsKey("109"), "Should have car #109");
        Assert.IsTrue(startingPositions.ContainsKey("11"), "Should have car #11");
    }

    [TestMethod]
    public async Task RealData_VerifyStartingOrderMatchesLapZeroPositions()
    {
        // Arrange
        var csvPath = Path.Combine("EventStatus", "RMonitor", "data-1767472981761.csv");
        await LoadCsvIntoDatabase(csvPath);
        await SetupSessionContextWithCarsFromCsv();

        // Act
        var result = await _processor.UpdateStartingPositionsFromHistoricLapsAsync(67);
        var laps = await _processor.LoadStartingLapsAsync(67);
        var lapZeroPositions = laps.Where(l => l.LastLapCompleted == 0)
            .OrderBy(l => l.OverallPosition)
            .ToList();

        // Assert
        Assert.IsTrue(result);
        var startingPositions = _sessionContext.GetStartingPositions();

        // Verify starting positions match lap 0 positions
        foreach (var lap in lapZeroPositions)
        {
            if (string.IsNullOrEmpty(lap.Number)) continue;

            Assert.IsTrue(startingPositions.ContainsKey(lap.Number),
                $"Car {lap.Number} should have a starting position");
            Assert.AreEqual(lap.OverallPosition, startingPositions[lap.Number],
                $"Car {lap.Number} starting position should match lap 0 position");
        }
    }

    #endregion

    #region Helper Methods

    private static CarPosition CreateCarPosition(string number, int position, int lapCompleted, Flags flag)
    {
        return new CarPosition
        {
            Number = number,
            OverallPosition = position,
            ClassPosition = position,
            LastLapCompleted = lapCompleted,
            TrackFlag = flag,
            Class = "Test Class"
        };
    }

    private async Task SeedDatabaseWithLaps(int sessionId, int numberOfLaps)
    {
        for (int lap = 0; lap < numberOfLaps; lap++)
        {
            var carPosition = CreateCarPosition("1", 1, lap, lap == 0 ? Flags.Yellow : Flags.Green);
            var lapData = JsonSerializer.Serialize(carPosition);

            _dbContext.CarLapLogs.Add(new CarLapLog
            {
                EventId = 47,
                SessionId = sessionId,
                CarNumber = "1",
                Timestamp = DateTime.UtcNow,
                LapNumber = lap,
                Flag = (int)carPosition.TrackFlag,
                LapData = lapData
            });
        }
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedDatabaseWithStartingLaps(int sessionId)
    {
        // Simulate a rolling start: lap 0 is yellow, lap 1+ is green
        var cars = new[] { "1", "2", "3", "4", "5" };

        for (int i = 0; i < cars.Length; i++)
        {
            // Lap 0 - Yellow flag
            var lap0Position = CreateCarPosition(cars[i], i + 1, 0, Flags.Yellow);
            _dbContext.CarLapLogs.Add(new CarLapLog
            {
                EventId = 47,
                SessionId = sessionId,
                CarNumber = cars[i],
                Timestamp = DateTime.UtcNow.AddSeconds(i),
                LapNumber = 0,
                Flag = (int)Flags.Yellow,
                LapData = JsonSerializer.Serialize(lap0Position)
            });

            // Lap 1 - Green flag
            var lap1Position = CreateCarPosition(cars[i], i + 1, 1, Flags.Green);
            _dbContext.CarLapLogs.Add(new CarLapLog
            {
                EventId = 47,
                SessionId = sessionId,
                CarNumber = cars[i],
                Timestamp = DateTime.UtcNow.AddSeconds(100 + i),
                LapNumber = 1,
                Flag = (int)Flags.Green,
                LapData = JsonSerializer.Serialize(lap1Position)
            });
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedDatabaseWithLapsNoGreen(int sessionId)
    {
        for (int lap = 0; lap < 5; lap++)
        {
            var carPosition = CreateCarPosition("1", 1, lap, Flags.Yellow);
            var lapData = JsonSerializer.Serialize(carPosition);

            _dbContext.CarLapLogs.Add(new CarLapLog
            {
                EventId = 47,
                SessionId = sessionId,
                CarNumber = "1",
                Timestamp = DateTime.UtcNow,
                LapNumber = lap,
                Flag = (int)Flags.Yellow,
                LapData = lapData
            });
        }
        await _dbContext.SaveChangesAsync();
    }

    private async Task SetupSessionContextWithCars()
    {
        using (await _sessionContext.SessionStateLock.AcquireWriteLockAsync(_sessionContext.CancellationToken))
        {
            _sessionContext.SessionState.SessionId = 67;
            for (int i = 1; i <= 5; i++)
            {
                var car = CreateCarPosition(i.ToString(), i, 0, Flags.Yellow);
                _sessionContext.SessionState.CarPositions.Add(car);
            }
        }
    }

    private async Task LoadCsvIntoDatabase(string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            Assert.Fail($"CSV file not found at: {csvPath}");
        }

        var lines = await File.ReadAllLinesAsync(csvPath);
        // Skip header
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var parts = ParseCsvLine(line);

            if (parts.Length < 8) continue;

            // The LapData is in the 8th column (index 7) and needs quotes removed and unescaped
            var lapData = parts[7];
            if (lapData.StartsWith("\"") && lapData.EndsWith("\""))
            {
                lapData = lapData[1..^1]; // Remove surrounding quotes
            }
            // Unescape double quotes
            lapData = lapData.Replace("\"\"", "\"");

            var carLapLog = new CarLapLog
            {
                Id = long.Parse(parts[0].Trim('"')),
                EventId = int.Parse(parts[1].Trim('"')),
                SessionId = int.Parse(parts[2].Trim('"')),
                CarNumber = parts[3].Trim('"'),
                Timestamp = DateTime.Parse(parts[4].Trim('"')),
                LapNumber = int.Parse(parts[5].Trim('"')),
                Flag = int.Parse(parts[6].Trim('"')),
                LapData = lapData
            };

            _dbContext.CarLapLogs.Add(carLapLog);
        }

        await _dbContext.SaveChangesAsync();
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        bool escapeNext = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (escapeNext)
            {
                current.Append(c);
                escapeNext = false;
                continue;
            }

            if (c == '"' && i + 1 < line.Length && line[i + 1] == '"' && inQuotes)
            {
                // Handle escaped quotes within quoted strings
                current.Append(c);
                current.Append(line[i + 1]);
                i++; // Skip next quote
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return [.. result];
    }

    private async Task SetupSessionContextWithCarsFromCsv()
    {
        // Load unique car numbers from the database
        var carNumbers = await _dbContext.CarLapLogs
            .Where(cl => cl.SessionId == 67)
            .Select(cl => cl.CarNumber)
            .Distinct()
            .ToListAsync();

        using (await _sessionContext.SessionStateLock.AcquireWriteLockAsync(_sessionContext.CancellationToken))
        {
            _sessionContext.SessionState.SessionId = 67;
            foreach (var carNumber in carNumbers)
            {
                var car = CreateCarPosition(carNumber, 1, 0, Flags.Yellow);
                _sessionContext.SessionState.CarPositions.Add(car);
            }
        }
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var databaseName = $"TestDatabase_{Guid.NewGuid()}";
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase(databaseName);
        var options = optionsBuilder.Options;
        return new TestDbContextFactory(options);
    }

    #endregion
}
