using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.LapData;

[TestClass]
public class CarLapHistoryServiceTests
{
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private Mock<IConnectionMultiplexer> _mockConnectionMultiplexer = null!;
    private Mock<IDatabase> _mockDatabase = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private SessionContext _sessionContext = null!;
    private FakeTimeProvider _timeProvider = null!;
    private CarLapHistoryService _service = null!;
    private readonly List<(RedisKey key, RedisValue value)> _capturedListPushes = [];
    private readonly Dictionary<RedisKey, List<RedisValue>> _redisListStorage = [];
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

        _service = new CarLapHistoryService(
            _mockLoggerFactory.Object,
            _mockConnectionMultiplexer.Object,
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
        _capturedListPushes.Clear();
        _redisListStorage.Clear();
        _mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();

        _mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDatabase.Object);

        // Mock ListLeftPushAsync
        _mockDatabase.Setup(x => x.ListLeftPushAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, When, CommandFlags>((key, value, when, flags) =>
            {
                _capturedListPushes.Add((key, value));

                if (!_redisListStorage.ContainsKey(key))
                {
                    _redisListStorage[key] = [];
                }
                _redisListStorage[key].Insert(0, value); // Insert at beginning (left push)
            })
            .ReturnsAsync(1);

        // Mock ListTrimAsync
        _mockDatabase.Setup(x => x.ListTrimAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<long>(),
            It.IsAny<long>(),
            It.IsAny<CommandFlags>()))
            .Callback<RedisKey, long, long, CommandFlags>((key, start, stop, flags) =>
            {
                if (_redisListStorage.ContainsKey(key))
                {
                    var list = _redisListStorage[key];
                    var count = (int)(stop - start + 1);
                    if (list.Count > count)
                    {
                        list.RemoveRange(count, list.Count - count);
                    }
                }
            })
            .Returns(Task.CompletedTask);

        // Mock ListRangeAsync
        _mockDatabase.Setup(x => x.ListRangeAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<long>(),
            It.IsAny<long>(),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, long start, long stop, CommandFlags flags) =>
            {
                if (_redisListStorage.ContainsKey(key))
                {
                    var list = _redisListStorage[key];
                    var count = Math.Min((int)(stop - start + 1), list.Count);
                    return list.Take(count).ToArray();
                }
                return Array.Empty<RedisValue>();
            });
    }

    private static CarPosition CreateTestCarPosition(string carNumber, int lapNumber, double lastLapTime = 90.5)
    {
        return new CarPosition
        {
            Number = carNumber,
            Class = "A",
            OverallPosition = 1,
            TransponderId = 12345,
            EventId = "1",
            SessionId = "1",
            BestLap = 0,
            LastLapCompleted = lapNumber,
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
            LastLapTime = lastLapTime.ToString("F3"),
            TotalTime = "00:00:00.000"
        };
    }

    #region AddLapAsync Tests

    [TestMethod]
    public async Task AddLapAsync_ValidPosition_AddsToRedis()
    {
        // Arrange
        var position = CreateTestCarPosition("1", 1);

        // Act
        await _service.AddLapAsync(position);

        // Assert
        Assert.AreEqual(1, _capturedListPushes.Count, "Should capture one list push");

        var expectedKey = string.Format(Consts.CAR_LAP_HISTORY, EventId, "1");
        Assert.AreEqual(expectedKey, _capturedListPushes[0].key.ToString());

        var deserializedPosition = JsonSerializer.Deserialize<CarPosition>(_capturedListPushes[0].value.ToString());
        Assert.IsNotNull(deserializedPosition);
        Assert.AreEqual("1", deserializedPosition.Number);
        Assert.AreEqual(1, deserializedPosition.LastLapCompleted);
    }

    [TestMethod]
    public async Task AddLapAsync_MultipleLaps_MaintainsRollingWindow()
    {
        // Arrange
        var carNumber = "42";

        // Act - Add 7 laps (should only keep last 5)
        for (int i = 1; i <= 7; i++)
        {
            var position = CreateTestCarPosition(carNumber, i, 90.0 + i);
            await _service.AddLapAsync(position);
        }

        // Assert
        Assert.AreEqual(7, _capturedListPushes.Count, "Should have pushed 7 laps");

        var key = string.Format(Consts.CAR_LAP_HISTORY, EventId, carNumber);
        Assert.IsTrue(_redisListStorage.ContainsKey(key));
        Assert.AreEqual(5, _redisListStorage[key].Count, "Should only keep 5 laps");

        // Verify the most recent 5 laps are kept (laps 3-7)
        var storedLaps = _redisListStorage[key]
            .Select(v => JsonSerializer.Deserialize<CarPosition>(v.ToString()))
            .ToList();

        Assert.AreEqual(7, storedLaps[0]!.LastLapCompleted, "Most recent lap should be first");
        Assert.AreEqual(6, storedLaps[1]!.LastLapCompleted);
        Assert.AreEqual(5, storedLaps[2]!.LastLapCompleted);
        Assert.AreEqual(4, storedLaps[3]!.LastLapCompleted);
        Assert.AreEqual(3, storedLaps[4]!.LastLapCompleted, "Oldest kept lap should be 3");
    }

    [TestMethod]
    public async Task AddLapAsync_MultipleCars_KeepsSeparateHistory()
    {
        // Arrange & Act
        await _service.AddLapAsync(CreateTestCarPosition("1", 1));
        await _service.AddLapAsync(CreateTestCarPosition("2", 1));
        await _service.AddLapAsync(CreateTestCarPosition("1", 2));
        await _service.AddLapAsync(CreateTestCarPosition("2", 2));

        // Assert
        var key1 = string.Format(Consts.CAR_LAP_HISTORY, EventId, "1");
        var key2 = string.Format(Consts.CAR_LAP_HISTORY, EventId, "2");

        Assert.IsTrue(_redisListStorage.ContainsKey(key1));
        Assert.IsTrue(_redisListStorage.ContainsKey(key2));
        Assert.AreEqual(2, _redisListStorage[key1].Count, "Car 1 should have 2 laps");
        Assert.AreEqual(2, _redisListStorage[key2].Count, "Car 2 should have 2 laps");
    }

    [TestMethod]
    public async Task AddLapAsync_NullPosition_ThrowsNullReferenceException()
    {
        // Act & Assert - When position is null, we get NullReferenceException when accessing position.Number
        var exception = await Assert.ThrowsAsync<NullReferenceException>(() => _service.AddLapAsync(null!));
        Assert.IsNotNull(exception);
    }

    [TestMethod]
    public async Task AddLapAsync_NullCarNumber_ThrowsArgumentException()
    {
        // Arrange
        var position = CreateTestCarPosition("1", 1);
        position.Number = null;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _service.AddLapAsync(position));
        Assert.IsNotNull(exception);
    }

    [TestMethod]
    public async Task AddLapAsync_EmptyCarNumber_ThrowsArgumentException()
    {
        // Arrange
        var position = CreateTestCarPosition("1", 1);
        position.Number = string.Empty;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _service.AddLapAsync(position));
        Assert.IsNotNull(exception);
    }

    [TestMethod]
    public async Task AddLapAsync_PreservesAllCarPositionData()
    {
        // Arrange
        var position = new CarPosition
        {
            Number = "99",
            Class = "GT3",
            OverallPosition = 3,
            TransponderId = 12345,
            EventId = "1",
            SessionId = "1",
            BestLap = 0,
            LastLapCompleted = 5,
            OverallStartingPosition = 5,
            InClassStartingPosition = 3,
            OverallPositionsGained = 2,
            InClassPositionsGained = 1,
            ClassPosition = 2,
            PenalityLaps = 0,
            PenalityWarnings = 0,
            BlackFlags = 0,
            IsEnteredPit = false,
            IsPitStartFinish = false,
            IsExitedPit = false,
            IsInPit = false,
            LapIncludedPit = false,
            LastLoopName = "SF",
            IsStale = false,
            TrackFlag = Flags.Yellow,
            LocalFlag = Flags.Yellow,
            CompletedSections = [],
            ProjectedLapTimeMs = 120000,
            LapStartTime = TimeOnly.MinValue,
            DriverName = "Test Driver",
            DriverId = "DRV99",
            CurrentStatus = "Active",
            ImpactWarning = false,
            IsBestTime = false,
            IsBestTimeClass = false,
            IsOverallMostPositionsGained = true,
            IsClassMostPositionsGained = true,
            LastLapTime = "123.456",
            TotalTime = "00:10:23.456"
        };

        // Act
        await _service.AddLapAsync(position);

        // Assert
        var key = string.Format(Consts.CAR_LAP_HISTORY, EventId, "99");
        var storedValue = _redisListStorage[key][0];
        var deserializedPosition = JsonSerializer.Deserialize<CarPosition>(storedValue.ToString());

        Assert.IsNotNull(deserializedPosition);
        Assert.AreEqual("99", deserializedPosition.Number);
        Assert.AreEqual(5, deserializedPosition.LastLapCompleted);
        Assert.AreEqual("123.456", deserializedPosition.LastLapTime);
        Assert.AreEqual(3, deserializedPosition.OverallPosition);
        Assert.AreEqual(2, deserializedPosition.ClassPosition);
        Assert.AreEqual(Flags.Yellow, deserializedPosition.TrackFlag);
        Assert.AreEqual(2, deserializedPosition.OverallPositionsGained);
        Assert.AreEqual(true, deserializedPosition.IsOverallMostPositionsGained);
    }

    #endregion

    #region GetLapsAsync Tests

    [TestMethod]
    public async Task GetLapsAsync_NoLapsStored_ReturnsEmptyList()
    {
        // Act
        var laps = await _service.GetLapsAsync("1");

        // Assert
        Assert.IsNotNull(laps);
        Assert.AreEqual(0, laps.Count);
    }

    [TestMethod]
    public async Task GetLapsAsync_OneLapStored_ReturnsOneLap()
    {
        // Arrange
        var position = CreateTestCarPosition("1", 1);
        await _service.AddLapAsync(position);

        // Act
        var laps = await _service.GetLapsAsync("1");

        // Assert
        Assert.IsNotNull(laps);
        Assert.AreEqual(1, laps.Count);
        Assert.AreEqual("1", laps[0].Number);
        Assert.AreEqual(1, laps[0].LastLapCompleted);
    }

    [TestMethod]
    public async Task GetLapsAsync_MultipleLapsStored_ReturnsInReverseChronologicalOrder()
    {
        // Arrange
        for (int i = 1; i <= 5; i++)
        {
            await _service.AddLapAsync(CreateTestCarPosition("42", i, 90.0 + i));
        }

        // Act
        var laps = await _service.GetLapsAsync("42");

        // Assert
        Assert.IsNotNull(laps);
        Assert.AreEqual(5, laps.Count);

        // Most recent first
        Assert.AreEqual(5, laps[0].LastLapCompleted, "Most recent lap should be first");
        Assert.AreEqual(4, laps[1].LastLapCompleted);
        Assert.AreEqual(3, laps[2].LastLapCompleted);
        Assert.AreEqual(2, laps[3].LastLapCompleted);
        Assert.AreEqual(1, laps[4].LastLapCompleted, "Oldest lap should be last");
    }

    [TestMethod]
    public async Task GetLapsAsync_DifferentCar_ReturnsCorrectLaps()
    {
        // Arrange
        await _service.AddLapAsync(CreateTestCarPosition("1", 1));
        await _service.AddLapAsync(CreateTestCarPosition("2", 1));
        await _service.AddLapAsync(CreateTestCarPosition("1", 2));
        await _service.AddLapAsync(CreateTestCarPosition("2", 2));

        // Act
        var car1Laps = await _service.GetLapsAsync("1");
        var car2Laps = await _service.GetLapsAsync("2");

        // Assert
        Assert.AreEqual(2, car1Laps.Count);
        Assert.AreEqual(2, car2Laps.Count);
        Assert.IsTrue(car1Laps.All(l => l.Number == "1"));
        Assert.IsTrue(car2Laps.All(l => l.Number == "2"));
    }

    [TestMethod]
    public async Task GetLapsAsync_NullCarNumber_ThrowsArgumentException()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _service.GetLapsAsync(null!));
        Assert.IsNotNull(exception);
    }

    [TestMethod]
    public async Task GetLapsAsync_EmptyCarNumber_ThrowsArgumentException()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _service.GetLapsAsync(string.Empty));
        Assert.IsNotNull(exception);
    }

    [TestMethod]
    public async Task GetLapsAsync_MaxFiveLaps_ReturnsOnlyFive()
    {
        // Arrange - Add 7 laps
        for (int i = 1; i <= 7; i++)
        {
            await _service.AddLapAsync(CreateTestCarPosition("1", i));
        }

        // Act
        var laps = await _service.GetLapsAsync("1");

        // Assert
        Assert.IsNotNull(laps);
        Assert.AreEqual(5, laps.Count, "Should return maximum of 5 laps");
        Assert.AreEqual(7, laps[0].LastLapCompleted, "Should have most recent 5 laps");
        Assert.AreEqual(3, laps[4].LastLapCompleted, "Oldest lap should be lap 3");
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public async Task AddAndRetrieve_CompleteWorkflow_WorksCorrectly()
    {
        // Arrange
        var carNumber = "77";
        var expectedLaps = new List<CarPosition>();

        // Act - Add 3 laps
        for (int i = 1; i <= 3; i++)
        {
            var position = CreateTestCarPosition(carNumber, i, 85.0 + i);
            expectedLaps.Add(position);
            await _service.AddLapAsync(position);
        }

        var retrievedLaps = await _service.GetLapsAsync(carNumber);

        // Assert
        Assert.AreEqual(3, retrievedLaps.Count);

        // Verify in reverse order (most recent first)
        for (int i = 0; i < 3; i++)
        {
            var expected = expectedLaps[2 - i]; // Reverse order
            var actual = retrievedLaps[i];

            Assert.AreEqual(expected.Number, actual.Number);
            Assert.AreEqual(expected.LastLapCompleted, actual.LastLapCompleted);
            Assert.AreEqual(expected.LastLapTime, actual.LastLapTime);
        }
    }

    [TestMethod]
    public async Task AddAndRetrieve_WithRollingWindow_KeepsOnlyLastFive()
    {
        // Arrange
        var carNumber = "5";

        // Act - Add 10 laps
        for (int i = 1; i <= 10; i++)
        {
            await _service.AddLapAsync(CreateTestCarPosition(carNumber, i, 80.0 + i));
        }

        var retrievedLaps = await _service.GetLapsAsync(carNumber);

        // Assert
        Assert.AreEqual(5, retrievedLaps.Count, "Should only keep last 5 laps");
        Assert.AreEqual(10, retrievedLaps[0].LastLapCompleted, "Should have laps 6-10");
        Assert.AreEqual(9, retrievedLaps[1].LastLapCompleted);
        Assert.AreEqual(8, retrievedLaps[2].LastLapCompleted);
        Assert.AreEqual(7, retrievedLaps[3].LastLapCompleted);
        Assert.AreEqual(6, retrievedLaps[4].LastLapCompleted);
    }

    [TestMethod]
    public async Task GetLapsAsync_AfterAddingLaps_ReturnsCorrectData()
    {
        // Arrange
        var position1 = CreateTestCarPosition("100", 1, 95.123);
        var position2 = CreateTestCarPosition("100", 2, 94.567);

        await _service.AddLapAsync(position1);
        await _service.AddLapAsync(position2);

        // Act
        var laps = await _service.GetLapsAsync("100");

        // Assert
        Assert.AreEqual(2, laps.Count);

        // Most recent first
        Assert.AreEqual(2, laps[0].LastLapCompleted);
        Assert.AreEqual("94.567", laps[0].LastLapTime);

        Assert.AreEqual(1, laps[1].LastLapCompleted);
        Assert.AreEqual("95.123", laps[1].LastLapTime);
    }

    #endregion

    #region Edge Cases

    [TestMethod]
    public async Task AddLapAsync_Lap0_IsStored()
    {
        // Arrange
        var position = CreateTestCarPosition("1", 0);

        // Act
        await _service.AddLapAsync(position);
        var laps = await _service.GetLapsAsync("1");

        // Assert
        Assert.AreEqual(1, laps.Count);
        Assert.AreEqual(0, laps[0].LastLapCompleted);
    }

    [TestMethod]
    public async Task AddLapAsync_SpecialCharactersInCarNumber_HandlesCorrectly()
    {
        // Arrange
        var carNumber = "A-1";
        var position = CreateTestCarPosition(carNumber, 1);

        // Act
        await _service.AddLapAsync(position);
        var laps = await _service.GetLapsAsync(carNumber);

        // Assert
        Assert.AreEqual(1, laps.Count);
        Assert.AreEqual(carNumber, laps[0].Number);
    }

    [TestMethod]
    public async Task GetLapsAsync_NonExistentCar_ReturnsEmptyList()
    {
        // Arrange
        await _service.AddLapAsync(CreateTestCarPosition("1", 1));

        // Act
        var laps = await _service.GetLapsAsync("999");

        // Assert
        Assert.IsNotNull(laps);
        Assert.AreEqual(0, laps.Count);
    }

    [TestMethod]
    public async Task AddLapAsync_SameLapTwice_StoresBoth()
    {
        // Arrange
        var position1 = CreateTestCarPosition("1", 5, 90.0);
        var position2 = CreateTestCarPosition("1", 5, 91.0); // Same lap, different time

        // Act
        await _service.AddLapAsync(position1);
        await _service.AddLapAsync(position2);
        var laps = await _service.GetLapsAsync("1");

        // Assert
        Assert.AreEqual(2, laps.Count, "Both positions should be stored");
        Assert.AreEqual(5, laps[0].LastLapCompleted);
        Assert.AreEqual("91.000", laps[0].LastLapTime, "Most recent entry should be first");
        Assert.AreEqual(5, laps[1].LastLapCompleted);
        Assert.AreEqual("90.000", laps[1].LastLapTime);
    }

    #endregion
}
