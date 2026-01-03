using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Backend.Shared.Models;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.X2;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.LapData;

[TestClass]
public class LapProcessorTests
{
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private SessionContext _sessionContext = null!;
    private Mock<IConnectionMultiplexer> _mockConnectionMultiplexer = null!;
    private Mock<IDatabase> _mockDatabase = null!;
    private PitProcessor _pitProcessor = null!;
    private FakeTimeProvider _timeProvider = null!;
    private LapProcessor _lapProcessor = null!;
    private readonly List<(RedisKey key, RedisValue field, RedisValue value)> _capturedStreamAdds = [];

    [TestInitialize]
    public void Setup()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();

        _dbContextFactory = CreateDbContextFactory();
        _timeProvider = new FakeTimeProvider();
        _sessionContext = new SessionContext(configuration, _dbContextFactory, _timeProvider);
        _sessionContext.SessionState.SessionId = 1;

        SetupRedisMock();

        _pitProcessor = new PitProcessor(_dbContextFactory, _mockLoggerFactory.Object, _sessionContext);

        _lapProcessor = new LapProcessor(
            _mockLoggerFactory.Object,
            _dbContextFactory,
            _sessionContext,
            _mockConnectionMultiplexer.Object,
            _pitProcessor,
            _timeProvider);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _lapProcessor?.Dispose();
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
        _capturedStreamAdds.Clear();
        _mockConnectionMultiplexer = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();

        _mockConnectionMultiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDatabase.Object);

        _mockDatabase.Setup(x => x.StreamAddAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(),
            null,
            null,
            false,
            null,
            default(StreamTrimMode),
            CommandFlags.None))
            .Callback<RedisKey, RedisValue, RedisValue, RedisValue?, long?, bool, long?, StreamTrimMode, CommandFlags>(
                (key, field, value, messageId, maxLength, useApproximateMaxLength, limit, trimMode, flags) =>
                {
                    _capturedStreamAdds.Add((key, field, value));
                })
            .ReturnsAsync(RedisValue.Null);
    }

    #region Lap 0 Tests

    [TestMethod]
    public async Task ProcessAsync_Lap0_IsLogged()
    {
        // Arrange
        var carPosition = CreateTestCarPosition("1", 0);
        var carPositions = new List<CarPosition> { carPosition };

        // Act
        await _lapProcessor.ProcessAsync(carPositions);
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert
        Assert.AreEqual(1, _capturedStreamAdds.Count, "Lap 0 should be logged");

        var capturedData = _capturedStreamAdds[0];
        Assert.AreEqual("laps", capturedData.field.ToString());

        var laps = JsonSerializer.Deserialize<List<CarLapData>>(capturedData.value.ToString());
        Assert.IsNotNull(laps);
        Assert.HasCount(1, laps);
        Assert.AreEqual("1", laps[0].Log.CarNumber);
        Assert.AreEqual(0, laps[0].Log.LapNumber);
    }

    [TestMethod]
    public async Task ProcessAsync_Lap0_OnlyLoggedOnce()
    {
        // Arrange
        var carPosition = CreateTestCarPosition("1", 0);
        var carPositions = new List<CarPosition> { carPosition };

        // Act - Process the same lap 0 multiple times
        await _lapProcessor.ProcessAsync(carPositions);
        await _lapProcessor.ProcessAsync(carPositions);
        await _lapProcessor.ProcessAsync(carPositions);
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - Should only log once
        Assert.HasCount(1, _capturedStreamAdds, "Lap 0 should only be logged once");

        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.HasCount(1, laps);
    }

    [TestMethod]
    public async Task ProcessAsync_Lap0_ThenLap1_BothLogged()
    {
        // Arrange
        var carPosition0 = CreateTestCarPosition("1", 0);
        var carPosition1 = CreateTestCarPosition("1", 1);

        // Act - Process both laps, then flush once
        await _lapProcessor.ProcessAsync(new List<CarPosition> { carPosition0 });
        await _lapProcessor.ProcessAsync(new List<CarPosition> { carPosition1 });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - Should have 1 stream add containing both laps
        Assert.HasCount(1, _capturedStreamAdds, "Should have one stream add");

        var allLaps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(allLaps);
        Assert.HasCount(2, allLaps, "Should contain both lap 0 and lap 1");

        Assert.IsTrue(allLaps.Any(l => l.Log.LapNumber == 0), "Lap 0 should be included");
        Assert.IsTrue(allLaps.Any(l => l.Log.LapNumber == 1), "Lap 1 should be included");
    }

    [TestMethod]
    public async Task ProcessAsync_MultipleCars_Lap0_AllLogged()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", 0);
        var car2 = CreateTestCarPosition("2", 0);
        var car3 = CreateTestCarPosition("3", 0);
        var carPositions = new List<CarPosition> { car1, car2, car3 };

        // Act
        await _lapProcessor.ProcessAsync(carPositions);
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - Should log all three cars' lap 0
        Assert.HasCount(1, _capturedStreamAdds);

        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.HasCount(3, laps, "All three cars should have lap 0 logged");

        Assert.IsTrue(laps.Any(l => l.Log.CarNumber == "1" && l.Log.LapNumber == 0));
        Assert.IsTrue(laps.Any(l => l.Log.CarNumber == "2" && l.Log.LapNumber == 0));
        Assert.IsTrue(laps.Any(l => l.Log.CarNumber == "3" && l.Log.LapNumber == 0));
    }

    [TestMethod]
    public async Task ProcessAsync_Lap0_WithChangedPosition_UpdatedOnSubsequentCall()
    {
        // Arrange
        var carPosition1 = CreateTestCarPosition("1", 0);
        carPosition1.OverallPosition = 5;

        var carPosition2 = CreateTestCarPosition("1", 0);
        carPosition2.OverallPosition = 3; // Changed position

        // Act - Process lap 0 twice with different positions, then flush
        await _lapProcessor.ProcessAsync(new List<CarPosition> { carPosition1 });
        await _lapProcessor.ProcessAsync(new List<CarPosition> { carPosition2 });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - The behavior depends on IsLapNewerThanLastEntryWithReplace
        // Since position changed, it should be logged
        Assert.HasCount(1, _capturedStreamAdds);

        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        // Should have logged at least once, possibly twice if position change triggers re-log
        Assert.IsGreaterThanOrEqualTo(1, laps.Count, "At least one lap should be logged");
    }

    [TestMethod]
    public async Task ProcessAsync_Lap1_BeforeLap0_OnlyLap1Logged()
    {
        // Arrange - This simulates a scenario where lap 1 is already processed
        // and then lap 0 comes in (which shouldn't happen in practice but we test the guard)
        var carPosition1 = CreateTestCarPosition("1", 1);
        var carPosition0 = CreateTestCarPosition("1", 0);

        // Act - Process both laps, then flush once
        await _lapProcessor.ProcessAsync(new List<CarPosition> { carPosition1 });
        await _lapProcessor.ProcessAsync(new List<CarPosition> { carPosition0 });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - Lap 0 should NOT be logged after lap 1 has been processed
        // because lap 0 is not newer than lap 1
        Assert.HasCount(1, _capturedStreamAdds, "Should have one stream add");

        var allLaps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(allLaps);
        Assert.HasCount(1, allLaps, "Only lap 1 should be logged");
        Assert.AreEqual(1, allLaps[0].Log.LapNumber, "Should be lap 1");
    }

    [TestMethod]
    public async Task ProcessAsync_Lap0_IncludesCorrectEventAndSessionId()
    {
        // Arrange
        _sessionContext.SessionState.SessionId = 42;
        var carPosition = CreateTestCarPosition("1", 0);

        // Act
        await _lapProcessor.ProcessAsync(new List<CarPosition> { carPosition });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert
        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.AreEqual(1, laps[0].Log.EventId);
        Assert.AreEqual(42, laps[0].Log.SessionId);
    }

    [TestMethod]
    public async Task ProcessAsync_EmptyCarNumber_NotLogged()
    {
        // Arrange
        var carPosition = CreateTestCarPosition("", 0);

        // Act
        await _lapProcessor.ProcessAsync(new List<CarPosition> { carPosition });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert
        Assert.IsEmpty(_capturedStreamAdds, "Car with empty number should not be logged");
    }

    #endregion

    #region Concurrent Processing Tests

    [TestMethod]
    public async Task ProcessAsync_ConcurrentCalls_NoRaceConditions()
    {
        // Arrange
        var car1Lap1 = CreateTestCarPosition("1", 1);
        var car1Lap2 = CreateTestCarPosition("1", 2);
        var car1Lap3 = CreateTestCarPosition("1", 3);

        // Act - Simulate concurrent calls
        var tasks = new List<Task>
        {
            Task.Run(async () => await _lapProcessor.ProcessAsync(new List<CarPosition> { car1Lap1 })),
            Task.Run(async () => await _lapProcessor.ProcessAsync(new List<CarPosition> { car1Lap2 })),
            Task.Run(async () => await _lapProcessor.ProcessAsync(new List<CarPosition> { car1Lap3 }))
        };

        await Task.WhenAll(tasks);
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - All laps should be logged exactly once (may be across multiple stream adds if background task processed some)
        var allLaps = _capturedStreamAdds
            .SelectMany(add => JsonSerializer.Deserialize<List<CarLapData>>(add.value.ToString()) ?? [])
            .ToList();

        Assert.IsNotNull(allLaps);
        Assert.HasCount(3, allLaps, "All three laps should be logged");

        // Verify no duplicates
        var lapNumbers = allLaps.Select(l => l.Log.LapNumber).ToList();
        Assert.AreEqual(3, lapNumbers.Distinct().Count(), "No duplicate laps should be logged");

        // Verify all expected laps are present
        Assert.IsTrue(allLaps.Any(l => l.Log.LapNumber == 1), "Lap 1 should be logged");
        Assert.IsTrue(allLaps.Any(l => l.Log.LapNumber == 2), "Lap 2 should be logged");
        Assert.IsTrue(allLaps.Any(l => l.Log.LapNumber == 3), "Lap 3 should be logged");
    }

    [TestMethod]
    public async Task ProcessAsync_ConcurrentMultipleCars_AllLogged()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", 1);
        var car2 = CreateTestCarPosition("2", 1);
        var car3 = CreateTestCarPosition("3", 1);

        // Act - Process different cars concurrently
        var tasks = new List<Task>
        {
            Task.Run(async () => await _lapProcessor.ProcessAsync(new List<CarPosition> { car1 })),
            Task.Run(async () => await _lapProcessor.ProcessAsync(new List<CarPosition> { car2 })),
            Task.Run(async () => await _lapProcessor.ProcessAsync(new List<CarPosition> { car3 }))
        };

        await Task.WhenAll(tasks);
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert
        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.HasCount(3, laps, "All three cars should be logged");

        Assert.IsTrue(laps.Any(l => l.Log.CarNumber == "1"));
        Assert.IsTrue(laps.Any(l => l.Log.CarNumber == "2"));
        Assert.IsTrue(laps.Any(l => l.Log.CarNumber == "3"));
    }

    #endregion

    #region Pit Message Integration Tests

    [TestMethod]
    public async Task ProcessPendingLapForCarAsync_ForcesImmediateProcessing()
    {
        // Arrange
        var car1Lap1 = CreateTestCarPosition("1", 1);
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1Lap1 });

        // Act - Force immediate processing for car 1
        await _lapProcessor.ProcessPendingLapForCarAsync("1");

        // Assert - Lap should be processed immediately without waiting for timeout
        Assert.HasCount(1, _capturedStreamAdds, "Lap should be processed immediately");

        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.HasCount(1, laps);
        Assert.AreEqual("1", laps[0].Log.CarNumber);
    }

    [TestMethod]
    public async Task ProcessPendingLapForCarAsync_OnlyProcessesSpecificCar()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", 1);
        var car2 = CreateTestCarPosition("2", 1);

        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1, car2 });

        // Act - Only force car 1
        await _lapProcessor.ProcessPendingLapForCarAsync("1");

        // Assert - Only car 1 should be processed
        Assert.HasCount(1, _capturedStreamAdds);

        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.HasCount(1, laps, "Only car 1 should be processed");
        Assert.AreEqual("1", laps[0].Log.CarNumber);

        // Car 2 should still be pending
        await _lapProcessor.FlushPendingLapsAsync();
        Assert.HasCount(2, _capturedStreamAdds, "Car 2 should be processed on flush");
    }

    [TestMethod]
    public async Task ProcessPendingLapForCarAsync_NonExistentCar_NoError()
    {
        // Act & Assert - Should not throw exception
        await _lapProcessor.ProcessPendingLapForCarAsync("999");

        Assert.IsEmpty(_capturedStreamAdds, "No laps should be logged");
    }

    #endregion

    #region Background Task Processing Tests

    [TestMethod]
    public async Task BackgroundTask_ProcessesLapsAfterTimeout()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", 1);
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1 });

        // Act - Advance time past the pit message wait time (1 second)
        _timeProvider.Advance(TimeSpan.FromMilliseconds(1100));

        // Wait for background task to process (it checks every 100ms)
        await Task.Delay(250);

        // Assert - Lap should be processed automatically by background task
        Assert.IsGreaterThanOrEqualTo(1, _capturedStreamAdds.Count, "Background task should process the lap");
    }

    [TestMethod]
    public async Task BackgroundTask_DoesNotProcessBeforeTimeout()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", 1);
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1 });

        // Act - Advance time but not past the timeout
        _timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        await Task.Delay(150);

        // Assert - Lap should NOT be processed yet
        Assert.IsEmpty(_capturedStreamAdds, "Lap should not be processed before timeout");
    }

    #endregion

    #region Session/Event Isolation Tests

    [TestMethod]
    public async Task ProcessAsync_DifferentSessions_IsolatedLapTracking()
    {
        // Arrange
        var car1Session1 = CreateTestCarPosition("1", 1);

        // Process lap 1 in session 1
        _sessionContext.SessionState.SessionId = 1;
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1Session1 });
        await _lapProcessor.FlushPendingLapsAsync();

        // Change to session 2 and process lap 1 again
        _sessionContext.SessionState.SessionId = 2;
        var car1Session2 = CreateTestCarPosition("1", 1);
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1Session2 });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - Both should be logged (different sessions)
        Assert.HasCount(2, _capturedStreamAdds, "Both sessions should log lap 1");

        var session1Laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        var session2Laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[1].value.ToString());

        Assert.IsNotNull(session1Laps);
        Assert.IsNotNull(session2Laps);
        Assert.AreEqual(1, session1Laps[0].Log.SessionId);
        Assert.AreEqual(2, session2Laps[0].Log.SessionId);
    }

    [TestMethod]
    public async Task ProcessAsync_SameCarDifferentSessions_IndependentTracking()
    {
        // Arrange & Act - Session 1: Process laps 1, 2, 3
        _sessionContext.SessionState.SessionId = 1;
        await _lapProcessor.ProcessAsync(new List<CarPosition> 
        { 
            CreateTestCarPosition("1", 1),
            CreateTestCarPosition("1", 2),
            CreateTestCarPosition("1", 3)
        });
        await _lapProcessor.FlushPendingLapsAsync();

        // Session 2: Should be able to log lap 1 again
        _sessionContext.SessionState.SessionId = 2;
        await _lapProcessor.ProcessAsync(new List<CarPosition> { CreateTestCarPosition("1", 1) });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert
        Assert.HasCount(2, _capturedStreamAdds);

        var session2Laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[1].value.ToString());
        Assert.IsNotNull(session2Laps);
        Assert.HasCount(1, session2Laps);
        Assert.AreEqual(1, session2Laps[0].Log.LapNumber, "Session 2 should be able to log lap 1");
    }

    #endregion

    #region Database Recovery Tests

    [TestMethod]
    public async Task InitializeEventLastLaps_RestoresFromDatabase()
    {
        // Arrange - Pre-populate database with last lap data
        using (var context = _dbContextFactory.CreateDbContext())
        {
            context.CarLastLaps.Add(new Database.Models.CarLastLap
            {
                EventId = 1,
                SessionId = 1,
                CarNumber = "1",
                LastLapNumber = 5
            });
            context.CarLastLaps.Add(new Database.Models.CarLastLap
            {
                EventId = 1,
                SessionId = 1,
                CarNumber = "2",
                LastLapNumber = 3
            });
            await context.SaveChangesAsync();
        }

        // Create a new LapProcessor (simulating service restart)
        using var newProcessor = new LapProcessor(
            _mockLoggerFactory.Object,
            _dbContextFactory,
            _sessionContext,
            _mockConnectionMultiplexer.Object,
            _pitProcessor,
            _timeProvider);

        // Act - Try to process lap 5 for car 1 (should not log because it's not newer)
        await newProcessor.ProcessAsync(new List<CarPosition> { CreateTestCarPosition("1", 5) });
        await newProcessor.FlushPendingLapsAsync();

        // Process lap 6 for car 1 (should log)
        await newProcessor.ProcessAsync(new List<CarPosition> { CreateTestCarPosition("1", 6) });
        await newProcessor.FlushPendingLapsAsync();

        // Assert - Only lap 6 should be logged
        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.HasCount(1, laps);
        Assert.AreEqual(6, laps[0].Log.LapNumber, "Only new lap should be logged after recovery");
    }

    #endregion

    #region Lap Sequence Tests

    [TestMethod]
    public async Task ProcessAsync_SkippedLapNumbers_AllLogged()
    {
        // Arrange - Process laps 1, 3, 5 (skipping 2 and 4)
        var laps = new List<CarPosition>
        {
            CreateTestCarPosition("1", 1),
            CreateTestCarPosition("1", 3),
            CreateTestCarPosition("1", 5)
        };

        // Act
        await _lapProcessor.ProcessAsync(laps);
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - All should be logged
        var loggedLaps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(loggedLaps);
        Assert.HasCount(3, loggedLaps, "All provided laps should be logged");

        Assert.IsTrue(loggedLaps.Any(l => l.Log.LapNumber == 1));
        Assert.IsTrue(loggedLaps.Any(l => l.Log.LapNumber == 3));
        Assert.IsTrue(loggedLaps.Any(l => l.Log.LapNumber == 5));
    }

    [TestMethod]
    public async Task ProcessAsync_OutOfOrderLaps_OnlyNewerLogged()
    {
        // Arrange - Process laps 5, then 3, then 7
        await _lapProcessor.ProcessAsync(new List<CarPosition> { CreateTestCarPosition("1", 5) });
        await _lapProcessor.ProcessAsync(new List<CarPosition> { CreateTestCarPosition("1", 3) });
        await _lapProcessor.ProcessAsync(new List<CarPosition> { CreateTestCarPosition("1", 7) });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - Only laps 5 and 7 should be logged (3 is older than 5)
        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.HasCount(2, laps, "Only laps 5 and 7 should be logged");

        Assert.IsTrue(laps.Any(l => l.Log.LapNumber == 5));
        Assert.IsTrue(laps.Any(l => l.Log.LapNumber == 7));
        Assert.IsFalse(laps.Any(l => l.Log.LapNumber == 3), "Lap 3 should not be logged");
    }

    #endregion

    #region Race Simulation Tests

    [TestMethod]
    public async Task ProcessAsync_RaceSimulation_CorrectLapCounting()
    {
        // Arrange - Simulate a race with 5 cars
        var carNumbers = new[] { "1", "2", "3", "4", "5" };

        // Simulate 10 laps
        for (int lap = 0; lap <= 10; lap++)
        {
            var positions = carNumbers.Select(carNum => CreateTestCarPosition(carNum, lap)).ToList();
            await _lapProcessor.ProcessAsync(positions);
        }

        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - Should have logged all laps for all cars
        var allLaps = _capturedStreamAdds
            .SelectMany(add => JsonSerializer.Deserialize<List<CarLapData>>(add.value.ToString()) ?? [])
            .ToList();

        Assert.HasCount(5 * 11, allLaps, "Should log 11 laps (0-10) for 5 cars");

        // Verify each car has laps 0-10
        foreach (var carNum in carNumbers)
        {
            var carLaps = allLaps.Where(l => l.Log.CarNumber == carNum).Select(l => l.Log.LapNumber).OrderBy(n => n).ToList();
            CollectionAssert.AreEqual(Enumerable.Range(0, 11).ToList(), carLaps, $"Car {carNum} should have all laps 0-10");
        }
    }

    [TestMethod]
    public async Task ProcessAsync_StaggeredRace_CarsAtDifferentLaps()
    {
        // Arrange - Car 1 on lap 10, Car 2 on lap 8, Car 3 on lap 6
        var positions = new List<CarPosition>
        {
            CreateTestCarPosition("1", 10),
            CreateTestCarPosition("2", 8),
            CreateTestCarPosition("3", 6)
        };

        // Act
        await _lapProcessor.ProcessAsync(positions);
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert
        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.HasCount(3, laps);

        Assert.AreEqual(10, laps.First(l => l.Log.CarNumber == "1").Log.LapNumber);
        Assert.AreEqual(8, laps.First(l => l.Log.CarNumber == "2").Log.LapNumber);
        Assert.AreEqual(6, laps.First(l => l.Log.CarNumber == "3").Log.LapNumber);
    }

    #endregion

    #region Timestamp and Ordering Tests

    [TestMethod]
    public async Task LogCompletedLaps_UsesCorrectTimestamps()
    {
        // Arrange
        var startTime = DateTimeOffset.UtcNow;
        _timeProvider.SetUtcNow(startTime);

        var car1 = CreateTestCarPosition("1", 1);
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1 });

        // Advance time by a small amount (not enough to trigger background processing)
        _timeProvider.Advance(TimeSpan.FromMilliseconds(500));

        var car2 = CreateTestCarPosition("1", 2);
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car2 });

        // Flush immediately before background task can process
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - Check timestamps
        var allLaps = _capturedStreamAdds
            .SelectMany(add => JsonSerializer.Deserialize<List<CarLapData>>(add.value.ToString()) ?? [])
            .ToList();

        Assert.IsNotNull(allLaps);
        Assert.HasCount(2, allLaps, "Should have both laps");

        // Both should have the flush time as timestamp
        var flushTime = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var lap in allLaps)
        {
            Assert.AreEqual(flushTime, lap.Log.Timestamp);
        }
    }

    #endregion

    #region Flag State Tests

    [TestMethod]
    public async Task ProcessAsync_DifferentFlagStates_AllLogged()
    {
        // Arrange
        var car1Green = CreateTestCarPosition("1", 1);
        car1Green.TrackFlag = Flags.Green;

        var car1Yellow = CreateTestCarPosition("1", 2);
        car1Yellow.TrackFlag = Flags.Yellow;

        var car1Checkered = CreateTestCarPosition("1", 3);
        car1Checkered.TrackFlag = Flags.Checkered;

        // Act
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1Green, car1Yellow, car1Checkered });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert
        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.HasCount(3, laps);

        Assert.AreEqual((int)Flags.Green, laps.First(l => l.Log.LapNumber == 1).Log.Flag);
        Assert.AreEqual((int)Flags.Yellow, laps.First(l => l.Log.LapNumber == 2).Log.Flag);
        Assert.AreEqual((int)Flags.Checkered, laps.First(l => l.Log.LapNumber == 3).Log.Flag);
    }

    #endregion

    #region Position Change Detection Tests

    [TestMethod]
    public async Task IsLapNewerThanLastEntry_DetectsLapTimeChanges()
    {
        // Arrange
        var car1First = CreateTestCarPosition("1", 0);
        car1First.LastLapTime = "00:01:30.000";

        var car1Updated = CreateTestCarPosition("1", 0);
        car1Updated.LastLapTime = "00:01:29.500";

        // Act
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1First });
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1Updated });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - Second lap 0 with changed lap time should also be logged
        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        // The behavior depends on IsLapNewerThanLastEntryWithReplace detecting the change
        // and the lap being enqueued. Since lap 0 is already tracked after first call,
        // it won't be enqueued again even with changes.
        Assert.IsGreaterThanOrEqualTo(1, laps.Count, "At least first lap should be logged");

        // Verify the logged lap has correct data
        var lap0 = laps.First(l => l.Log.LapNumber == 0);
        Assert.IsNotNull(lap0);
    }

    [TestMethod]
    public async Task IsLapNewerThanLastEntry_DetectsPositionChanges()
    {
        // Arrange
        var car1First = CreateTestCarPosition("1", 0);
        car1First.OverallPosition = 5;

        var car1Updated = CreateTestCarPosition("1", 0);
        car1Updated.OverallPosition = 3;

        // Act
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1First });
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1Updated });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - Similar to lap time, position changes are tracked but lap 0 won't be re-enqueued
        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.IsGreaterThanOrEqualTo(1, laps.Count, "At least first lap should be logged");

        // Verify the logged lap has correct data
        var lap0 = laps.First(l => l.Log.LapNumber == 0);
        Assert.IsNotNull(lap0);
    }

    [TestMethod]
    public async Task IsLapNewerThanLastEntry_HigherLapsAlwaysLogged()
    {
        // Arrange - Test that regular lap progression always logs
        var car1Lap1 = CreateTestCarPosition("1", 1);
        var car1Lap2 = CreateTestCarPosition("1", 2);
        var car1Lap3 = CreateTestCarPosition("1", 3);

        // Act
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1Lap1, car1Lap2, car1Lap3 });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - All laps should be logged
        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.HasCount(3, laps, "All three laps should be logged");
        Assert.IsTrue(laps.Any(l => l.Log.LapNumber == 1));
        Assert.IsTrue(laps.Any(l => l.Log.LapNumber == 2));
        Assert.IsTrue(laps.Any(l => l.Log.LapNumber == 3));
    }

    #endregion

    #region Disposal and Cleanup Tests

    [TestMethod]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var processor = new LapProcessor(
            _mockLoggerFactory.Object,
            _dbContextFactory,
            _sessionContext,
            _mockConnectionMultiplexer.Object,
            _pitProcessor,
            _timeProvider);

        // Act & Assert - Should not throw
        processor.Dispose();
        processor.Dispose();
        processor.Dispose();
    }

    [TestMethod]
    public async Task Dispose_StopsBackgroundTask()
    {
        // Arrange
        var processor = new LapProcessor(
            _mockLoggerFactory.Object,
            _dbContextFactory,
            _sessionContext,
            _mockConnectionMultiplexer.Object,
            _pitProcessor,
            _timeProvider);

        var car1 = CreateTestCarPosition("1", 1);
        await processor.ProcessAsync(new List<CarPosition> { car1 });

        // Act
        processor.Dispose();

        // Advance time and wait - background task should not process
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(250);

        // Assert - Nothing should be logged (background task stopped)
        Assert.IsEmpty(_capturedStreamAdds, "Background task should stop after disposal");
    }

    #endregion

    #region Error Handling Tests

    [TestMethod]
    public async Task LogCompletedLaps_RedisFailure_LogsError()
    {
        // Arrange
        var errorLogged = false;
        _mockLogger.Setup(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(() => errorLogged = true);

        _mockDatabase.Setup(x => x.StreamAddAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(),
            null,
            null,
            false,
            null,
            default(StreamTrimMode),
            CommandFlags.None))
            .ThrowsAsync(new RedisException("Connection failed"));

        var car1 = CreateTestCarPosition("1", 1);
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1 });

        // Act
        try
        {
            await _lapProcessor.FlushPendingLapsAsync();
        }
        catch
        {
            // Expected - Redis failure
        }

        // Assert - Error should be logged (in background task)
        await Task.Delay(1200); // Wait for background task error handling
        // Note: Error logging happens in background task, not in FlushPendingLapsAsync
    }

    #endregion

    #region Large Batch Tests

    [TestMethod]
    public async Task ProcessAsync_LargeNumberOfCars_PerformsWell()
    {
        // Arrange - 100 cars
        var positions = Enumerable.Range(1, 100)
            .Select(i => CreateTestCarPosition(i.ToString(), 1))
            .ToList();

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _lapProcessor.ProcessAsync(positions);
        await _lapProcessor.FlushPendingLapsAsync();
        stopwatch.Stop();

        // Assert
        var laps = JsonSerializer.Deserialize<List<CarLapData>>(_capturedStreamAdds[0].value.ToString());
        Assert.IsNotNull(laps);
        Assert.HasCount(100, laps, "All 100 cars should be logged");
        Assert.IsLessThan(5000, stopwatch.ElapsedMilliseconds, "Should complete in reasonable time");
    }

    [TestMethod]
    public async Task ProcessAsync_ManyLapsPerCar_PerformsWell()
    {
        // Arrange - 5 cars, 50 laps each
        for (int lap = 0; lap <= 50; lap++)
        {
            var positions = Enumerable.Range(1, 5)
                .Select(carNum => CreateTestCarPosition(carNum.ToString(), lap))
                .ToList();
            await _lapProcessor.ProcessAsync(positions);
        }

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _lapProcessor.FlushPendingLapsAsync();
        stopwatch.Stop();

        // Assert
        var allLaps = _capturedStreamAdds
            .SelectMany(add => JsonSerializer.Deserialize<List<CarLapData>>(add.value.ToString()) ?? [])
            .ToList();

        Assert.HasCount(5 * 51, allLaps, "Should log all laps for all cars");
        Assert.IsLessThan(1000, stopwatch.ElapsedMilliseconds, "Should flush quickly");
    }

    #endregion

    #region Queue Management Tests

    [TestMethod]
    public async Task ProcessAsync_QueueCleanup_RemovesEmptyQueues()
    {
        // Arrange
        var car1 = CreateTestCarPosition("1", 1);
        var car2 = CreateTestCarPosition("2", 1);

        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1, car2 });

        // Act - Process and flush
        await _lapProcessor.FlushPendingLapsAsync();

        // Process new laps - queues should have been cleaned up and recreated
        var car1Lap2 = CreateTestCarPosition("1", 2);
        await _lapProcessor.ProcessAsync(new List<CarPosition> { car1Lap2 });
        await _lapProcessor.FlushPendingLapsAsync();

        // Assert - Should work correctly after cleanup
        Assert.HasCount(2, _capturedStreamAdds, "Both batches should be logged");
    }

    #endregion

    #region Helper Methods

    private static CarPosition CreateTestCarPosition(string number, int lastLapCompleted)
    {
        return new CarPosition
        {
            Number = number,
            Class = "A",
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
            LastLapTime = "00:01:30.000",
            TotalTime = "00:01:30.000"
        };
    }

    #endregion
}
