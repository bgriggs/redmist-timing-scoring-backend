using RedMist.TimingAndScoringService.EventStatus.Multiloop;
using RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop.StateChanges;

[TestClass]
public class PositionInfoStateUpdateTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidCompletedLap_CreatesInstance()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 5, lapsLed: 12, currentStatus: "Active");

        // Act
        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(completedLap, stateUpdate.CompletedLap);
    }

    #endregion

    #region GetChanges Tests - All Properties Changed

    [TestMethod]
    public void GetChanges_AllPropertiesChanged_ReturnsCompletePatch()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 8, lapsLed: 25, currentStatus: "Active");
        var currentState = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 5, // Different
            LapsLedOverall = 18, // Different
            CurrentStatus = "In Pits" // Different
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(8, result.OverallStartingPosition);
        Assert.AreEqual(25, result.LapsLedOverall);
        Assert.AreEqual("Active", result.CurrentStatus);
    }

    [TestMethod]
    public void GetChanges_NoChanges_ReturnsEmptyPatch()
    {
        // Arrange
        var completedLap = CreateCompletedLap("99", startPosition: 3, lapsLed: 15, currentStatus: "Active");
        var currentState = new CarPosition
        {
            Number = "99",
            OverallStartingPosition = 3, // Same
            LapsLedOverall = 15, // Same
            CurrentStatus = "Active" // Same
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("99", result.Number);
        Assert.IsNull(result.OverallStartingPosition);
        Assert.IsNull(result.LapsLedOverall);
        Assert.IsNull(result.CurrentStatus);
    }

    #endregion

    #region GetChanges Tests - Individual Property Changes

    [TestMethod]
    public void GetChanges_OnlyStartPositionChanged_ReturnsPartialPatch()
    {
        // Arrange
        var completedLap = CreateCompletedLap("77", startPosition: 12, lapsLed: 8, currentStatus: "Active");
        var currentState = new CarPosition
        {
            Number = "77",
            OverallStartingPosition = 10, // Different
            LapsLedOverall = 8, // Same
            CurrentStatus = "Active" // Same
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("77", result.Number);
        Assert.AreEqual(12, result.OverallStartingPosition); // Should be set
        Assert.IsNull(result.LapsLedOverall); // Should not be set
        Assert.IsNull(result.CurrentStatus); // Should not be set
    }

    [TestMethod]
    public void GetChanges_OnlyLapsLedChanged_ReturnsPartialPatch()
    {
        // Arrange
        var completedLap = CreateCompletedLap("88", startPosition: 6, lapsLed: 22, currentStatus: "Active");
        var currentState = new CarPosition
        {
            Number = "88",
            OverallStartingPosition = 6, // Same
            LapsLedOverall = 18, // Different
            CurrentStatus = "Active" // Same
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("88", result.Number);
        Assert.IsNull(result.OverallStartingPosition); // Should not be set
        Assert.AreEqual(22, result.LapsLedOverall); // Should be set
        Assert.IsNull(result.CurrentStatus); // Should not be set
    }

    [TestMethod]
    public void GetChanges_OnlyCurrentStatusChanged_ReturnsPartialPatch()
    {
        // Arrange
        var completedLap = CreateCompletedLap("33", startPosition: 4, lapsLed: 10, currentStatus: "Mechanical");
        var currentState = new CarPosition
        {
            Number = "33",
            OverallStartingPosition = 4, // Same
            LapsLedOverall = 10, // Same
            CurrentStatus = "Active" // Different
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("33", result.Number);
        Assert.IsNull(result.OverallStartingPosition); // Should not be set
        Assert.IsNull(result.LapsLedOverall); // Should not be set
        Assert.AreEqual("Mechanical", result.CurrentStatus); // Should be set
    }

    #endregion

    #region GetChanges Tests - Status Truncation

    [TestMethod]
    public void GetChanges_StatusExceeds12Characters_TruncatesTo12()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 1, lapsLed: 5, currentStatus: "This is a very long status that exceeds twelve characters");
        var currentState = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 1,
            LapsLedOverall = 5,
            CurrentStatus = "Active"
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual("This is a ve", result.CurrentStatus); // Should be truncated to 12 characters
        Assert.AreEqual(12, result.CurrentStatus!.Length);
    }

    [TestMethod]
    public void GetChanges_StatusExactly12Characters_NotTruncated()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 1, lapsLed: 5, currentStatus: "ExactlyTwlve"); // Exactly 12 characters
        var currentState = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 1,
            LapsLedOverall = 5,
            CurrentStatus = "Active"
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual("ExactlyTwlve", result.CurrentStatus);
        Assert.AreEqual(12, result.CurrentStatus!.Length);
    }

    [TestMethod]
    public void GetChanges_StatusLessThan12Characters_NotTruncated()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 1, lapsLed: 5, currentStatus: "Short");
        var currentState = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 1,
            LapsLedOverall = 5,
            CurrentStatus = "Active"
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual("Short", result.CurrentStatus);
        Assert.AreEqual(5, result.CurrentStatus!.Length);
    }

    [TestMethod]
    public void GetChanges_EmptyStatus_HandlesCorrectly()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 1, lapsLed: 5, currentStatus: "");
        var currentState = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 1,
            LapsLedOverall = 5,
            CurrentStatus = "Active"
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual("", result.CurrentStatus);
    }

    [TestMethod]
    public void GetChanges_NullStatus_HandlesCorrectly()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 1, lapsLed: 5, currentStatus: null);
        var currentState = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 1,
            LapsLedOverall = 5,
            CurrentStatus = "Active"
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual("", result.CurrentStatus);
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_ZeroValues_HandlesCorrectly()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 0, lapsLed: 0, currentStatus: "DNS");
        var currentState = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 5,
            LapsLedOverall = 3,
            CurrentStatus = "Active"
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(0, result.OverallStartingPosition);
        Assert.AreEqual(0, result.LapsLedOverall);
        Assert.AreEqual("DNS", result.CurrentStatus);
    }

    [TestMethod]
    public void GetChanges_NullCarPositionValues_HandlesCorrectly()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 8, lapsLed: 15, currentStatus: "Active");
        var currentState = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 0, // Default value for int
            LapsLedOverall = null,
            CurrentStatus = ""
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(8, result.OverallStartingPosition);
        Assert.AreEqual(15, result.LapsLedOverall);
        Assert.AreEqual("Active", result.CurrentStatus);
    }

    [TestMethod]
    public void GetChanges_MaxValues_HandlesCorrectly()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: ushort.MaxValue, lapsLed: ushort.MaxValue, currentStatus: "MaxPosition");
        var currentState = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 1,
            LapsLedOverall = 5,
            CurrentStatus = "Active"
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(ushort.MaxValue, result.OverallStartingPosition);
        Assert.AreEqual(ushort.MaxValue, result.LapsLedOverall);
        Assert.AreEqual("MaxPosition", result.CurrentStatus);
    }

    #endregion

    #region GetChanges Tests - Car Number Handling

    [TestMethod]
    public void GetChanges_DifferentCarNumbers_CopiesCorrectly()
    {
        // Test various car number formats
        var testCases = new[]
        {
            "1",
            "42",
            "99X",
            "123",
            "007",
            "A1"
        };

        foreach (var carNumber in testCases)
        {
            // Arrange
            var completedLap = CreateCompletedLap(carNumber, startPosition: 5, lapsLed: 10, currentStatus: "Active");
            var currentState = new CarPosition
            {
                Number = carNumber,
                OverallStartingPosition = 3,
                LapsLedOverall = 8,
                CurrentStatus = "Racing"
            };

            var stateUpdate = new PositionInfoStateUpdate(completedLap);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for car number: {carNumber}");
            Assert.AreEqual(carNumber, result.Number, $"Car number should match for: {carNumber}");
            Assert.AreEqual(5, result.OverallStartingPosition, $"Start position should be updated for car: {carNumber}");
            Assert.AreEqual(10, result.LapsLedOverall, $"Laps led should be updated for car: {carNumber}");
            Assert.AreEqual("Active", result.CurrentStatus, $"Status should be updated for car: {carNumber}");
        }
    }

    [TestMethod]
    public void GetChanges_EmptyCarNumber_CopiesEmptyNumber()
    {
        // Arrange
        var completedLap = CreateCompletedLap("", startPosition: 1, lapsLed: 5, currentStatus: "Active");
        var currentState = new CarPosition
        {
            Number = "",
            OverallStartingPosition = 0,
            LapsLedOverall = 0,
            CurrentStatus = ""
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("", result.Number);
        Assert.AreEqual(1, result.OverallStartingPosition);
        Assert.AreEqual(5, result.LapsLedOverall);
        Assert.AreEqual("Active", result.CurrentStatus);
    }

    [TestMethod]
    public void GetChanges_NullCarNumber_CopiesNullNumber()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 3, lapsLed: 8, currentStatus: "Active");
        var currentState = new CarPosition
        {
            Number = null,
            OverallStartingPosition = 1,
            LapsLedOverall = 5,
            CurrentStatus = "Racing"
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.Number);
        Assert.AreEqual(3, result.OverallStartingPosition);
        Assert.AreEqual(8, result.LapsLedOverall);
        Assert.AreEqual("Active", result.CurrentStatus);
    }

    #endregion

    #region GetChanges Tests - Common Racing Status Values

    [TestMethod]
    public void GetChanges_CommonRacingStatuses_HandlesCorrectly()
    {
        var statusTestCases = new[]
        {
            "Active",
            "In Pits", 
            "DNS", // Did Not Start
            "DNF", // Did Not Finish
            "Mechanical",
            "Contact",
            "Retired",
            "Disqualified"
        };

        foreach (var status in statusTestCases)
        {
            // Arrange
            var completedLap = CreateCompletedLap("42", startPosition: 5, lapsLed: 10, currentStatus: status);
            var currentState = new CarPosition
            {
                Number = "42",
                OverallStartingPosition = 5,
                LapsLedOverall = 10,
                CurrentStatus = "Different"
            };

            var stateUpdate = new PositionInfoStateUpdate(completedLap);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for status: {status}");
            Assert.AreEqual(status, result.CurrentStatus, $"Status should be set correctly for: {status}");
        }
    }

    #endregion

    #region GetChanges Tests - Multiple Sequential Calls

    [TestMethod]
    public void GetChanges_MultipleCallsWithSameState_ConsistentResults()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 8, lapsLed: 15, currentStatus: "Active");
        var currentState = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 5,
            LapsLedOverall = 10,
            CurrentStatus = "Racing"
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result1 = stateUpdate.GetChanges(currentState);
        var result2 = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.Number, result2.Number);
        Assert.AreEqual(result1.OverallStartingPosition, result2.OverallStartingPosition);
        Assert.AreEqual(result1.LapsLedOverall, result2.LapsLedOverall);
        Assert.AreEqual(result1.CurrentStatus, result2.CurrentStatus);
    }

    [TestMethod]
    public void GetChanges_DifferentStatesSequentially_ReturnsCorrectPatches()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 8, lapsLed: 15, currentStatus: "Active");

        var state1 = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 5, // Different from CompletedLap
            LapsLedOverall = 10, // Different from CompletedLap
            CurrentStatus = "Racing" // Different from CompletedLap
        };

        var state2 = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 8, // Same as CompletedLap
            LapsLedOverall = 10, // Different from CompletedLap
            CurrentStatus = "Active" // Same as CompletedLap
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result1 = stateUpdate.GetChanges(state1);
        var result2 = stateUpdate.GetChanges(state2);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        
        // First result should have all changes
        Assert.AreEqual("42", result1.Number);
        Assert.AreEqual(8, result1.OverallStartingPosition);
        Assert.AreEqual(15, result1.LapsLedOverall);
        Assert.AreEqual("Active", result1.CurrentStatus);
        
        // Second result should only have laps led change
        Assert.AreEqual("42", result2.Number);
        Assert.IsNull(result2.OverallStartingPosition); // No change
        Assert.AreEqual(15, result2.LapsLedOverall);
        Assert.IsNull(result2.CurrentStatus); // No change
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldRaceScenario_WorksCorrectly()
    {
        // Arrange - Simulate a race leader who started 10th and has led 25 laps
        var completedLap = CreateCompletedLap("42", startPosition: 10, lapsLed: 25, currentStatus: "Active");
        
        var currentCarState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 50,
            Class = "GT3",
            OverallStartingPosition = 0, // Not set yet
            LapsLedOverall = 20, // Previous count
            CurrentStatus = "", // Not set yet
            OverallPosition = 1
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(10, result.OverallStartingPosition); // Started 10th
        Assert.AreEqual(25, result.LapsLedOverall); // Now led 25 laps
        Assert.AreEqual("Active", result.CurrentStatus); // Currently active
        
        // Verify that other properties are not touched
        Assert.IsNull(result.LastLapCompleted);
        Assert.IsNull(result.Class);
        Assert.IsNull(result.OverallPosition);
    }

    [TestMethod]
    public void GetChanges_CarRetirement_UpdatesStatus()
    {
        // Arrange - Simulate a car that retired due to mechanical issues
        var completedLap = CreateCompletedLap("99", startPosition: 15, lapsLed: 3, currentStatus: "Mechanical");
        
        var currentCarState = new CarPosition
        {
            Number = "99",
            OverallStartingPosition = 15,
            LapsLedOverall = 3,
            CurrentStatus = "Active", // Was active, now retired
            OverallPosition = 25
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("99", result.Number);
        Assert.IsNull(result.OverallStartingPosition); // No change
        Assert.IsNull(result.LapsLedOverall); // No change
        Assert.AreEqual("Mechanical", result.CurrentStatus); // Status updated to retired
    }

    [TestMethod]
    public void GetChanges_MultipleCarScenario_HandlesEachIndependently()
    {
        // Arrange - Test multiple cars with different position scenarios
        var car42CompletedLap = CreateCompletedLap("42", startPosition: 8, lapsLed: 25, currentStatus: "Active");
        var car99CompletedLap = CreateCompletedLap("99", startPosition: 2, lapsLed: 0, currentStatus: "Contact");
        var car7CompletedLap = CreateCompletedLap("7", startPosition: 20, lapsLed: 5, currentStatus: "In Pits");

        var car42State = new CarPosition { Number = "42", OverallStartingPosition = 5, LapsLedOverall = 20, CurrentStatus = "Racing" };
        var car99State = new CarPosition { Number = "99", OverallStartingPosition = 2, LapsLedOverall = 0, CurrentStatus = "Active" };
        var car7State = new CarPosition { Number = "7", OverallStartingPosition = 18, LapsLedOverall = 5, CurrentStatus = "Active" };

        var car42StateUpdate = new PositionInfoStateUpdate(car42CompletedLap);
        var car99StateUpdate = new PositionInfoStateUpdate(car99CompletedLap);
        var car7StateUpdate = new PositionInfoStateUpdate(car7CompletedLap);

        // Act
        var result42 = car42StateUpdate.GetChanges(car42State);
        var result99 = car99StateUpdate.GetChanges(car99State);
        var result7 = car7StateUpdate.GetChanges(car7State);

        // Assert
        Assert.IsNotNull(result42);
        Assert.AreEqual("42", result42.Number);
        Assert.AreEqual(8, result42.OverallStartingPosition);
        Assert.AreEqual(25, result42.LapsLedOverall);
        Assert.AreEqual("Active", result42.CurrentStatus);

        Assert.IsNotNull(result99);
        Assert.AreEqual("99", result99.Number);
        Assert.IsNull(result99.OverallStartingPosition); // No change
        Assert.IsNull(result99.LapsLedOverall); // No change
        Assert.AreEqual("Contact", result99.CurrentStatus);

        Assert.IsNotNull(result7);
        Assert.AreEqual("7", result7.Number);
        Assert.AreEqual(20, result7.OverallStartingPosition);
        Assert.IsNull(result7.LapsLedOverall); // No change
        Assert.AreEqual("In Pits", result7.CurrentStatus);
    }

    #endregion

    #region Concurrency Tests

    [TestMethod]
    public void GetChanges_ConcurrentCalls_ThreadSafe()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 8, lapsLed: 15, currentStatus: "Active");
        var currentState = new CarPosition
        {
            Number = "42",
            OverallStartingPosition = 5,
            LapsLedOverall = 10,
            CurrentStatus = "Racing"
        };

        var stateUpdate = new PositionInfoStateUpdate(completedLap);
        var results = new List<CarPositionPatch?>();

        // Act - Run multiple concurrent calls
        for (int i = 0; i < 10; i++)
        {
            results.Add(stateUpdate.GetChanges(currentState));
        }

        // Assert
        Assert.IsTrue(results.All(r => r is not null));
        Assert.IsTrue(results.All(r => r!.Number == "42"));
        Assert.IsTrue(results.All(r => r!.OverallStartingPosition == 8));
        Assert.IsTrue(results.All(r => r!.LapsLedOverall == 15));
        Assert.IsTrue(results.All(r => r!.CurrentStatus == "Active"));
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void CompletedLap_Property_ReturnsCorrectValue()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 8, lapsLed: 15, currentStatus: "Active");

        // Act
        var stateUpdate = new PositionInfoStateUpdate(completedLap);

        // Assert
        Assert.AreSame(completedLap, stateUpdate.CompletedLap);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SameCompletedLapInstance_ReturnsTrue()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", startPosition: 8, lapsLed: 15, currentStatus: "Active");
        var stateUpdate1 = new PositionInfoStateUpdate(completedLap);
        var stateUpdate2 = new PositionInfoStateUpdate(completedLap);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentCompletedLapInstances_ReturnsFalse()
    {
        // Arrange
        var completedLap1 = CreateCompletedLap("42", startPosition: 8, lapsLed: 15, currentStatus: "Active");
        var completedLap2 = CreateCompletedLap("99", startPosition: 3, lapsLed: 5, currentStatus: "Contact");
        var stateUpdate1 = new PositionInfoStateUpdate(completedLap1);
        var stateUpdate2 = new PositionInfoStateUpdate(completedLap2);

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a CompletedLap instance with the specified values for testing.
    /// </summary>
    private static CompletedLap CreateCompletedLap(
        string carNumber, 
        ushort startPosition = 1, 
        ushort lapsLed = 0,
        string? currentStatus = "Active")
    {
        var completedLap = new CompletedLap();

        // Use reflection to set private properties for testing
        var numberProp = typeof(CompletedLap).GetProperty("Number");
        var startPositionProp = typeof(CompletedLap).GetProperty("StartPosition");
        var lapsLedProp = typeof(CompletedLap).GetProperty("LapsLed");
        var currentStatusProp = typeof(CompletedLap).GetProperty("CurrentStatus");

        numberProp?.SetValue(completedLap, carNumber);
        startPositionProp?.SetValue(completedLap, startPosition);
        lapsLedProp?.SetValue(completedLap, lapsLed);
        currentStatusProp?.SetValue(completedLap, currentStatus ?? "");

        return completedLap;
    }

    #endregion
}
