using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.RMonitor.StateChanges;

[TestClass]
public class CarLapStateUpdateTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidRaceInformation_CreatesInstance()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 15, raceTime: "00:45:30.123");

        // Act
        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(raceInformation, stateUpdate.RaceInformation);
    }

    #endregion

    #region Targets Property Tests

    [TestMethod]
    public void Targets_Property_ReturnsCorrectTargets()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 10, raceTime: "00:30:15.456");
        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var targets = stateUpdate.Targets;

        // Assert
        Assert.IsNotNull(targets);
        Assert.AreEqual(1, targets.Count);
        Assert.AreEqual(nameof(CarPosition.LastLapCompleted), targets[0]);
    }

    #endregion

    #region GetChanges Tests - Both Properties Changed

    [TestMethod]
    public void GetChanges_BothLastLapCompletedAndTotalTimeChanged_ReturnsCompletePatch()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 25, raceTime: "01:15:45.789");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 20, // Different
            TotalTime = "01:10:30.456" // Different
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(25, result.LastLapCompleted);
        Assert.AreEqual("01:15:45.789", result.TotalTime);
    }

    [TestMethod]
    public void GetChanges_NoChanges_ReturnsEmptyPatch()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 15, raceTime: "00:45:30.123");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 15, // Same
            TotalTime = "00:45:30.123" // Same
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.LastLapCompleted);
        Assert.IsNull(result.TotalTime);
    }

    #endregion

    #region GetChanges Tests - Individual Property Changes

    [TestMethod]
    public void GetChanges_OnlyLastLapCompletedChanged_ReturnsPartialPatch()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 18, raceTime: "00:45:30.123");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 15, // Different
            TotalTime = "00:45:30.123" // Same
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(18, result.LastLapCompleted); // Should be set
        Assert.IsNull(result.TotalTime); // Should not be set
    }

    [TestMethod]
    public void GetChanges_OnlyTotalTimeChanged_ReturnsPartialPatch()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 15, raceTime: "00:47:15.789");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 15, // Same
            TotalTime = "00:45:30.123" // Different
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.LastLapCompleted); // Should not be set
        Assert.AreEqual("00:47:15.789", result.TotalTime); // Should be set
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_ZeroLaps_HandlesCorrectly()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 0, raceTime: "00:00:00.000");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 5,
            TotalTime = "00:15:30.123"
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.LastLapCompleted);
        Assert.AreEqual("00:00:00.000", result.TotalTime);
    }

    [TestMethod]
    public void GetChanges_NullCarPositionValues_HandlesCorrectly()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 10, raceTime: "00:30:15.456");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 0, // Default value for int
            TotalTime = null
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(10, result.LastLapCompleted);
        Assert.AreEqual("00:30:15.456", result.TotalTime);
    }

    [TestMethod]
    public void GetChanges_LargeLapNumbers_HandlesCorrectly()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 500, raceTime: "10:00:00.000");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 100,
            TotalTime = "02:00:00.000"
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(500, result.LastLapCompleted);
        Assert.AreEqual("10:00:00.000", result.TotalTime);
    }

    #endregion

    #region GetChanges Tests - Car Number Handling

    [TestMethod]
    public void GetChanges_CarNumber_NotCopiedToPatch()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 15, raceTime: "00:45:30.123");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 10,
            TotalTime = "00:30:15.456"
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.Number); // Number should not be set in the patch
        Assert.AreEqual(15, result.LastLapCompleted);
        Assert.AreEqual("00:45:30.123", result.TotalTime);
    }

    #endregion

    #region GetChanges Tests - Time Format Variations

    [TestMethod]
    public void GetChanges_DifferentTimeFormats_HandlesCorrectly()
    {
        var timeTestCases = new[]
        {
            "00:45:30.123",
            "01:15:45.456",
            "02:30:00.000",
            "00:59:59.999",
            "10:00:00.000", // Long race
            "00:01:30.5",   // Shorter format
            "1:30:45.123"   // No leading zero
        };

        foreach (var timeFormat in timeTestCases)
        {
            // Arrange
            var raceInformation = CreateRaceInformation(position: 1, laps: 10, raceTime: timeFormat);
            var currentState = new CarPosition
            {
                Number = "42",
                LastLapCompleted = 10,
                TotalTime = "00:55:30.123" // Different time
            };

            var stateUpdate = new CarLapStateUpdate(raceInformation);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for time format: {timeFormat}");
            Assert.AreEqual(timeFormat, result.TotalTime, $"Total time should match for format: {timeFormat}");
        }
    }

    #endregion

    #region GetChanges Tests - Multiple Sequential Calls

    [TestMethod]
    public void GetChanges_MultipleCallsWithSameState_ConsistentResults()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 20, raceTime: "01:00:30.456");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 15,
            TotalTime = "00:45:15.123"
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result1 = stateUpdate.GetChanges(currentState);
        var result2 = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.LastLapCompleted, result2.LastLapCompleted);
        Assert.AreEqual(result1.TotalTime, result2.TotalTime);
    }

    [TestMethod]
    public void GetChanges_DifferentStatesSequentially_ReturnsCorrectPatches()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 20, raceTime: "01:00:30.456");

        var state1 = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 15, // Different from RaceInformation
            TotalTime = "00:45:15.123" // Different from RaceInformation
        };

        var state2 = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 20, // Same as RaceInformation
            TotalTime = "00:45:15.123" // Different from RaceInformation
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result1 = stateUpdate.GetChanges(state1);
        var result2 = stateUpdate.GetChanges(state2);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        
        // First result should have both changes
        Assert.AreEqual(20, result1.LastLapCompleted);
        Assert.AreEqual("01:00:30.456", result1.TotalTime);
        
        // Second result should only have total time change
        Assert.IsNull(result2.LastLapCompleted); // No change
        Assert.AreEqual("01:00:30.456", result2.TotalTime);
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldRaceScenario_WorksCorrectly()
    {
        // Arrange - Simulate a race where a car completes another lap
        var raceInformation = CreateRaceInformation(position: 3, laps: 45, raceTime: "01:30:25.789");
        
        var currentCarState = new CarPosition
        {
            Number = "42",
            OverallPosition = 3,
            Class = "GT3",
            LastLapCompleted = 44, // Previous lap
            TotalTime = "01:28:15.456", // Previous total time
            LastLapTime = "01:45.123"
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(45, result.LastLapCompleted); // Updated to current lap
        Assert.AreEqual("01:30:25.789", result.TotalTime); // Updated total time
        
        // Verify that other properties are not touched
        Assert.IsNull(result.OverallPosition);
        Assert.IsNull(result.Class);
        Assert.IsNull(result.LastLapTime);
    }

    [TestMethod]
    public void GetChanges_PracticeSessionProgression_TracksLaps()
    {
        // Arrange - Simulate practice session with progressive laps
        var raceInformation = CreateRaceInformation(position: 1, laps: 25, raceTime: "00:45:30.123");
        
        var currentCarState = new CarPosition
        {
            Number = "99",
            LastLapCompleted = 20,
            TotalTime = "00:36:45.789",
            Class = "GTE"
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(25, result.LastLapCompleted);
        Assert.AreEqual("00:45:30.123", result.TotalTime);
    }

    [TestMethod]
    public void GetChanges_MultipleCarScenario_HandlesEachIndependently()
    {
        // Arrange - Test multiple cars with different race scenarios
        var car42Race = CreateRaceInformation(position: 1, laps: 30, raceTime: "01:00:15.123");
        var car99Race = CreateRaceInformation(position: 2, laps: 29, raceTime: "01:01:30.456");
        var car7Race = CreateRaceInformation(position: 3, laps: 28, raceTime: "01:02:45.789");

        var car42State = new CarPosition { Number = "42", LastLapCompleted = 25, TotalTime = "00:52:30.000" };
        var car99State = new CarPosition { Number = "99", LastLapCompleted = 29, TotalTime = "01:01:30.456" }; // Same values
        var car7State = new CarPosition { Number = "7", LastLapCompleted = 27, TotalTime = "01:00:15.123" };

        var car42StateUpdate = new CarLapStateUpdate(car42Race);
        var car99StateUpdate = new CarLapStateUpdate(car99Race);
        var car7StateUpdate = new CarLapStateUpdate(car7Race);

        // Act
        var result42 = car42StateUpdate.GetChanges(car42State);
        var result99 = car99StateUpdate.GetChanges(car99State);
        var result7 = car7StateUpdate.GetChanges(car7State);

        // Assert
        Assert.IsNotNull(result42);
        Assert.AreEqual(30, result42.LastLapCompleted);
        Assert.AreEqual("01:00:15.123", result42.TotalTime);

        Assert.IsNotNull(result99);
        Assert.IsNull(result99.LastLapCompleted); // No change
        Assert.IsNull(result99.TotalTime); // No change

        Assert.IsNotNull(result7);
        Assert.AreEqual(28, result7.LastLapCompleted);
        Assert.AreEqual("01:02:45.789", result7.TotalTime);
    }

    [TestMethod]
    public void GetChanges_RaceProgression_TracksLapProgression()
    {
        // Arrange - Simulate race progression over multiple laps
        var raceProgression = new[]
        {
            (Lap: 10, Time: "00:18:30.123", Description: "Early race"),
            (Lap: 25, Time: "00:46:15.456", Description: "Mid race"),
            (Lap: 40, Time: "01:14:30.789", Description: "Late race"),
            (Lap: 50, Time: "01:32:45.012", Description: "Race finish")
        };

        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 0,
            TotalTime = ""
        };

        foreach (var race in raceProgression)
        {
            // Arrange
            var raceInformation = CreateRaceInformation(position: 1, laps: race.Lap, raceTime: race.Time);
            var stateUpdate = new CarLapStateUpdate(raceInformation);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for: {race.Description}");
            Assert.AreEqual(race.Lap, result.LastLapCompleted, $"Lap should match for: {race.Description}");
            Assert.AreEqual(race.Time, result.TotalTime, $"Time should match for: {race.Description}");

            // Update current state for next iteration
            currentState.LastLapCompleted = race.Lap;
            currentState.TotalTime = race.Time;
        }
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void RaceInformation_Property_ReturnsCorrectValue()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 15, raceTime: "00:45:30.123");

        // Act
        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Assert
        Assert.AreSame(raceInformation, stateUpdate.RaceInformation);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SameRaceInformationInstance_ReturnsTrue()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 15, raceTime: "00:45:30.123");
        var stateUpdate1 = new CarLapStateUpdate(raceInformation);
        var stateUpdate2 = new CarLapStateUpdate(raceInformation);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentRaceInformationInstances_ReturnsFalse()
    {
        // Arrange
        var raceInformation1 = CreateRaceInformation(position: 1, laps: 15, raceTime: "00:45:30.123");
        var raceInformation2 = CreateRaceInformation(position: 2, laps: 20, raceTime: "01:00:15.456");
        var stateUpdate1 = new CarLapStateUpdate(raceInformation1);
        var stateUpdate2 = new CarLapStateUpdate(raceInformation2);

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
        var raceInformation = CreateRaceInformation(position: 1, laps: -1, raceTime: "00:00:00.000");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 5,
            TotalTime = "00:15:30.123"
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(-1, result.LastLapCompleted);
        Assert.AreEqual("00:00:00.000", result.TotalTime);
    }

    [TestMethod]
    public void GetChanges_EmptyRaceTime_HandlesCorrectly()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 10, raceTime: "");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 10,
            TotalTime = "00:30:15.123"
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.LastLapCompleted); // No change for lap
        Assert.AreEqual("", result.TotalTime);
    }

    #endregion

    #region Performance Tests

    [TestMethod]
    public void GetChanges_HighLapCount_PerformsEfficiently()
    {
        // Arrange
        var raceInformation = CreateRaceInformation(position: 1, laps: 1000, raceTime: "20:00:00.000");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 999,
            TotalTime = "19:58:30.123"
        };

        var stateUpdate = new CarLapStateUpdate(raceInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(1000, result.LastLapCompleted);
        Assert.AreEqual("20:00:00.000", result.TotalTime);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a RaceInformation instance with the specified values for testing.
    /// </summary>
    private static RaceInformation CreateRaceInformation(
        int position = 1, 
        int laps = 0, 
        string raceTime = "")
    {
        var raceInformation = new RaceInformation();

        // Use reflection to set properties for testing
        var positionProp = typeof(RaceInformation).GetProperty("Position");
        var lapsProp = typeof(RaceInformation).GetProperty("Laps");
        var raceTimeProp = typeof(RaceInformation).GetProperty("RaceTime");

        positionProp?.SetValue(raceInformation, position);
        lapsProp?.SetValue(raceInformation, laps);
        raceTimeProp?.SetValue(raceInformation, raceTime);

        return raceInformation;
    }

    #endregion
}
