using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using RedMist.Backend.Shared;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.LapData;

[TestClass]
public class InMemoryCarLapHistoryServiceTests
{
    private IDbContextFactory<TsContext> _dbContextFactory = null!;
    private SessionContext _sessionContext = null!;
    private FakeTimeProvider _timeProvider = null!;
    private InMemoryCarLapHistoryService _service = null!;
    private const int EventId = 1;
    private const int SessionId = 1;

    [TestInitialize]
    public void Setup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", EventId.ToString() } })
            .Build();

        _dbContextFactory = CreateDbContextFactory();
        _timeProvider = new FakeTimeProvider();
        _sessionContext = new SessionContext(configuration, _dbContextFactory, _timeProvider);
        _sessionContext.SessionState.SessionId = SessionId;

        _service = new InMemoryCarLapHistoryService(_sessionContext);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _service.Clear();
    }

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var databaseName = $"TestDatabase_{Guid.NewGuid()}";
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase(databaseName);
        var options = optionsBuilder.Options;
        return new TestDbContextFactory(options);
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
    public async Task AddLapAsync_ValidPosition_AddsToMemory()
    {
        // Arrange
        var position = CreateTestCarPosition("1", 1);

        // Act
        await _service.AddLapAsync(position);

        // Assert
        var laps = await _service.GetLapsAsync("1");
        Assert.AreEqual(1, laps.Count, "Should have one lap");
        Assert.AreEqual("1", laps[0].Number);
        Assert.AreEqual(1, laps[0].LastLapCompleted);
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
        var laps = await _service.GetLapsAsync(carNumber);
        Assert.AreEqual(5, laps.Count, "Should only keep 5 laps");

        // Verify the most recent 5 laps are kept (laps 3-7)
        Assert.AreEqual(7, laps[0].LastLapCompleted, "Most recent lap should be first");
        Assert.AreEqual(6, laps[1].LastLapCompleted);
        Assert.AreEqual(5, laps[2].LastLapCompleted);
        Assert.AreEqual(4, laps[3].LastLapCompleted);
        Assert.AreEqual(3, laps[4].LastLapCompleted, "Oldest kept lap should be 3");
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
        var laps1 = await _service.GetLapsAsync("1");
        var laps2 = await _service.GetLapsAsync("2");

        Assert.AreEqual(2, laps1.Count, "Car 1 should have 2 laps");
        Assert.AreEqual(2, laps2.Count, "Car 2 should have 2 laps");
    }

    [TestMethod]
    public async Task AddLapAsync_NullPosition_ThrowsArgumentException()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _service.AddLapAsync(null!));
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
            PenalityWarnings = 1,
            BlackFlags = 0,
            IsEnteredPit = true,
            IsPitStartFinish = false,
            IsExitedPit = false,
            IsInPit = false,
            LapIncludedPit = true,
            LastLoopName = "Loop1",
            IsStale = false,
            TrackFlag = Flags.Yellow,
            LocalFlag = Flags.Green,
            CompletedSections = [],
            ProjectedLapTimeMs = 95000,
            LapStartTime = new TimeOnly(14, 30, 0),
            DriverName = "Test Driver 99",
            DriverId = "DRV99",
            CurrentStatus = "Running",
            ImpactWarning = true,
            IsBestTime = false,
            IsBestTimeClass = true,
            IsOverallMostPositionsGained = false,
            IsClassMostPositionsGained = true,
            LastLapTime = "94.523",
            TotalTime = "01:23:45.678"
        };

        // Act
        await _service.AddLapAsync(position);

        // Assert
        var laps = await _service.GetLapsAsync("99");
        Assert.AreEqual(1, laps.Count);
        var retrieved = laps[0];

        Assert.AreEqual("99", retrieved.Number);
        Assert.AreEqual("GT3", retrieved.Class);
        Assert.AreEqual(3, retrieved.OverallPosition);
        Assert.AreEqual((uint)12345, retrieved.TransponderId);
        Assert.AreEqual("1", retrieved.EventId);
        Assert.AreEqual("1", retrieved.SessionId);
        Assert.AreEqual(5, retrieved.LastLapCompleted);
        Assert.AreEqual(2, retrieved.OverallPositionsGained);
        Assert.AreEqual(1, retrieved.InClassPositionsGained);
        Assert.AreEqual(true, retrieved.IsEnteredPit);
        Assert.AreEqual(true, retrieved.LapIncludedPit);
        Assert.AreEqual("Test Driver 99", retrieved.DriverName);
        Assert.AreEqual("94.523", retrieved.LastLapTime);
    }

    #endregion

    #region GetLapsAsync Tests

    [TestMethod]
    public async Task GetLapsAsync_NoLaps_ReturnsEmptyList()
    {
        // Act
        var laps = await _service.GetLapsAsync("99");

        // Assert
        Assert.IsNotNull(laps);
        Assert.AreEqual(0, laps.Count);
    }

    [TestMethod]
    public async Task GetLapsAsync_ValidCarNumber_ReturnsLapsInReverseChronologicalOrder()
    {
        // Arrange
        await _service.AddLapAsync(CreateTestCarPosition("5", 1, 91.0));
        await _service.AddLapAsync(CreateTestCarPosition("5", 2, 92.0));
        await _service.AddLapAsync(CreateTestCarPosition("5", 3, 93.0));

        // Act
        var laps = await _service.GetLapsAsync("5");

        // Assert
        Assert.AreEqual(3, laps.Count);
        Assert.AreEqual(3, laps[0].LastLapCompleted, "Most recent lap should be first");
        Assert.AreEqual(2, laps[1].LastLapCompleted);
        Assert.AreEqual(1, laps[2].LastLapCompleted, "Oldest lap should be last");
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
    public async Task GetLapsAsync_ReturnsCopy_ModificationDoesNotAffectStorage()
    {
        // Arrange
        await _service.AddLapAsync(CreateTestCarPosition("1", 1));

        // Act - Get laps and modify
        var laps1 = await _service.GetLapsAsync("1");
        laps1[0].Number = "999";  // Modify the returned list

        // Assert - Get laps again and verify original data is intact
        var laps2 = await _service.GetLapsAsync("1");
        Assert.AreEqual("1", laps2[0].Number, "Original data should not be modified");
    }

    #endregion

    #region Clear Tests

    [TestMethod]
    public async Task Clear_RemovesAllData()
    {
        // Arrange
        await _service.AddLapAsync(CreateTestCarPosition("1", 1));
        await _service.AddLapAsync(CreateTestCarPosition("2", 1));
        await _service.AddLapAsync(CreateTestCarPosition("3", 1));

        // Act
        _service.Clear();

        // Assert
        var laps1 = await _service.GetLapsAsync("1");
        var laps2 = await _service.GetLapsAsync("2");
        var laps3 = await _service.GetLapsAsync("3");

        Assert.AreEqual(0, laps1.Count);
        Assert.AreEqual(0, laps2.Count);
        Assert.AreEqual(0, laps3.Count);
    }

    #endregion
}
