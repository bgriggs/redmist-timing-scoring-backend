using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.RMonitor.StateChanges;

[TestClass]
public class CarLapTimeStateUpdateTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidPassingInformation_CreatesInstance()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: "01:45.123", raceTime: "15:30.456");

        // Act
        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(passingInformation, stateUpdate.PassingInformation);
    }

    #endregion

    #region GetChanges Tests - Both Properties Changed

    [TestMethod]
    public void GetChanges_BothLastLapTimeAndTotalTimeChanged_ReturnsCompletePatch()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: "01:42.500", raceTime: "45:30.789");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapTime = "01:45.123", // Different
            TotalTime = "44:15.456" // Different
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("01:42.500", result.LastLapTime);
        Assert.AreEqual("45:30.789", result.TotalTime);
    }

    [TestMethod]
    public void GetChanges_NoChanges_ReturnsEmptyPatch()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: "01:45.123", raceTime: "30:15.456");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapTime = "01:45.123", // Same
            TotalTime = "30:15.456" // Same
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.LastLapTime);
        Assert.IsNull(result.TotalTime);
    }

    #endregion

    #region GetChanges Tests - Individual Property Changes

    [TestMethod]
    public void GetChanges_OnlyLastLapTimeChanged_ReturnsPartialPatch()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: "01:43.789", raceTime: "30:15.456");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapTime = "01:45.123", // Different
            TotalTime = "30:15.456" // Same
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("01:43.789", result.LastLapTime); // Should be set
        Assert.IsNull(result.TotalTime); // Should not be set
    }

    [TestMethod]
    public void GetChanges_OnlyTotalTimeChanged_ReturnsPartialPatch()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: "01:45.123", raceTime: "32:45.789");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapTime = "01:45.123", // Same
            TotalTime = "30:15.456" // Different
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.LastLapTime); // Should not be set
        Assert.AreEqual("32:45.789", result.TotalTime); // Should be set
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_EmptyTimes_HandlesCorrectly()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: "", raceTime: "");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapTime = "01:45.123",
            TotalTime = "30:15.456"
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("", result.LastLapTime);
        Assert.AreEqual("", result.TotalTime);
    }

    [TestMethod]
    public void GetChanges_NullCarPositionValues_HandlesCorrectly()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: "01:45.123", raceTime: "30:15.456");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapTime = null,
            TotalTime = null
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("01:45.123", result.LastLapTime);
        Assert.AreEqual("30:15.456", result.TotalTime);
    }

    #endregion

    #region GetChanges Tests - Car Number Handling

    [TestMethod]
    public void GetChanges_CarNumber_NotCopiedToPatch()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: "01:42.500", raceTime: "45:30.789");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapTime = "01:45.123",
            TotalTime = "44:15.456"
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.Number); // Number should not be set in the patch
        Assert.AreEqual("01:42.500", result.LastLapTime);
        Assert.AreEqual("45:30.789", result.TotalTime);
    }

    #endregion

    #region GetChanges Tests - Time Format Variations

    [TestMethod]
    public void GetChanges_DifferentLapTimeFormats_HandlesCorrectly()
    {
        var lapTimeTestCases = new[]
        {
            "01:45.123",
            "02:15.456",
            "00:59.999",
            "03:00.000",
            "01:23.45",   // Shorter format
            "1:45.123",   // No leading zero in minutes
            "59.999"      // Seconds only
        };

        foreach (var timeFormat in lapTimeTestCases)
        {
            // Arrange
            var passingInformation = CreatePassingInformation(lapTime: timeFormat, raceTime: "30:15.456");
            var currentState = new CarPosition
            {
                Number = "42",
                LastLapTime = "11:45.123", // Different time
                TotalTime = "30:15.456" // Same
            };

            var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for lap time format: {timeFormat}");
            Assert.AreEqual(timeFormat, result.LastLapTime, $"Lap time should match for format: {timeFormat}");
        }
    }

    [TestMethod]
    public void GetChanges_DifferentRaceTimeFormats_HandlesCorrectly()
    {
        var raceTimeTestCases = new[]
        {
            "30:15.456",
            "01:45:30.123",
            "02:00:00.000",
            "00:59:59.999",
            "10:30:45.789", // Long race
            "45:30.5",      // Shorter format
            "1:30:45.123"   // No leading zero in hours
        };

        foreach (var timeFormat in raceTimeTestCases)
        {
            // Arrange
            var passingInformation = CreatePassingInformation(lapTime: "01:45.123", raceTime: timeFormat);
            var currentState = new CarPosition
            {
                Number = "42",
                LastLapTime = "01:45.123", // Same
                TotalTime = "33:15.456" // Different time
            };

            var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for race time format: {timeFormat}");
            Assert.AreEqual(timeFormat, result.TotalTime, $"Race time should match for format: {timeFormat}");
        }
    }

    #endregion

    #region GetChanges Tests - Multiple Sequential Calls

    [TestMethod]
    public void GetChanges_MultipleCallsWithSameState_ConsistentResults()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: "01:42.500", raceTime: "45:30.789");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapTime = "01:45.123",
            TotalTime = "44:15.456"
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result1 = stateUpdate.GetChanges(currentState);
        var result2 = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.LastLapTime, result2.LastLapTime);
        Assert.AreEqual(result1.TotalTime, result2.TotalTime);
    }

    [TestMethod]
    public void GetChanges_DifferentStatesSequentially_ReturnsCorrectPatches()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: "01:42.500", raceTime: "45:30.789");

        var state1 = new CarPosition
        {
            Number = "42",
            LastLapTime = "01:45.123", // Different from PassingInformation
            TotalTime = "44:15.456" // Different from PassingInformation
        };

        var state2 = new CarPosition
        {
            Number = "42",
            LastLapTime = "01:42.500", // Same as PassingInformation
            TotalTime = "44:15.456" // Different from PassingInformation
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result1 = stateUpdate.GetChanges(state1);
        var result2 = stateUpdate.GetChanges(state2);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        
        // First result should have both changes
        Assert.AreEqual("01:42.500", result1.LastLapTime);
        Assert.AreEqual("45:30.789", result1.TotalTime);
        
        // Second result should only have total time change
        Assert.IsNull(result2.LastLapTime); // No change
        Assert.AreEqual("45:30.789", result2.TotalTime);
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldRaceScenario_WorksCorrectly()
    {
        // Arrange - Simulate a race passing where a car sets a new lap time
        var passingInformation = CreatePassingInformation(lapTime: "01:38.426", raceTime: "01:15:42.789");
        
        var currentCarState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 25,
            Class = "GT3",
            LastLapTime = "01:39.854", // Previous lap time
            TotalTime = "01:14:04.363", // Previous total time
            OverallPosition = 3
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("01:38.426", result.LastLapTime); // New faster lap time
        Assert.AreEqual("01:15:42.789", result.TotalTime); // Updated total time
        
        // Verify that other properties are not touched
        Assert.IsNull(result.LastLapCompleted);
        Assert.IsNull(result.Class);
        Assert.IsNull(result.OverallPosition);
    }

    [TestMethod]
    public void GetChanges_QualifyingScenario_UpdatesCorrectly()
    {
        // Arrange - Simulate qualifying session with improving lap times
        var passingInformation = CreatePassingInformation(lapTime: "01:41.125", raceTime: "12:30.456");
        
        var currentCarState = new CarPosition
        {
            Number = "99",
            LastLapCompleted = 8,
            LastLapTime = "01:42.789", // Slower previous time
            TotalTime = "11:45.331", // Previous session time
            Class = "GTE"
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("01:41.125", result.LastLapTime); // Improved lap time
        Assert.AreEqual("12:30.456", result.TotalTime); // Updated session time
    }

    [TestMethod]
    public void GetChanges_MultipleCarScenario_HandlesEachIndependently()
    {
        // Arrange - Test multiple cars with different passing scenarios
        var car42Passing = CreatePassingInformation(lapTime: "01:38.500", raceTime: "30:15.123");
        var car99Passing = CreatePassingInformation(lapTime: "01:39.200", raceTime: "31:30.456");
        var car7Passing = CreatePassingInformation(lapTime: "01:40.100", raceTime: "32:45.789");

        var car42State = new CarPosition { Number = "42", LastLapTime = "01:39.000", TotalTime = "29:30.000" };
        var car99State = new CarPosition { Number = "99", LastLapTime = "01:39.200", TotalTime = "31:30.456" }; // Same values
        var car7State = new CarPosition { Number = "7", LastLapTime = "01:41.500", TotalTime = "32:00.123" };

        var car42StateUpdate = new CarLapTimeStateUpdate(car42Passing);
        var car99StateUpdate = new CarLapTimeStateUpdate(car99Passing);
        var car7StateUpdate = new CarLapTimeStateUpdate(car7Passing);

        // Act
        var result42 = car42StateUpdate.GetChanges(car42State);
        var result99 = car99StateUpdate.GetChanges(car99State);
        var result7 = car7StateUpdate.GetChanges(car7State);

        // Assert
        Assert.IsNotNull(result42);
        Assert.AreEqual("01:38.500", result42.LastLapTime);
        Assert.AreEqual("30:15.123", result42.TotalTime);

        Assert.IsNotNull(result99);
        Assert.IsNull(result99.LastLapTime); // No change
        Assert.IsNull(result99.TotalTime); // No change

        Assert.IsNotNull(result7);
        Assert.AreEqual("01:40.100", result7.LastLapTime);
        Assert.AreEqual("32:45.789", result7.TotalTime);
    }

    [TestMethod]
    public void GetChanges_SessionProgression_TracksTimeProgression()
    {
        // Arrange - Simulate session progression with evolving times
        var sessionProgression = new[]
        {
            (LapTime: "01:50.000", RaceTime: "01:50.000", Description: "First lap"),
            (LapTime: "01:47.500", RaceTime: "03:37.500", Description: "Second lap improvement"),
            (LapTime: "01:45.100", RaceTime: "05:22.600", Description: "Further improvement"),
            (LapTime: "01:43.850", RaceTime: "07:06.450", Description: "Personal best")
        };

        var currentState = new CarPosition
        {
            Number = "42",
            LastLapTime = "",
            TotalTime = ""
        };

        foreach (var session in sessionProgression)
        {
            // Arrange
            var passingInformation = CreatePassingInformation(lapTime: session.LapTime, raceTime: session.RaceTime);
            var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for: {session.Description}");
            Assert.AreEqual(session.LapTime, result.LastLapTime, $"Lap time should match for: {session.Description}");
            Assert.AreEqual(session.RaceTime, result.TotalTime, $"Race time should match for: {session.Description}");

            // Update current state for next iteration
            currentState.LastLapTime = session.LapTime;
            currentState.TotalTime = session.RaceTime;
        }
    }

    [TestMethod]
    public void GetChanges_EnduranceRaceScenario_HandlesLongTimes()
    {
        // Arrange - Simulate endurance race with long total times
        var passingInformation = CreatePassingInformation(lapTime: "01:45.123", raceTime: "04:30:15.456");
        
        var currentCarState = new CarPosition
        {
            Number = "24",
            LastLapCompleted = 200,
            LastLapTime = "01:46.789",
            TotalTime = "04:28:30.333",
            Class = "LMP1"
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("01:45.123", result.LastLapTime);
        Assert.AreEqual("04:30:15.456", result.TotalTime);
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void PassingInformation_Property_ReturnsCorrectValue()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: "01:45.123", raceTime: "30:15.456");

        // Act
        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Assert
        Assert.AreSame(passingInformation, stateUpdate.PassingInformation);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SamePassingInformationInstance_ReturnsTrue()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: "01:45.123", raceTime: "30:15.456");
        var stateUpdate1 = new CarLapTimeStateUpdate(passingInformation);
        var stateUpdate2 = new CarLapTimeStateUpdate(passingInformation);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentPassingInformationInstances_ReturnsFalse()
    {
        // Arrange
        var passingInformation1 = CreatePassingInformation(lapTime: "01:45.123", raceTime: "30:15.456");
        var passingInformation2 = CreatePassingInformation(lapTime: "01:47.456", raceTime: "32:30.789");
        var stateUpdate1 = new CarLapTimeStateUpdate(passingInformation1);
        var stateUpdate2 = new CarLapTimeStateUpdate(passingInformation2);

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    #endregion

    #region Negative Test Cases

    [TestMethod]
    public void GetChanges_InvalidTimeFormats_HandlesGracefully()
    {
        var invalidTimeFormats = new[]
        {
            "invalid",
            "99:99.999",
            "abc:def.ghi",
            ":",
            "."
        };

        foreach (var invalidFormat in invalidTimeFormats)
        {
            // Arrange
            var passingInformation = CreatePassingInformation(lapTime: invalidFormat, raceTime: "30:15.456");
            var currentState = new CarPosition
            {
                Number = "42",
                LastLapTime = "01:45.123",
                TotalTime = "30:15.456"
            };

            var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for invalid format: {invalidFormat}");
            Assert.AreEqual(invalidFormat, result.LastLapTime, $"Should handle invalid format gracefully: {invalidFormat}");
        }
    }

    [TestMethod]
    public void GetChanges_WhitespaceInTimes_HandlesCorrectly()
    {
        // Arrange
        var passingInformation = CreatePassingInformation(lapTime: " 01:45.123 ", raceTime: " 30:15.456 ");
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapTime = "01:45.123",
            TotalTime = "30:15.456"
        };

        var stateUpdate = new CarLapTimeStateUpdate(passingInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(" 01:45.123 ", result.LastLapTime); // Preserves whitespace
        Assert.AreEqual(" 30:15.456 ", result.TotalTime); // Preserves whitespace
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a PassingInformation instance with the specified values for testing.
    /// </summary>
    private static PassingInformation CreatePassingInformation(
        string lapTime = "", 
        string raceTime = "")
    {
        var passingInformation = new PassingInformation();

        // Use reflection to set properties for testing
        var lapTimeProp = typeof(PassingInformation).GetProperty("LapTime");
        var raceTimeProp = typeof(PassingInformation).GetProperty("RaceTime");

        lapTimeProp?.SetValue(passingInformation, lapTime);
        raceTimeProp?.SetValue(passingInformation, raceTime);

        return passingInformation;
    }

    #endregion
}
