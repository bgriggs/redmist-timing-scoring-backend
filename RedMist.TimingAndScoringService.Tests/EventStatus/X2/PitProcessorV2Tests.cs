using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.EventStatus.X2;
using RedMist.TimingAndScoringService.EventStatus.X2.StateChanges;
using RedMist.TimingAndScoringService.Models;
using RedMist.TimingCommon.Models;
using RedMist.TimingCommon.Models.Configuration;
using RedMist.TimingCommon.Models.X2;
using System.Collections.Immutable;
using System.Text.Json;
using ConfigurationEvent = RedMist.TimingCommon.Models.Configuration.Event;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.X2;

[TestClass]
public class PitProcessorV2Tests
{
    private PitProcessorV2 _processor = null!;
    private Mock<IDbContextFactory<TsContext>> _mockDbContextFactory = null!;
    private Mock<ILoggerFactory> _mockLoggerFactory = null!;
    private Mock<ILogger> _mockLogger = null!;
    private Mock<SessionContext> _mockSessionContext = null!;

    [TestInitialize]
    public void Setup()
    {
        _mockDbContextFactory = new Mock<IDbContextFactory<TsContext>>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger>();
        _mockSessionContext = new Mock<SessionContext>();

        // Create a real TsContext with InMemory database instead of mocking it
        var databaseName = $"TestDatabase_{Guid.NewGuid()}";
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase(databaseName);
        var options = optionsBuilder.Options;

        // Use real TsContext instead of mock since we need to access non-virtual properties
        var realDbContext = new TsContext(options);
        
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);
        _mockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(realDbContext);

        _processor = new PitProcessorV2(_mockDbContextFactory.Object, _mockLoggerFactory.Object, _mockSessionContext.Object);
    }

    #region Initialization Tests

    [TestMethod]
    public async Task Initialize_ValidEventId_LoadsEventConfiguration()
    {
        // Arrange
        var eventId = 123;
        var eventConfig = new ConfigurationEvent
        {
            Id = eventId,
            LoopsMetadata = new List<LoopMetadata>
            {
                new LoopMetadata { Id = 1, Type = LoopType.PitIn, Name = "Pit Entry" },
                new LoopMetadata { Id = 2, Type = LoopType.PitExit, Name = "Pit Exit" }
            }
        };

        // Setup a real database context with the event configuration
        _mockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var databaseName = $"TestDatabase_{Guid.NewGuid()}";
                var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
                optionsBuilder.UseInMemoryDatabase(databaseName);
                var options = optionsBuilder.Options;
                
                var context = new TsContext(options);
                context.Events.Add(eventConfig);
                await context.SaveChangesAsync();
                return context;
            });

        // Act
        await _processor.Initialize(eventId);

        // Assert
        _mockDbContextFactory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [TestMethod]
    public async Task Initialize_DatabaseException_LogsErrorAndContinues()
    {
        // Arrange
        var eventId = 123;
        _mockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act
        await _processor.Initialize(eventId);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error refreshing event loop metadata")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region Process Tests - Basic Functionality

    [TestMethod]
    public async Task Process_X2PassMessage_ReturnsNull()
    {
        // Arrange
        var message = new TimingMessage("x2pass", "data", 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task Process_MultiloopActive_ReturnsNull()
    {
        // Arrange
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(true);
        var message = new TimingMessage("x2pass", "data", 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task Process_EmptyPassingsData_ReturnsNull()
    {
        // Arrange
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);
        var message = new TimingMessage("x2pass", "[]", 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task Process_NullPassingsData_ReturnsNull()
    {
        // Arrange
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);
        var message = new TimingMessage("x2pass", "null", 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNull(result);
    }

    #endregion

    #region Process Tests - Pit Processing

    [TestMethod]
    public async Task Process_ValidPassings_ProcessesPitData()
    {
        // Arrange
        await SetupEventWithLoops();
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);

        var passings = new List<Passing>
        {
            new Passing { TransponderId = 123, LoopId = 1, IsInPit = true },
            new Passing { TransponderId = 456, LoopId = 2, IsInPit = false }
        };

        var message = new TimingMessage("x2pass", JsonSerializer.Serialize(passings), 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("PitProcessorV2", result.Source);
        Assert.AreEqual(1, result.Changes.Count);
        Assert.IsTrue(result.Changes[0] is PitStateUpdate);

        var pitStateUpdate = (PitStateUpdate)result.Changes[0];
        Assert.IsTrue(pitStateUpdate.InPit.ContainsKey(123));
        Assert.IsTrue(pitStateUpdate.PitEntrance.ContainsKey(123));
        Assert.IsTrue(pitStateUpdate.PitExit.ContainsKey(456));
    }

    [TestMethod]
    public async Task Process_PitInLoop_UpdatesPitEntrance()
    {
        // Arrange
        await SetupEventWithLoops();
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);

        var passings = new List<Passing>
        {
            new Passing { TransponderId = 123, LoopId = 1, IsInPit = false } // LoopId 1 is PitIn
        };

        var message = new TimingMessage("x2pass", JsonSerializer.Serialize(passings), 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var pitStateUpdate = (PitStateUpdate)result.Changes[0];
        Assert.IsTrue(pitStateUpdate.PitEntrance.ContainsKey(123));
        Assert.IsFalse(pitStateUpdate.InPit.ContainsKey(123)); // IsInPit was false
    }

    [TestMethod]
    public async Task Process_PitExitLoop_UpdatesPitExit()
    {
        // Arrange
        await SetupEventWithLoops();
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);

        var passings = new List<Passing>
        {
            new Passing { TransponderId = 123, LoopId = 2, IsInPit = false } // LoopId 2 is PitExit
        };

        var message = new TimingMessage("x2pass", JsonSerializer.Serialize(passings), 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var pitStateUpdate = (PitStateUpdate)result.Changes[0];
        Assert.IsTrue(pitStateUpdate.PitExit.ContainsKey(123));
    }

    [TestMethod]
    public async Task Process_PitStartFinishLoop_UpdatesPitSf()
    {
        // Arrange
        await SetupEventWithLoops();
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);

        var passings = new List<Passing>
        {
            new Passing { TransponderId = 123, LoopId = 3, IsInPit = false } // LoopId 3 is PitStartFinish
        };

        var message = new TimingMessage("x2pass", JsonSerializer.Serialize(passings), 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var pitStateUpdate = (PitStateUpdate)result.Changes[0];
        Assert.IsTrue(pitStateUpdate.PitSf.ContainsKey(123));
    }

    [TestMethod]
    public async Task Process_PitOtherLoop_UpdatesPitOther()
    {
        // Arrange
        await SetupEventWithLoops();
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);

        var passings = new List<Passing>
        {
            new Passing { TransponderId = 123, LoopId = 4, IsInPit = false } // LoopId 4 is PitOther
        };

        var message = new TimingMessage("x2pass", JsonSerializer.Serialize(passings), 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var pitStateUpdate = (PitStateUpdate)result.Changes[0];
        Assert.IsTrue(pitStateUpdate.PitOther.ContainsKey(123));
    }

    [TestMethod]
    public async Task Process_OtherLoop_UpdatesOther()
    {
        // Arrange
        await SetupEventWithLoops();
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);

        var passings = new List<Passing>
        {
            new Passing { TransponderId = 123, LoopId = 5, IsInPit = false } // LoopId 5 is Other
        };

        var message = new TimingMessage("x2pass", JsonSerializer.Serialize(passings), 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var pitStateUpdate = (PitStateUpdate)result.Changes[0];
        Assert.IsTrue(pitStateUpdate.Other.ContainsKey(123));
    }

    #endregion

    #region Process Tests - Transponder Management

    [TestMethod]
    public async Task Process_MultiplePassingsForSameTransponder_KeepsLatestOnly()
    {
        // Arrange
        await SetupEventWithLoops();
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);

        var passings = new List<Passing>
        {
            new Passing { TransponderId = 123, LoopId = 1, IsInPit = true },  // First passing
            new Passing { TransponderId = 123, LoopId = 2, IsInPit = false }  // Second passing (should replace first)
        };

        var message = new TimingMessage("x2pass", JsonSerializer.Serialize(passings), 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var pitStateUpdate = (PitStateUpdate)result.Changes[0];
        
        // Should only have the latest passing for transponder 123
        Assert.IsFalse(pitStateUpdate.PitEntrance.ContainsKey(123)); // Should be removed
        Assert.IsTrue(pitStateUpdate.PitExit.ContainsKey(123)); // Should contain latest
        Assert.IsFalse(pitStateUpdate.InPit.ContainsKey(123)); // IsInPit was false in latest
    }

    [TestMethod]
    public async Task Process_UnknownLoopId_IgnoresPassingForLoopClassification()
    {
        // Arrange
        await SetupEventWithLoops();
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);

        var passings = new List<Passing>
        {
            new Passing { TransponderId = 123, LoopId = 999, IsInPit = true } // Unknown loop
        };

        var message = new TimingMessage("x2pass", JsonSerializer.Serialize(passings), 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var pitStateUpdate = (PitStateUpdate)result.Changes[0];
        
        // Should only update InPit based on IsInPit flag, not any loop-specific collections
        Assert.IsTrue(pitStateUpdate.InPit.ContainsKey(123));
        Assert.IsFalse(pitStateUpdate.PitEntrance.ContainsKey(123));
        Assert.IsFalse(pitStateUpdate.PitExit.ContainsKey(123));
        Assert.IsFalse(pitStateUpdate.PitSf.ContainsKey(123));
        Assert.IsFalse(pitStateUpdate.PitOther.ContainsKey(123));
        Assert.IsFalse(pitStateUpdate.Other.ContainsKey(123));
    }

    #endregion

    #region CarLapsWithPitStops Tests

    [TestMethod]
    public void GetCarLapsWithPitStops_ReturnsImmutableCopy()
    {
        // Act
        var result = _processor.GetCarLapsWithPitStops();

        // Assert
        Assert.IsNotNull(result);
        Assert.IsInstanceOfType(result, typeof(ImmutableDictionary<string, ImmutableHashSet<int>>));
    }

    #endregion

    #region UpdateCarPositionForLogging Tests

    [TestMethod]
    public void UpdateCarPositionForLogging_CarWithPitLaps_UpdatesLapIncludedPit()
    {
        // Arrange
        var carPosition = new CarPosition
        {
            Number = "123",
            LastLapCompleted = 5
        };

        // Add some pit laps to the internal collection using reflection
        var carLapsField = typeof(PitProcessorV2).GetField("carLapsWithPitStops", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(carLapsField, "carLapsWithPitStops field should exist");
        var carLapsDict = (Dictionary<string, HashSet<int>>)carLapsField.GetValue(_processor)!;
        carLapsDict["123"] = new HashSet<int> { 3, 5, 7 };

        // Act
        _processor.UpdateCarPositionForLogging(carPosition);

        // Assert
        // The method should correctly identify that lap 5 is included in pit stops
        Assert.IsTrue(carPosition.LapIncludedPit);
    }

    [TestMethod]
    public void UpdateCarPositionForLogging_CarWithoutPitLaps_DoesNotUpdateLapIncludedPit()
    {
        // Arrange
        var carPosition = new CarPosition
        {
            Number = "123",
            LastLapCompleted = 5,
            LapIncludedPit = false
        };

        // Act
        _processor.UpdateCarPositionForLogging(carPosition);

        // Assert
        Assert.IsFalse(carPosition.LapIncludedPit);
    }

    [TestMethod]
    public void UpdateCarPositionForLogging_CarWithNullNumber_DoesNotThrow()
    {
        // Arrange
        var carPosition = new CarPosition
        {
            Number = null,
            LastLapCompleted = 5
        };

        // Act & Assert - The method handles null gracefully due to the null check
        _processor.UpdateCarPositionForLogging(carPosition);
        // Should not throw - the condition carPosition.Number != null short-circuits
    }

    [TestMethod]
    public void UpdateCarPositionForLogging_CarNumberNotInPitLaps_DoesNotUpdateLapIncludedPit()
    {
        // Arrange
        var carPosition = new CarPosition
        {
            Number = "456", // Different car number
            LastLapCompleted = 5,
            LapIncludedPit = false
        };

        // Add pit laps for a different car
        var carLapsField = typeof(PitProcessorV2).GetField("carLapsWithPitStops", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(carLapsField, "carLapsWithPitStops field should exist");
        var carLapsDict = (Dictionary<string, HashSet<int>>)carLapsField.GetValue(_processor)!;
        carLapsDict["123"] = new HashSet<int> { 3, 5, 7 };

        // Act
        _processor.UpdateCarPositionForLogging(carPosition);

        // Assert
        Assert.IsFalse(carPosition.LapIncludedPit);
    }

    [TestMethod]
    public void UpdateCarPositionForLogging_BugInLogic_ExposedByTest()
    {
        // This test now verifies the corrected behavior
        // The method should properly check if a car has pit laps for the current lap
        
        // Arrange
        var carPosition = new CarPosition
        {
            Number = "123", // Car that has pit laps
            LastLapCompleted = 5,
            LapIncludedPit = false
        };

        // Add pit laps to the internal collection using reflection
        var carLapsField = typeof(PitProcessorV2).GetField("carLapsWithPitStops", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(carLapsField, "carLapsWithPitStops field should exist");
        var carLapsDict = (Dictionary<string, HashSet<int>>)carLapsField.GetValue(_processor)!;
        carLapsDict["123"] = new HashSet<int> { 3, 7 }; // Lap 5 is NOT in pit laps

        // Act
        _processor.UpdateCarPositionForLogging(carPosition);

        // Assert - The method should correctly identify that lap 5 is NOT in pit stops
        Assert.IsFalse(carPosition.LapIncludedPit);
        
        // Test the positive case
        carPosition.LastLapCompleted = 7; // Lap 7 IS in pit laps
        carPosition.LapIncludedPit = false; // Reset
        
        _processor.UpdateCarPositionForLogging(carPosition);
        Assert.IsTrue(carPosition.LapIncludedPit); // Should be true now
    }
    #endregion

    #region Integration Tests

    [TestMethod]
    public async Task Process_CompleteScenario_ProcessesCorrectly()
    {
        // Arrange
        await SetupEventWithLoops();
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);

        var passings = new List<Passing>
        {
            new Passing { TransponderId = 100, LoopId = 1, IsInPit = true },  // Pit entry
            new Passing { TransponderId = 200, LoopId = 2, IsInPit = true },  // Pit exit
            new Passing { TransponderId = 300, LoopId = 3, IsInPit = true },  // Pit start/finish
            new Passing { TransponderId = 400, LoopId = 4, IsInPit = false }, // Pit other
            new Passing { TransponderId = 500, LoopId = 5, IsInPit = false }  // Other loop
        };

        var message = new TimingMessage("x2pass", JsonSerializer.Serialize(passings), 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("PitProcessorV2", result.Source);
        Assert.AreEqual(1, result.Changes.Count);

        var pitStateUpdate = (PitStateUpdate)result.Changes[0];
        
        // Verify InPit collection
        Assert.IsTrue(pitStateUpdate.InPit.ContainsKey(100));
        Assert.IsTrue(pitStateUpdate.InPit.ContainsKey(200));
        Assert.IsTrue(pitStateUpdate.InPit.ContainsKey(300));
        Assert.IsFalse(pitStateUpdate.InPit.ContainsKey(400)); // IsInPit was false
        Assert.IsFalse(pitStateUpdate.InPit.ContainsKey(500)); // IsInPit was false

        // Verify loop-specific collections
        Assert.IsTrue(pitStateUpdate.PitEntrance.ContainsKey(100));
        Assert.IsTrue(pitStateUpdate.PitExit.ContainsKey(200));
        Assert.IsTrue(pitStateUpdate.PitSf.ContainsKey(300));
        Assert.IsTrue(pitStateUpdate.PitOther.ContainsKey(400));
        Assert.IsTrue(pitStateUpdate.Other.ContainsKey(500));

        // Verify loop metadata is included
        Assert.IsTrue(pitStateUpdate.LoopMetadata.ContainsKey(1));
        Assert.IsTrue(pitStateUpdate.LoopMetadata.ContainsKey(2));
        Assert.IsTrue(pitStateUpdate.LoopMetadata.ContainsKey(3));
        Assert.IsTrue(pitStateUpdate.LoopMetadata.ContainsKey(4));
        Assert.IsTrue(pitStateUpdate.LoopMetadata.ContainsKey(5));
    }

    #endregion

    #region Error Handling Tests

    [TestMethod]
    public async Task Process_InvalidJsonData_HandlesGracefully()
    {
        // Arrange
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);
        var message = new TimingMessage("x2pass", "invalid json", 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNull(result); // Should return null when JSON deserialization fails
    }

    [TestMethod]
    public async Task Process_NoEventConfigurationLoaded_ProcessesWithEmptyLoopMetadata()
    {
        // Arrange - Don't call SetupEventWithLoops, so no event configuration is loaded
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);

        var passings = new List<Passing>
        {
            new Passing { TransponderId = 123, LoopId = 1, IsInPit = true }
        };

        var message = new TimingMessage("x2pass", JsonSerializer.Serialize(passings), 1, DateTime.Now);

        // Act
        var result = await _processor.Process(message);

        // Assert
        Assert.IsNotNull(result);
        var pitStateUpdate = (PitStateUpdate)result.Changes[0];
        
        // Should still process InPit
        Assert.IsTrue(pitStateUpdate.InPit.ContainsKey(123));
        
        // But no loop-specific processing should occur
        Assert.IsFalse(pitStateUpdate.PitEntrance.ContainsKey(123));
        
        // LoopMetadata should be empty
        Assert.AreEqual(0, pitStateUpdate.LoopMetadata.Count);
    }

    #endregion

    #region Event Configuration Reload Tests

    [TestMethod]
    public async Task Process_EventChangedMessage_MatchingEventId_ReloadsConfiguration()
    {
        // Arrange
        var eventId = 123;
        await SetupEventWithLoops(); // This calls Initialize with eventId 123

        var eventChangedMessage = new TimingMessage("event-changed", eventId.ToString(), 1, DateTime.Now);

        // Setup a modified event configuration for reload
        var modifiedEventConfig = new ConfigurationEvent
        {
            Id = eventId,
            LoopsMetadata = new List<LoopMetadata>
            {
                new LoopMetadata { Id = 1, Type = LoopType.PitIn, Name = "Modified Pit Entry" },
                new LoopMetadata { Id = 2, Type = LoopType.PitExit, Name = "Modified Pit Exit" },
                new LoopMetadata { Id = 6, Type = LoopType.Other, Name = "New Loop" } // New loop added
            }
        };

        _mockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var databaseName = $"TestDatabase_{Guid.NewGuid()}";
                var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
                optionsBuilder.UseInMemoryDatabase(databaseName);
                var options = optionsBuilder.Options;
                
                var context = new TsContext(options);
                context.Events.Add(modifiedEventConfig);
                await context.SaveChangesAsync();
                return context;
            });

        // Act
        var result = await _processor.Process(eventChangedMessage);

        // Assert
        Assert.IsNull(result); // Event changed messages should return null
        
        // Verify that the configuration reload was logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Event configuration changed for event {eventId}, reloading")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        // Verify that the database was accessed to reload configuration
        _mockDbContextFactory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [TestMethod]
    public async Task Process_EventChangedMessage_DifferentEventId_DoesNotReloadConfiguration()
    {
        // Arrange
        var eventId = 123;
        var differentEventId = 456;
        await SetupEventWithLoops(); // This calls Initialize with eventId 123

        var eventChangedMessage = new TimingMessage("event-changed", differentEventId.ToString(), 1, DateTime.Now);

        // Reset the mock to track calls after initialization
        _mockDbContextFactory.Reset();
        _mockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TsContext(new DbContextOptionsBuilder<TsContext>().UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}").Options));

        // Act
        var result = await _processor.Process(eventChangedMessage);

        // Assert
        Assert.IsNull(result); // Event changed messages should return null
        
        // Verify that the configuration reload was NOT logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Event configuration changed for event {eventId}, reloading")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);

        // Verify that the database was NOT accessed for reload
        _mockDbContextFactory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Process_EventChangedMessage_InvalidEventIdFormat_DoesNotReloadConfiguration()
    {
        // Arrange
        var eventId = 123;
        await SetupEventWithLoops(); // This calls Initialize with eventId 123

        var eventChangedMessage = new TimingMessage("event-changed", "invalid-event-id", 1, DateTime.Now);

        // Reset the mock to track calls after initialization
        _mockDbContextFactory.Reset();
        _mockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TsContext(new DbContextOptionsBuilder<TsContext>().UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}").Options));

        // Act
        var result = await _processor.Process(eventChangedMessage);

        // Assert
        Assert.IsNull(result); // Event changed messages should return null
        
        // Verify that the configuration reload was NOT logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Event configuration changed for event {eventId}, reloading")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);

        // Verify that the database was NOT accessed for reload
        _mockDbContextFactory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Process_EventChangedMessage_EmptyEventIdString_DoesNotReloadConfiguration()
    {
        // Arrange
        var eventId = 123;
        await SetupEventWithLoops(); // This calls Initialize with eventId 123

        var eventChangedMessage = new TimingMessage("event-changed", "", 1, DateTime.Now);

        // Reset the mock to track calls after initialization
        _mockDbContextFactory.Reset();
        _mockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TsContext(new DbContextOptionsBuilder<TsContext>().UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}").Options));

        // Act
        var result = await _processor.Process(eventChangedMessage);

        // Assert
        Assert.IsNull(result); // Event changed messages should return null
        
        // Verify that the configuration reload was NOT logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Event configuration changed for event {eventId}, reloading")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);

        // Verify that the database was NOT accessed for reload
        _mockDbContextFactory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Process_EventChangedMessage_NullEventIdString_DoesNotReloadConfiguration()
    {
        // Arrange
        var eventId = 123;
        await SetupEventWithLoops(); // This calls Initialize with eventId 123

        var eventChangedMessage = new TimingMessage("event-changed", null!, 1, DateTime.Now);

        // Reset the mock to track calls after initialization
        _mockDbContextFactory.Reset();
        _mockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TsContext(new DbContextOptionsBuilder<TsContext>().UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}").Options));

        // Act
        var result = await _processor.Process(eventChangedMessage);

        // Assert
        Assert.IsNull(result); // Event changed messages should return null
        
        // Verify that the configuration reload was NOT logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Event configuration changed for event {eventId}, reloading")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);

        // Verify that the database was NOT accessed for reload
        _mockDbContextFactory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Process_EventChangedMessage_ProcessorNotInitialized_HandlesGracefully()
    {
        // Arrange - Don't call SetupEventWithLoops or Initialize, so eventId is default (0)
        var eventChangedMessage = new TimingMessage("event-changed", "123", 1, DateTime.Now);

        // Reset the mock to track calls
        _mockDbContextFactory.Reset();
        _mockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TsContext(new DbContextOptionsBuilder<TsContext>().UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}").Options));

        // Act
        var result = await _processor.Process(eventChangedMessage);

        // Assert
        Assert.IsNull(result); // Event changed messages should return null
        
        // Since eventId is 0 (not initialized), it won't match the message's eventId of 123
        // Verify that the configuration reload was NOT logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Event configuration changed for event")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [TestMethod]
    public async Task Process_EventChangedMessage_ReloadConfigurationAfterPassings_UsesNewConfiguration()
    {
        // Arrange
        var eventId = 123;
        await SetupEventWithLoops(); // Initialize with original configuration
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);

        // Process some passings with original configuration
        var passings = new List<Passing>
        {
            new Passing { TransponderId = 123, LoopId = 1, IsInPit = true } // LoopId 1 is PitIn in original config
        };
        var passingMessage = new TimingMessage("x2pass", JsonSerializer.Serialize(passings), 1, DateTime.Now);
        var firstResult = await _processor.Process(passingMessage);

        // Verify first result uses original configuration
        Assert.IsNotNull(firstResult);
        var firstPitStateUpdate = (PitStateUpdate)firstResult.Changes[0];
        Assert.IsTrue(firstPitStateUpdate.PitEntrance.ContainsKey(123)); // Should be in PitEntrance

        // Setup modified configuration for reload - change loop 1 from PitIn to PitExit
        var modifiedEventConfig = new ConfigurationEvent
        {
            Id = eventId,
            LoopsMetadata = new List<LoopMetadata>
            {
                new LoopMetadata { Id = 1, Type = LoopType.PitExit, Name = "Modified to Pit Exit" }, // Changed from PitIn to PitExit
                new LoopMetadata { Id = 2, Type = LoopType.PitIn, Name = "Pit Entry" }
            }
        };

        _mockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var databaseName = $"TestDatabase_{Guid.NewGuid()}";
                var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
                optionsBuilder.UseInMemoryDatabase(databaseName);
                var options = optionsBuilder.Options;
                
                var context = new TsContext(options);
                context.Events.Add(modifiedEventConfig);
                await context.SaveChangesAsync();
                return context;
            });

        // Act - Send event changed message to reload configuration
        var eventChangedMessage = new TimingMessage("event-changed", eventId.ToString(), 1, DateTime.Now);
        var reloadResult = await _processor.Process(eventChangedMessage);
        Assert.IsNull(reloadResult); // Event changed messages return null

        // Process the same passing again with new configuration
        var secondResult = await _processor.Process(passingMessage);

        // Assert
        Assert.IsNotNull(secondResult);
        var secondPitStateUpdate = (PitStateUpdate)secondResult.Changes[0];
        
        // Now loop 1 should be treated as PitExit instead of PitIn
        Assert.IsFalse(secondPitStateUpdate.PitEntrance.ContainsKey(123)); // Should NOT be in PitEntrance anymore
        Assert.IsTrue(secondPitStateUpdate.PitExit.ContainsKey(123)); // Should be in PitExit now

        // Verify reload was logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains($"Event configuration changed for event {eventId}, reloading")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region Helper Methods

    private async Task SetupEventWithLoops()
    {
        var eventConfig = new ConfigurationEvent
        {
            Id = 123,
            LoopsMetadata = new List<LoopMetadata>
            {
                new LoopMetadata { Id = 1, Type = LoopType.PitIn, Name = "Pit Entry" },
                new LoopMetadata { Id = 2, Type = LoopType.PitExit, Name = "Pit Exit" },
                new LoopMetadata { Id = 3, Type = LoopType.PitStartFinish, Name = "Pit Start/Finish" },
                new LoopMetadata { Id = 4, Type = LoopType.PitOther, Name = "Pit Other" },
                new LoopMetadata { Id = 5, Type = LoopType.Other, Name = "Other Loop" }
            }
        };

        // Use the real database context to add the event configuration
        _mockDbContextFactory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var databaseName = $"TestDatabase_{Guid.NewGuid()}";
                var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
                optionsBuilder.UseInMemoryDatabase(databaseName);
                var options = optionsBuilder.Options;
                
                var context = new TsContext(options);
                context.Events.Add(eventConfig);
                await context.SaveChangesAsync();
                return context;
            });

        await _processor.Initialize(123);
    }

    #endregion

    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidParameters_CreatesInstance()
    {
        // Arrange & Act
        var processor = new PitProcessorV2(_mockDbContextFactory.Object, _mockLoggerFactory.Object, _mockSessionContext.Object);

        // Assert
        Assert.IsNotNull(processor);
        // Note: CreateLogger is called once during Setup() and once during this test
        _mockLoggerFactory.Verify(x => x.CreateLogger(It.IsAny<string>()), Times.AtLeast(1));
    }

    [TestMethod]
    public void Constructor_NullDbContextFactory_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.ThrowsExactly<ArgumentNullException>(() => 
            new PitProcessorV2(null!, _mockLoggerFactory.Object, _mockSessionContext.Object));
    }

    [TestMethod]
    public void Constructor_NullLoggerFactory_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.ThrowsExactly<ArgumentNullException>(() => 
            new PitProcessorV2(_mockDbContextFactory.Object, null!, _mockSessionContext.Object));
    }

    [TestMethod]
    public void Constructor_NullSessionContext_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.ThrowsExactly<ArgumentNullException>(() => 
            new PitProcessorV2(_mockDbContextFactory.Object, _mockLoggerFactory.Object, null!));
    }

    #endregion

    #region Concurrency Tests

    [TestMethod]
    public async Task Process_ConcurrentCalls_ThreadSafe()
    {
        // Arrange
        await SetupEventWithLoops();
        _mockSessionContext.Setup(x => x.IsMultiloopActive).Returns(false);

        var passings = new List<Passing>
        {
            new Passing { TransponderId = 123, LoopId = 1, IsInPit = true }
        };

        var message = new TimingMessage("x2pass", JsonSerializer.Serialize(passings), 1, DateTime.Now);
        var tasks = new List<Task<SessionStateUpdate?>>();

        // Act - Run multiple concurrent processes
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(_processor.Process(message));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.IsTrue(results.All(r => r is not null));
        Assert.IsTrue(results.All(r => r!.Source == "PitProcessorV2"));
    }

    #endregion
}
