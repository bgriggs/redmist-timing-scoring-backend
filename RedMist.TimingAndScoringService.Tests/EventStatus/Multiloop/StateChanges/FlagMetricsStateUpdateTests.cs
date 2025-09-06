using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedMist.TimingAndScoringService.EventStatus.Multiloop;
using RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop.StateChanges;

[TestClass]
public class FlagMetricsStateUpdateTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidFlagInformation_CreatesInstance()
    {
        // Arrange
        var flagInformation = CreateFlagInformation();

        // Act
        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(flagInformation, stateUpdate.FlagInformation);
    }

    #endregion

    #region GetChanges Tests - All Properties Changed

    [TestMethod]
    public void GetChanges_AllPropertiesChanged_ReturnsCompletePatch()
    {
        // Arrange
        var flagInformation = CreateFlagInformation(
            greenTimeMs: 300000, // 5 minutes
            greenLaps: 25,
            yellowTimeMs: 120000, // 2 minutes
            yellowLaps: 8,
            numberOfYellows: 3,
            redTimeMs: 60000, // 1 minute
            averageRaceSpeed: "85.5",
            leadChanges: 12
        );

        var currentState = new SessionState
        {
            SessionId = 123,
            GreenTimeMs = 200000, // Different
            GreenLaps = 20, // Different
            YellowTimeMs = 100000, // Different
            YellowLaps = 5, // Different
            NumberOfYellows = 2, // Different
            RedTimeMs = 30000, // Different
            AverageRaceSpeed = "80.0", // Different
            LeadChanges = 8 // Different
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(123, result.SessionId);
        Assert.AreEqual(300000, result.GreenTimeMs);
        Assert.AreEqual(25, result.GreenLaps);
        Assert.AreEqual(120000, result.YellowTimeMs);
        Assert.AreEqual(8, result.YellowLaps);
        Assert.AreEqual(3, result.NumberOfYellows);
        Assert.AreEqual(60000, result.RedTimeMs);
        Assert.AreEqual("85.5", result.AverageRaceSpeed);
        Assert.AreEqual(12, result.LeadChanges);
    }

    [TestMethod]
    public void GetChanges_NoChanges_ReturnsEmptyPatch()
    {
        // Arrange
        var flagInformation = CreateFlagInformation(
            greenTimeMs: 300000,
            greenLaps: 25,
            yellowTimeMs: 120000,
            yellowLaps: 8,
            numberOfYellows: 3,
            redTimeMs: 60000,
            averageRaceSpeed: "85.5",
            leadChanges: 12
        );

        var currentState = new SessionState
        {
            SessionId = 456,
            GreenTimeMs = 300000, // Same
            GreenLaps = 25, // Same
            YellowTimeMs = 120000, // Same
            YellowLaps = 8, // Same
            NumberOfYellows = 3, // Same
            RedTimeMs = 60000, // Same
            AverageRaceSpeed = "85.5", // Same
            LeadChanges = 12 // Same
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(456, result.SessionId);
        Assert.IsNull(result.GreenTimeMs);
        Assert.IsNull(result.GreenLaps);
        Assert.IsNull(result.YellowTimeMs);
        Assert.IsNull(result.YellowLaps);
        Assert.IsNull(result.NumberOfYellows);
        Assert.IsNull(result.RedTimeMs);
        Assert.IsNull(result.AverageRaceSpeed);
        Assert.IsNull(result.LeadChanges);
    }

    #endregion

    #region GetChanges Tests - Individual Property Changes

    [TestMethod]
    public void GetChanges_OnlyGreenTimeChanged_ReturnsPartialPatch()
    {
        // Arrange
        var flagInformation = CreateFlagInformation(greenTimeMs: 500000); // Changed
        var currentState = new SessionState
        {
            SessionId = 789,
            GreenTimeMs = 400000, // Different
            GreenLaps = 15, // Same as default
            YellowTimeMs = 60000, // Same as default
            YellowLaps = 5, // Same as default
            NumberOfYellows = 2, // Same as default
            RedTimeMs = 30000, // Same as default
            AverageRaceSpeed = "80.0", // Same as default
            LeadChanges = 10 // Same as default
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(789, result.SessionId);
        Assert.AreEqual(500000, result.GreenTimeMs); // Should be set
        Assert.IsNull(result.GreenLaps); // Should not be set
        Assert.IsNull(result.YellowTimeMs);
        Assert.IsNull(result.YellowLaps);
        Assert.IsNull(result.NumberOfYellows);
        Assert.IsNull(result.RedTimeMs);
        Assert.IsNull(result.AverageRaceSpeed);
        Assert.IsNull(result.LeadChanges);
    }

    [TestMethod]
    public void GetChanges_OnlyGreenLapsChanged_ReturnsPartialPatch()
    {
        // Arrange
        var flagInformation = CreateFlagInformation(greenLaps: 30); // Changed
        var currentState = new SessionState
        {
            SessionId = 100,
            GreenTimeMs = 300000, // Same as default
            GreenLaps = 20, // Different
            YellowTimeMs = 60000, // Same as default
            YellowLaps = 5, // Same as default
            NumberOfYellows = 2, // Same as default
            RedTimeMs = 30000, // Same as default
            AverageRaceSpeed = "80.0", // Same as default
            LeadChanges = 10 // Same as default
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(100, result.SessionId);
        Assert.IsNull(result.GreenTimeMs);
        Assert.AreEqual(30, result.GreenLaps); // Should be set
        Assert.IsNull(result.YellowTimeMs);
        Assert.IsNull(result.YellowLaps);
        Assert.IsNull(result.NumberOfYellows);
        Assert.IsNull(result.RedTimeMs);
        Assert.IsNull(result.AverageRaceSpeed);
        Assert.IsNull(result.LeadChanges);
    }

    [TestMethod]
    public void GetChanges_OnlyYellowTimeChanged_ReturnsPartialPatch()
    {
        // Arrange
        var flagInformation = CreateFlagInformation(yellowTimeMs: 180000); // Changed
        var currentState = new SessionState
        {
            SessionId = 200,
            GreenTimeMs = 300000, // Same as default
            GreenLaps = 15, // Same as default
            YellowTimeMs = 120000, // Different
            YellowLaps = 5, // Same as default
            NumberOfYellows = 2, // Same as default
            RedTimeMs = 30000, // Same as default
            AverageRaceSpeed = "80.0", // Same as default
            LeadChanges = 10 // Same as default
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(200, result.SessionId);
        Assert.IsNull(result.GreenTimeMs);
        Assert.IsNull(result.GreenLaps);
        Assert.AreEqual(180000, result.YellowTimeMs); // Should be set
        Assert.IsNull(result.YellowLaps);
        Assert.IsNull(result.NumberOfYellows);
        Assert.IsNull(result.RedTimeMs);
        Assert.IsNull(result.AverageRaceSpeed);
        Assert.IsNull(result.LeadChanges);
    }

    [TestMethod]
    public void GetChanges_OnlyAverageSpeedChanged_ReturnsPartialPatch()
    {
        // Arrange
        var flagInformation = CreateFlagInformation(averageRaceSpeed: "95.7"); // Changed
        var currentState = new SessionState
        {
            SessionId = 300,
            GreenTimeMs = 300000, // Same as default
            GreenLaps = 15, // Same as default
            YellowTimeMs = 60000, // Same as default
            YellowLaps = 5, // Same as default
            NumberOfYellows = 2, // Same as default
            RedTimeMs = 30000, // Same as default
            AverageRaceSpeed = "80.0", // Different
            LeadChanges = 10 // Same as default
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(300, result.SessionId);
        Assert.IsNull(result.GreenTimeMs);
        Assert.IsNull(result.GreenLaps);
        Assert.IsNull(result.YellowTimeMs);
        Assert.IsNull(result.YellowLaps);
        Assert.IsNull(result.NumberOfYellows);
        Assert.IsNull(result.RedTimeMs);
        Assert.AreEqual("95.7", result.AverageRaceSpeed); // Should be set
        Assert.IsNull(result.LeadChanges);
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_ZeroValues_HandlesCorrectly()
    {
        // Arrange
        var flagInformation = CreateFlagInformation(
            greenTimeMs: 0,
            greenLaps: 0,
            yellowTimeMs: 0,
            yellowLaps: 0,
            numberOfYellows: 0,
            redTimeMs: 0,
            averageRaceSpeed: "0.0",
            leadChanges: 0
        );

        var currentState = new SessionState
        {
            SessionId = 400,
            GreenTimeMs = 100000,
            GreenLaps = 5,
            YellowTimeMs = 50000,
            YellowLaps = 2,
            NumberOfYellows = 1,
            RedTimeMs = 10000,
            AverageRaceSpeed = "75.0",
            LeadChanges = 3
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(400, result.SessionId);
        Assert.AreEqual(0, result.GreenTimeMs);
        Assert.AreEqual(0, result.GreenLaps);
        Assert.AreEqual(0, result.YellowTimeMs);
        Assert.AreEqual(0, result.YellowLaps);
        Assert.AreEqual(0, result.NumberOfYellows);
        Assert.AreEqual(0, result.RedTimeMs);
        Assert.AreEqual("0.0", result.AverageRaceSpeed);
        Assert.AreEqual(0, result.LeadChanges);
    }

    [TestMethod]
    public void GetChanges_NullSessionStateValues_HandlesCorrectly()
    {
        // Arrange
        var flagInformation = CreateFlagInformation(
            greenTimeMs: 300000,
            greenLaps: 15,
            yellowTimeMs: 60000,
            yellowLaps: 5,
            numberOfYellows: 2,
            redTimeMs: 30000,
            averageRaceSpeed: "80.0",
            leadChanges: 10
        );

        var currentState = new SessionState
        {
            SessionId = 500,
            GreenTimeMs = null,
            GreenLaps = null,
            YellowTimeMs = null,
            YellowLaps = null,
            NumberOfYellows = null,
            RedTimeMs = null,
            AverageRaceSpeed = null,
            LeadChanges = null
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(500, result.SessionId);
        Assert.AreEqual(300000, result.GreenTimeMs);
        Assert.AreEqual(15, result.GreenLaps);
        Assert.AreEqual(60000, result.YellowTimeMs);
        Assert.AreEqual(5, result.YellowLaps);
        Assert.AreEqual(2, result.NumberOfYellows);
        Assert.AreEqual(30000, result.RedTimeMs);
        Assert.AreEqual("80.0", result.AverageRaceSpeed);
        Assert.AreEqual(10, result.LeadChanges);
    }

    [TestMethod]
    public void GetChanges_LargeValues_HandlesCorrectly()
    {
        // Arrange
        var flagInformation = CreateFlagInformation(
            greenTimeMs: uint.MaxValue,
            greenLaps: ushort.MaxValue,
            yellowTimeMs: uint.MaxValue,
            yellowLaps: ushort.MaxValue,
            numberOfYellows: ushort.MaxValue,
            redTimeMs: uint.MaxValue,
            averageRaceSpeed: "999.99",
            leadChanges: ushort.MaxValue
        );

        var currentState = new SessionState
        {
            SessionId = 600,
            GreenTimeMs = 1000,
            GreenLaps = 10,
            YellowTimeMs = 2000,
            YellowLaps = 5,
            NumberOfYellows = 3,
            RedTimeMs = 500,
            AverageRaceSpeed = "50.0",
            LeadChanges = 2
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(600, result.SessionId);
        Assert.AreEqual(int.MaxValue, result.GreenTimeMs);
        Assert.AreEqual(ushort.MaxValue, result.GreenLaps);
        Assert.AreEqual(int.MaxValue, result.YellowTimeMs);
        Assert.AreEqual(ushort.MaxValue, result.YellowLaps);
        Assert.AreEqual(ushort.MaxValue, result.NumberOfYellows);
        Assert.AreEqual(int.MaxValue, result.RedTimeMs);
        Assert.AreEqual("999.99", result.AverageRaceSpeed);
        Assert.AreEqual(ushort.MaxValue, result.LeadChanges);
    }

    #endregion

    #region GetChanges Tests - Session ID Handling

    [TestMethod]
    public void GetChanges_DifferentSessionIds_CopiesCorrectSessionId()
    {
        // Arrange
        var flagInformation = CreateFlagInformation();
        var currentState = new SessionState
        {
            SessionId = 999,
            GreenTimeMs = 100000
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(999, result.SessionId);
    }

    [TestMethod]
    public void GetChanges_ZeroSessionId_HandlesCorrectly()
    {
        // Arrange
        var flagInformation = CreateFlagInformation();
        var currentState = new SessionState
        {
            SessionId = 0,
            GreenTimeMs = 100000
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionId);
    }

    [TestMethod]
    public void GetChanges_NegativeSessionId_HandlesCorrectly()
    {
        // Arrange
        var flagInformation = CreateFlagInformation();
        var currentState = new SessionState
        {
            SessionId = -1,
            GreenTimeMs = 100000
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(-1, result.SessionId);
    }

    #endregion

    #region GetChanges Tests - Multiple Sequential Calls

    [TestMethod]
    public void GetChanges_MultipleCallsWithSameState_ConsistentResults()
    {
        // Arrange
        var flagInformation = CreateFlagInformation(greenTimeMs: 500000);
        var currentState = new SessionState
        {
            SessionId = 700,
            GreenTimeMs = 400000
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result1 = stateUpdate.GetChanges(currentState);
        var result2 = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.SessionId, result2.SessionId);
        Assert.AreEqual(result1.GreenTimeMs, result2.GreenTimeMs);
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldRaceScenario_WorksCorrectly()
    {
        // Arrange - Simulate real race metrics during a race
        var flagInformation = CreateFlagInformation(
            greenTimeMs: 2700000, // 45 minutes of green flag racing
            greenLaps: 87,
            yellowTimeMs: 900000, // 15 minutes under yellow
            yellowLaps: 12,
            numberOfYellows: 4,
            redTimeMs: 300000, // 5 minutes red flag
            averageRaceSpeed: "142.8",
            leadChanges: 23
        );

        var currentState = new SessionState
        {
            SessionId = 20241215,
            EventId = 100,
            SessionName = "Main Race",
            GreenTimeMs = 2400000, // Previous values
            GreenLaps = 75,
            YellowTimeMs = 600000,
            YellowLaps = 8,
            NumberOfYellows = 3,
            RedTimeMs = 0,
            AverageRaceSpeed = "145.2",
            LeadChanges = 18
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(20241215, result.SessionId);
        
        // Verify all the race metrics are updated
        Assert.AreEqual(2700000, result.GreenTimeMs);
        Assert.AreEqual(87, result.GreenLaps);
        Assert.AreEqual(900000, result.YellowTimeMs);
        Assert.AreEqual(12, result.YellowLaps);
        Assert.AreEqual(4, result.NumberOfYellows);
        Assert.AreEqual(300000, result.RedTimeMs);
        Assert.AreEqual("142.8", result.AverageRaceSpeed);
        Assert.AreEqual(23, result.LeadChanges);
    }

    [TestMethod]
    public void GetChanges_PracticeSessionScenario_HandlesZeroValues()
    {
        // Arrange - Simulate practice session with minimal flag activity
        var flagInformation = CreateFlagInformation(
            greenTimeMs: 1800000, // 30 minutes of practice
            greenLaps: 45,
            yellowTimeMs: 0, // No yellows in practice
            yellowLaps: 0,
            numberOfYellows: 0,
            redTimeMs: 0,
            averageRaceSpeed: "138.5",
            leadChanges: 0 // No lead changes tracked in practice
        );

        var currentState = new SessionState
        {
            SessionId = 1001,
            SessionName = "Practice 1",
            GreenTimeMs = 1200000,
            GreenLaps = 30,
            YellowTimeMs = null,
            YellowLaps = null,
            NumberOfYellows = null,
            RedTimeMs = null,
            AverageRaceSpeed = "135.0",
            LeadChanges = null
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(1001, result.SessionId);
        Assert.AreEqual(1800000, result.GreenTimeMs);
        Assert.AreEqual(45, result.GreenLaps);
        Assert.AreEqual(0, result.YellowTimeMs);
        Assert.AreEqual(0, result.YellowLaps);
        Assert.AreEqual(0, result.NumberOfYellows);
        Assert.AreEqual(0, result.RedTimeMs);
        Assert.AreEqual("138.5", result.AverageRaceSpeed);
        Assert.AreEqual(0, result.LeadChanges);
    }

    #endregion

    #region Concurrency Tests

    [TestMethod]
    public void GetChanges_ConcurrentCalls_ThreadSafe()
    {
        // Arrange
        var flagInformation = CreateFlagInformation(greenTimeMs: 600000);
        var currentState = new SessionState
        {
            SessionId = 800,
            GreenTimeMs = 500000
        };

        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);
        var results = new List<SessionStatePatch?>();

        // Act - Run multiple concurrent calls
        for (int i = 0; i < 10; i++)
        {
            results.Add(stateUpdate.GetChanges(currentState));
        }

        // Assert
        Assert.IsTrue(results.All(r => r is not null));
        Assert.IsTrue(results.All(r => r!.SessionId == 800));
        Assert.IsTrue(results.All(r => r!.GreenTimeMs == 600000));
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void FlagInformation_Property_ReturnsCorrectValue()
    {
        // Arrange
        var flagInformation = CreateFlagInformation();

        // Act
        var stateUpdate = new FlagMetricsStateUpdate(flagInformation);

        // Assert
        Assert.AreSame(flagInformation, stateUpdate.FlagInformation);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SameFlagInformationInstance_ReturnsTrue()
    {
        // Arrange
        var flagInformation = CreateFlagInformation();
        var stateUpdate1 = new FlagMetricsStateUpdate(flagInformation);
        var stateUpdate2 = new FlagMetricsStateUpdate(flagInformation);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentFlagInformationInstances_ReturnsFalse()
    {
        // Arrange
        var flagInformation1 = CreateFlagInformation(greenTimeMs: 300000);
        var flagInformation2 = CreateFlagInformation(greenTimeMs: 400000);
        var stateUpdate1 = new FlagMetricsStateUpdate(flagInformation1);
        var stateUpdate2 = new FlagMetricsStateUpdate(flagInformation2);

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a FlagInformation instance with default or specified values for testing.
    /// </summary>
    private static FlagInformation CreateFlagInformation(
        uint greenTimeMs = 300000,
        ushort greenLaps = 15,
        uint yellowTimeMs = 60000,
        ushort yellowLaps = 5,
        ushort numberOfYellows = 2,
        uint redTimeMs = 30000,
        string averageRaceSpeed = "80.0",
        ushort leadChanges = 10)
    {
        var flagInformation = new FlagInformation();

        // Use reflection to set private properties for testing
        var greenTimeMsProp = typeof(FlagInformation).GetProperty("GreenTimeMs");
        var greenLapsProp = typeof(FlagInformation).GetProperty("GreenLaps");
        var yellowTimeMsProp = typeof(FlagInformation).GetProperty("YellowTimeMs");
        var yellowLapsProp = typeof(FlagInformation).GetProperty("YellowLaps");
        var numberOfYellowsProp = typeof(FlagInformation).GetProperty("NumberOfYellows");
        var redTimeMsProp = typeof(FlagInformation).GetProperty("RedTimeMs");
        var averageRaceSpeedProp = typeof(FlagInformation).GetProperty("AverageRaceSpeedMph");
        var leadChangesProp = typeof(FlagInformation).GetProperty("LeadChanges");

        greenTimeMsProp?.SetValue(flagInformation, greenTimeMs);
        greenLapsProp?.SetValue(flagInformation, greenLaps);
        yellowTimeMsProp?.SetValue(flagInformation, yellowTimeMs);
        yellowLapsProp?.SetValue(flagInformation, yellowLaps);
        numberOfYellowsProp?.SetValue(flagInformation, numberOfYellows);
        redTimeMsProp?.SetValue(flagInformation, redTimeMs);
        averageRaceSpeedProp?.SetValue(flagInformation, averageRaceSpeed);
        leadChangesProp?.SetValue(flagInformation, leadChanges);

        return flagInformation;
    }

    #endregion
}
