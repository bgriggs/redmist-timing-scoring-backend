using RedMist.EventProcessor.EventStatus.RMonitor;
using RedMist.EventProcessor.EventStatus.RMonitor.StateChanges;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.RMonitor.StateChanges;

[TestClass]
public class CarBestLapStateUpdateTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidPracticeQualifying_CreatesInstance()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 5, bestLapTime: "01:45.123");

        // Act
        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(practiceQualifying, stateUpdate.PracticeQualifying);
    }

    #endregion

    #region GetChanges Tests - Both Properties Changed

    [TestMethod]
    public void GetChanges_BothBestLapAndBestTimeChanged_ReturnsCompletePatch()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 10, bestLapTime: "01:42.500");
        var currentState = new CarPosition
        {
            Number = "42",
            BestLap = 8, // Different
            BestTime = "01:45.123" // Different
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(10, result.BestLap);
        Assert.AreEqual("01:42.500", result.BestTime);
    }

    [TestMethod]
    public void GetChanges_NoChanges_ReturnsEmptyPatch()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 8, bestLapTime: "01:45.123");
        var currentState = new CarPosition
        {
            Number = "42",
            BestLap = 8, // Same
            BestTime = "01:45.123" // Same
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.BestLap);
        Assert.IsNull(result.BestTime);
    }

    #endregion

    #region GetChanges Tests - Individual Property Changes

    [TestMethod]
    public void GetChanges_OnlyBestLapChanged_ReturnsPartialPatch()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 12, bestLapTime: "01:45.123");
        var currentState = new CarPosition
        {
            Number = "42",
            BestLap = 10, // Different
            BestTime = "01:45.123" // Same
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(12, result.BestLap); // Should be set
        Assert.IsNull(result.BestTime); // Should not be set
    }

    [TestMethod]
    public void GetChanges_OnlyBestTimeChanged_ReturnsPartialPatch()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 8, bestLapTime: "01:43.456");
        var currentState = new CarPosition
        {
            Number = "42",
            BestLap = 8, // Same
            BestTime = "01:45.123" // Different
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.BestLap); // Should not be set
        Assert.AreEqual("01:43.456", result.BestTime); // Should be set
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_ZeroValues_HandlesCorrectly()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 0, bestLapTime: "");
        var currentState = new CarPosition
        {
            Number = "42",
            BestLap = 5,
            BestTime = "01:45.123"
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.BestLap);
        Assert.AreEqual("", result.BestTime);
    }

    [TestMethod]
    public void GetChanges_NullCarPositionValues_HandlesCorrectly()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 8, bestLapTime: "01:45.123");
        var currentState = new CarPosition
        {
            Number = "42",
            BestLap = 0, // Default value for int
            BestTime = null
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(8, result.BestLap);
        Assert.AreEqual("01:45.123", result.BestTime);
    }

    [TestMethod]
    public void GetChanges_LargeLapNumbers_HandlesCorrectly()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 999, bestLapTime: "01:30.000");
        var currentState = new CarPosition
        {
            Number = "42",
            BestLap = 100,
            BestTime = "01:45.123"
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(999, result.BestLap);
        Assert.AreEqual("01:30.000", result.BestTime);
    }

    #endregion

    #region GetChanges Tests - Car Number Handling

    [TestMethod]
    public void GetChanges_CarNumber_NotCopiedToPatch()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 10, bestLapTime: "01:42.500");
        var currentState = new CarPosition
        {
            Number = "42",
            BestLap = 8,
            BestTime = "01:45.123"
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.Number); // Number should not be set in the patch
        Assert.AreEqual(10, result.BestLap);
        Assert.AreEqual("01:42.500", result.BestTime);
    }

    #endregion

    #region GetChanges Tests - Time Format Variations

    [TestMethod]
    public void GetChanges_DifferentTimeFormats_HandlesCorrectly()
    {
        var timeTestCases = new[]
        {
            "01:45.123",
            "02:15.456",
            "00:59.999",
            "03:00.000",
            "01:23.45", // Shorter format
            "1:45.123"  // No leading zero
        };

        foreach (var timeFormat in timeTestCases)
        {
            // Arrange
            var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 5, bestLapTime: timeFormat);
            var currentState = new CarPosition
            {
                Number = "42",
                BestLap = 5,
                BestTime = "91:45.123" // Different time
            };

            var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for time format: {timeFormat}");
            Assert.AreEqual(timeFormat, result.BestTime, $"Best time should match for format: {timeFormat}");
        }
    }

    #endregion

    #region GetChanges Tests - Multiple Sequential Calls

    [TestMethod]
    public void GetChanges_MultipleCallsWithSameState_ConsistentResults()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 10, bestLapTime: "01:42.500");
        var currentState = new CarPosition
        {
            Number = "42",
            BestLap = 8,
            BestTime = "01:45.123"
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result1 = stateUpdate.GetChanges(currentState);
        var result2 = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.BestLap, result2.BestLap);
        Assert.AreEqual(result1.BestTime, result2.BestTime);
    }

    [TestMethod]
    public void GetChanges_DifferentStatesSequentially_ReturnsCorrectPatches()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 10, bestLapTime: "01:42.500");

        var state1 = new CarPosition
        {
            Number = "42",
            BestLap = 8, // Different from PracticeQualifying
            BestTime = "01:45.123" // Different from PracticeQualifying
        };

        var state2 = new CarPosition
        {
            Number = "42",
            BestLap = 10, // Same as PracticeQualifying
            BestTime = "01:45.123" // Different from PracticeQualifying
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result1 = stateUpdate.GetChanges(state1);
        var result2 = stateUpdate.GetChanges(state2);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        
        // First result should have both changes
        Assert.AreEqual(10, result1.BestLap);
        Assert.AreEqual("01:42.500", result1.BestTime);
        
        // Second result should only have best time change
        Assert.IsNull(result2.BestLap); // No change
        Assert.AreEqual("01:42.500", result2.BestTime);
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldQualifyingScenario_WorksCorrectly()
    {
        // Arrange - Simulate a qualifying session where a car sets a new best lap
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 15, bestLapTime: "01:38.426");
        
        var currentCarState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 15,
            Class = "GT3",
            BestLap = 12, // Previous best lap
            BestTime = "01:39.854", // Previous best time
            LastLapTime = "01:38.426"
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(15, result.BestLap); // Updated to current lap
        Assert.AreEqual("01:38.426", result.BestTime); // New best time
        
        // Verify that other properties are not touched
        Assert.IsNull(result.LastLapCompleted);
        Assert.IsNull(result.Class);
        Assert.IsNull(result.LastLapTime);
    }

    [TestMethod]
    public void GetChanges_PracticeSessionImprovement_UpdatesCorrectly()
    {
        // Arrange - Simulate practice session with gradual improvement
        var practiceQualifying = CreatePracticeQualifying(position: 3, bestLap: 25, bestLapTime: "01:41.125");
        
        var currentCarState = new CarPosition
        {
            Number = "99",
            LastLapCompleted = 25,
            BestLap = 20, // Earlier lap
            BestTime = "01:42.789", // Slower time
            Class = "GTE"
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(25, result.BestLap);
        Assert.AreEqual("01:41.125", result.BestTime);
    }

    [TestMethod]
    public void GetChanges_MultipleCarScenario_HandlesEachIndependently()
    {
        // Arrange - Test multiple cars with different qualifying scenarios
        var car42PQ = CreatePracticeQualifying(position: 1, bestLap: 10, bestLapTime: "01:38.500");
        var car99PQ = CreatePracticeQualifying(position: 2, bestLap: 8, bestLapTime: "01:39.200");
        var car7PQ = CreatePracticeQualifying(position: 3, bestLap: 15, bestLapTime: "01:40.100");

        var car42State = new CarPosition { Number = "42", BestLap = 8, BestTime = "01:39.000" };
        var car99State = new CarPosition { Number = "99", BestLap = 8, BestTime = "01:39.200" }; // Same time
        var car7State = new CarPosition { Number = "7", BestLap = 12, BestTime = "01:41.500" };

        var car42StateUpdate = new CarBestLapStateUpdate(car42PQ);
        var car99StateUpdate = new CarBestLapStateUpdate(car99PQ);
        var car7StateUpdate = new CarBestLapStateUpdate(car7PQ);

        // Act
        var result42 = car42StateUpdate.GetChanges(car42State);
        var result99 = car99StateUpdate.GetChanges(car99State);
        var result7 = car7StateUpdate.GetChanges(car7State);

        // Assert
        Assert.IsNotNull(result42);
        Assert.AreEqual(10, result42.BestLap);
        Assert.AreEqual("01:38.500", result42.BestTime);

        Assert.IsNotNull(result99);
        Assert.IsNull(result99.BestLap); // No change
        Assert.IsNull(result99.BestTime); // No change

        Assert.IsNotNull(result7);
        Assert.AreEqual(15, result7.BestLap);
        Assert.AreEqual("01:40.100", result7.BestTime);
    }

    [TestMethod]
    public void GetChanges_SessionProgression_TracksImprovements()
    {
        // Arrange - Simulate session progression with improving times
        var sessionProgression = new[]
        {
            (Lap: 3, Time: "01:45.000", Description: "Initial lap"),
            (Lap: 7, Time: "01:43.500", Description: "First improvement"),
            (Lap: 12, Time: "01:42.100", Description: "Second improvement"),
            (Lap: 18, Time: "01:41.850", Description: "Final best")
        };

        var currentState = new CarPosition
        {
            Number = "42",
            BestLap = 0,
            BestTime = ""
        };

        foreach (var session in sessionProgression)
        {
            // Arrange
            var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: session.Lap, bestLapTime: session.Time);
            var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for: {session.Description}");
            Assert.AreEqual(session.Lap, result.BestLap, $"Best lap should match for: {session.Description}");
            Assert.AreEqual(session.Time, result.BestTime, $"Best time should match for: {session.Description}");

            // Update current state for next iteration
            currentState.BestLap = session.Lap;
            currentState.BestTime = session.Time;
        }
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void PracticeQualifying_Property_ReturnsCorrectValue()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 5, bestLapTime: "01:45.123");

        // Act
        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Assert
        Assert.AreSame(practiceQualifying, stateUpdate.PracticeQualifying);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SamePracticeQualifyingInstance_ReturnsTrue()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 5, bestLapTime: "01:45.123");
        var stateUpdate1 = new CarBestLapStateUpdate(practiceQualifying);
        var stateUpdate2 = new CarBestLapStateUpdate(practiceQualifying);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentPracticeQualifyingInstances_ReturnsFalse()
    {
        // Arrange
        var practiceQualifying1 = CreatePracticeQualifying(position: 1, bestLap: 5, bestLapTime: "01:45.123");
        var practiceQualifying2 = CreatePracticeQualifying(position: 2, bestLap: 8, bestLapTime: "01:47.456");
        var stateUpdate1 = new CarBestLapStateUpdate(practiceQualifying1);
        var stateUpdate2 = new CarBestLapStateUpdate(practiceQualifying2);

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    #endregion

    #region Negative Test Cases

    [TestMethod]
    public void GetChanges_NegativeLapNumber_HandlesCorrectly()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: -1, bestLapTime: "01:45.123");
        var currentState = new CarPosition
        {
            Number = "42",
            BestLap = 5,
            BestTime = "01:45.123"
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(-1, result.BestLap);
        Assert.IsNull(result.BestTime); // No change for time
    }

    [TestMethod]
    public void GetChanges_EmptyBestTime_HandlesCorrectly()
    {
        // Arrange
        var practiceQualifying = CreatePracticeQualifying(position: 1, bestLap: 5, bestLapTime: "");
        var currentState = new CarPosition
        {
            Number = "42",
            BestLap = 5,
            BestTime = "01:45.123"
        };

        var stateUpdate = new CarBestLapStateUpdate(practiceQualifying);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.BestLap); // No change for lap
        Assert.AreEqual("", result.BestTime);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a PracticeQualifying instance with the specified values for testing.
    /// </summary>
    private static PracticeQualifying CreatePracticeQualifying(
        int position = 1, 
        int bestLap = 0, 
        string bestLapTime = "")
    {
        var practiceQualifying = new PracticeQualifying();

        // Use reflection to set properties for testing
        var positionProp = typeof(PracticeQualifying).GetProperty("Position");
        var bestLapProp = typeof(PracticeQualifying).GetProperty("BestLap");
        var bestLapTimeProp = typeof(PracticeQualifying).GetProperty("BestLapTime");

        positionProp?.SetValue(practiceQualifying, position);
        bestLapProp?.SetValue(practiceQualifying, bestLap);
        bestLapTimeProp?.SetValue(practiceQualifying, bestLapTime);

        return practiceQualifying;
    }

    #endregion
}
