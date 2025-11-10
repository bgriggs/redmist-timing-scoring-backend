using RedMist.EventProcessor.EventStatus.RMonitor;
using RedMist.EventProcessor.EventStatus.RMonitor.StateChanges;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.RMonitor.StateChanges;

[TestClass]
public class HeartbeatStateUpdateTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidHeartbeat_CreatesInstance()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(lapsToGo: 25, timeToGo: "00:15:30", timeOfDay: "14:30:45", raceTime: "01:45:15", flagStatus: "Green");

        // Act
        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(heartbeat, stateUpdate.Heartbeat);
    }

    #endregion

    #region GetChanges Tests - All Properties Changed

    [TestMethod]
    public void GetChanges_AllPropertiesChanged_ReturnsCompletePatch()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(
            lapsToGo: 25, 
            timeToGo: "00:15:30", 
            timeOfDay: "14:30:45", 
            raceTime: "01:45:15", 
            flagStatus: "Green");

        var currentState = new SessionState
        {
            LapsToGo = 20, // Different
            TimeToGo = "00:20:00", // Different
            LocalTimeOfDay = "14:25:30", // Different
            RunningRaceTime = "01:40:00", // Different
            CurrentFlag = Flags.Yellow // Different (Green flag will be converted)
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(25, result.LapsToGo);
        Assert.AreEqual("00:15:30", result.TimeToGo);
        Assert.AreEqual("14:30:45", result.LocalTimeOfDay);
        Assert.AreEqual("01:45:15", result.RunningRaceTime);
        Assert.AreEqual(Flags.Green, result.CurrentFlag);
    }

    [TestMethod]
    public void GetChanges_NoChanges_ReturnsEmptyPatch()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(
            lapsToGo: 25, 
            timeToGo: "00:15:30", 
            timeOfDay: "14:30:45", 
            raceTime: "01:45:15", 
            flagStatus: "Green");

        var currentState = new SessionState
        {
            LapsToGo = 25, // Same
            TimeToGo = "00:15:30", // Same
            LocalTimeOfDay = "14:30:45", // Same
            RunningRaceTime = "01:45:15", // Same
            CurrentFlag = Flags.Green // Same
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.LapsToGo);
        Assert.IsNull(result.TimeToGo);
        Assert.IsNull(result.LocalTimeOfDay);
        Assert.IsNull(result.RunningRaceTime);
        Assert.IsNull(result.CurrentFlag);
    }

    #endregion

    #region GetChanges Tests - Individual Property Changes

    [TestMethod]
    public void GetChanges_OnlyLapsToGoChanged_ReturnsPartialPatch()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(
            lapsToGo: 15, 
            timeToGo: "00:15:30", 
            timeOfDay: "14:30:45", 
            raceTime: "01:45:15", 
            flagStatus: "Green");

        var currentState = new SessionState
        {
            LapsToGo = 20, // Different
            TimeToGo = "00:15:30", // Same
            LocalTimeOfDay = "14:30:45", // Same
            RunningRaceTime = "01:45:15", // Same
            CurrentFlag = Flags.Green // Same
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(15, result.LapsToGo); // Should be set
        Assert.IsNull(result.TimeToGo); // Should not be set
        Assert.IsNull(result.LocalTimeOfDay); // Should not be set
        Assert.IsNull(result.RunningRaceTime); // Should not be set
        Assert.IsNull(result.CurrentFlag); // Should not be set
    }

    [TestMethod]
    public void GetChanges_OnlyTimeToGoChanged_ReturnsPartialPatch()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(
            lapsToGo: 25, 
            timeToGo: "00:12:45", 
            timeOfDay: "14:30:45", 
            raceTime: "01:45:15", 
            flagStatus: "Green");

        var currentState = new SessionState
        {
            LapsToGo = 25, // Same
            TimeToGo = "00:15:30", // Different
            LocalTimeOfDay = "14:30:45", // Same
            RunningRaceTime = "01:45:15", // Same
            CurrentFlag = Flags.Green // Same
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.LapsToGo); // Should not be set
        Assert.AreEqual("00:12:45", result.TimeToGo); // Should be set
        Assert.IsNull(result.LocalTimeOfDay); // Should not be set
        Assert.IsNull(result.RunningRaceTime); // Should not be set
        Assert.IsNull(result.CurrentFlag); // Should not be set
    }

    [TestMethod]
    public void GetChanges_OnlyTimeOfDayChanged_ReturnsPartialPatch()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(
            lapsToGo: 25, 
            timeToGo: "00:15:30", 
            timeOfDay: "14:35:00", 
            raceTime: "01:45:15", 
            flagStatus: "Green");

        var currentState = new SessionState
        {
            LapsToGo = 25, // Same
            TimeToGo = "00:15:30", // Same
            LocalTimeOfDay = "14:30:45", // Different
            RunningRaceTime = "01:45:15", // Same
            CurrentFlag = Flags.Green // Same
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.LapsToGo); // Should not be set
        Assert.IsNull(result.TimeToGo); // Should not be set
        Assert.AreEqual("14:35:00", result.LocalTimeOfDay); // Should be set
        Assert.IsNull(result.RunningRaceTime); // Should not be set
        Assert.IsNull(result.CurrentFlag); // Should not be set
    }

    [TestMethod]
    public void GetChanges_OnlyRaceTimeChanged_ReturnsPartialPatch()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(
            lapsToGo: 25, 
            timeToGo: "00:15:30", 
            timeOfDay: "14:30:45", 
            raceTime: "01:47:30", 
            flagStatus: "Green");

        var currentState = new SessionState
        {
            LapsToGo = 25, // Same
            TimeToGo = "00:15:30", // Same
            LocalTimeOfDay = "14:30:45", // Same
            RunningRaceTime = "01:45:15", // Different
            CurrentFlag = Flags.Green // Same
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.LapsToGo); // Should not be set
        Assert.IsNull(result.TimeToGo); // Should not be set
        Assert.IsNull(result.LocalTimeOfDay); // Should not be set
        Assert.AreEqual("01:47:30", result.RunningRaceTime); // Should be set
        Assert.IsNull(result.CurrentFlag); // Should not be set
    }

    [TestMethod]
    public void GetChanges_OnlyFlagStatusChanged_ReturnsPartialPatch()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(
            lapsToGo: 25, 
            timeToGo: "00:15:30", 
            timeOfDay: "14:30:45", 
            raceTime: "01:45:15", 
            flagStatus: "Yellow");

        var currentState = new SessionState
        {
            LapsToGo = 25, // Same
            TimeToGo = "00:15:30", // Same
            LocalTimeOfDay = "14:30:45", // Same
            RunningRaceTime = "01:45:15", // Same
            CurrentFlag = Flags.Green // Different (Yellow flag will be converted)
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.LapsToGo); // Should not be set
        Assert.IsNull(result.TimeToGo); // Should not be set
        Assert.IsNull(result.LocalTimeOfDay); // Should not be set
        Assert.IsNull(result.RunningRaceTime); // Should not be set
        Assert.AreEqual(Flags.Yellow, result.CurrentFlag); // Should be set
    }

    #endregion

    #region GetChanges Tests - Flag Status Conversion

    [TestMethod]
    public void GetChanges_DifferentFlagStatuses_ConvertsCorrectly()
    {
        var flagTestCases = new[]
        {
            (FlagStatus: "Yellow", ExpectedFlag: Flags.Yellow),
            (FlagStatus: "Red", ExpectedFlag: Flags.Red),
            (FlagStatus: "White", ExpectedFlag: Flags.White),
            (FlagStatus: "Finish", ExpectedFlag: Flags.Checkered),
            (FlagStatus: "Unknown", ExpectedFlag: Flags.Unknown),
            (FlagStatus: "", ExpectedFlag: Flags.Unknown) // Empty string should convert to Unknown
        };

        foreach (var testCase in flagTestCases)
        {
            // Arrange
            var heartbeat = CreateHeartbeat(
                lapsToGo: 25, 
                timeToGo: "00:15:30", 
                timeOfDay: "14:30:45", 
                raceTime: "01:45:15", 
                flagStatus: testCase.FlagStatus);

            var currentState = new SessionState
            {
                LapsToGo = 25,
                TimeToGo = "00:15:30",
                LocalTimeOfDay = "14:30:45",
                RunningRaceTime = "01:45:15",
                CurrentFlag = Flags.Green // Different from test flag
            };

            var stateUpdate = new HeartbeatStateUpdate(heartbeat);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for flag status: {testCase.FlagStatus}");
            
            if (testCase.ExpectedFlag == Flags.Green)
            {
                // If expected flag is Green and current is also Green, no change should be made
                Assert.IsNull(result.CurrentFlag, $"CurrentFlag should not be set when both are Green for: {testCase.FlagStatus}");
            }
            else
            {
                Assert.AreEqual(testCase.ExpectedFlag, result.CurrentFlag, 
                    $"Flag should be converted correctly for status: {testCase.FlagStatus}");
            }
        }
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_ZeroLapsToGo_HandlesCorrectly()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(lapsToGo: 0, timeToGo: "00:00:00", timeOfDay: "15:30:00", raceTime: "02:00:00", flagStatus: "Finish");
        var currentState = new SessionState
        {
            LapsToGo = 5,
            TimeToGo = "00:05:30",
            LocalTimeOfDay = "15:25:00",
            RunningRaceTime = "01:55:00",
            CurrentFlag = Flags.Green
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.LapsToGo);
        Assert.AreEqual("00:00:00", result.TimeToGo);
        Assert.AreEqual("15:30:00", result.LocalTimeOfDay);
        Assert.AreEqual("02:00:00", result.RunningRaceTime);
        Assert.AreEqual(Flags.Checkered, result.CurrentFlag);
    }

    [TestMethod]
    public void GetChanges_NegativeLapsToGo_HandlesCorrectly()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(lapsToGo: -1, timeToGo: "00:00:00", timeOfDay: "15:30:00", raceTime: "02:00:00", flagStatus: "Checkered");
        var currentState = new SessionState
        {
            LapsToGo = 5,
            TimeToGo = "00:05:30",
            LocalTimeOfDay = "15:25:00",
            RunningRaceTime = "01:55:00",
            CurrentFlag = Flags.Green
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(-1, result.LapsToGo);
    }

    [TestMethod]
    public void GetChanges_EmptyTimeStrings_HandlesCorrectly()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(lapsToGo: 25, timeToGo: "", timeOfDay: "", raceTime: "", flagStatus: "Green");
        var currentState = new SessionState
        {
            LapsToGo = 25,
            TimeToGo = "00:15:30",
            LocalTimeOfDay = "14:30:45",
            RunningRaceTime = "01:45:15",
            CurrentFlag = Flags.Green
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.LapsToGo); // Same
        Assert.AreEqual("", result.TimeToGo); // Changed to empty
        Assert.AreEqual("", result.LocalTimeOfDay); // Changed to empty
        Assert.AreEqual("", result.RunningRaceTime); // Changed to empty
        Assert.IsNull(result.CurrentFlag); // Same flag
    }

    #endregion

    #region GetChanges Tests - Time Format Variations

    [TestMethod]
    public void GetChanges_DifferentTimeFormats_HandlesCorrectly()
    {
        var timeTestCases = new[]
        {
            (TimeToGo: "00:15:30", TimeOfDay: "14:30:45", RaceTime: "01:45:15"),
            (TimeToGo: "01:30:00", TimeOfDay: "09:00:00", RaceTime: "00:30:00"),
            (TimeToGo: "00:05:45", TimeOfDay: "23:59:59", RaceTime: "03:15:30"),
            (TimeToGo: "10:00:00", TimeOfDay: "12:00:00", RaceTime: "10:00:00"), // Long race
            (TimeToGo: "0:05:30", TimeOfDay: "9:30:15", RaceTime: "2:15:45"), // No leading zeros
        };

        foreach (var testCase in timeTestCases)
        {
            // Arrange
            var heartbeat = CreateHeartbeat(
                lapsToGo: 25, 
                timeToGo: testCase.TimeToGo, 
                timeOfDay: testCase.TimeOfDay, 
                raceTime: testCase.RaceTime, 
                flagStatus: "Green");

            var currentState = new SessionState
            {
                LapsToGo = 25,
                TimeToGo = "00:00:00", // Different
                LocalTimeOfDay = "00:00:00", // Different
                RunningRaceTime = "00:00:00", // Different
                CurrentFlag = Flags.Green
            };

            var stateUpdate = new HeartbeatStateUpdate(heartbeat);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for time formats: {testCase}");
            Assert.AreEqual(testCase.TimeToGo, result.TimeToGo, $"TimeToGo should match: {testCase.TimeToGo}");
            Assert.AreEqual(testCase.TimeOfDay, result.LocalTimeOfDay, $"LocalTimeOfDay should match: {testCase.TimeOfDay}");
            Assert.AreEqual(testCase.RaceTime, result.RunningRaceTime, $"RunningRaceTime should match: {testCase.RaceTime}");
        }
    }

    #endregion

    #region GetChanges Tests - Multiple Sequential Calls

    [TestMethod]
    public void GetChanges_MultipleCallsWithSameState_ConsistentResults()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(lapsToGo: 25, timeToGo: "00:15:30", timeOfDay: "14:30:45", raceTime: "01:45:15", flagStatus: "Green");
        var currentState = new SessionState
        {
            LapsToGo = 20,
            TimeToGo = "00:20:00",
            LocalTimeOfDay = "14:25:30",
            RunningRaceTime = "01:40:00",
            CurrentFlag = Flags.Yellow
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result1 = stateUpdate.GetChanges(currentState);
        var result2 = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.LapsToGo, result2.LapsToGo);
        Assert.AreEqual(result1.TimeToGo, result2.TimeToGo);
        Assert.AreEqual(result1.LocalTimeOfDay, result2.LocalTimeOfDay);
        Assert.AreEqual(result1.RunningRaceTime, result2.RunningRaceTime);
        Assert.AreEqual(result1.CurrentFlag, result2.CurrentFlag);
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldRaceScenario_WorksCorrectly()
    {
        // Arrange - Simulate a real race heartbeat update
        var heartbeat = CreateHeartbeat(
            lapsToGo: 12, 
            timeToGo: "00:08:45", 
            timeOfDay: "15:45:30", 
            raceTime: "01:52:15", 
            flagStatus: "Green");
        
        var currentSessionState = new SessionState
        {
            SessionId = 456,
            EventId = 123,
            LapsToGo = 15, // Previous laps to go
            TimeToGo = "00:11:30", // Previous time to go
            LocalTimeOfDay = "15:42:00", // Previous time of day
            RunningRaceTime = "01:49:00", // Previous race time
            CurrentFlag = Flags.Yellow, // Previous flag status
            SessionName = "Main Race"
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentSessionState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(12, result.LapsToGo); // Updated lap count
        Assert.AreEqual("00:08:45", result.TimeToGo); // Updated time remaining
        Assert.AreEqual("15:45:30", result.LocalTimeOfDay); // Updated time of day
        Assert.AreEqual("01:52:15", result.RunningRaceTime); // Updated race time
        Assert.AreEqual(Flags.Green, result.CurrentFlag); // Flag changed to green
        
        // Verify that other properties are not touched
        Assert.IsNull(result.SessionId);
        Assert.IsNull(result.EventId);
        Assert.IsNull(result.SessionName);
    }

    [TestMethod]
    public void GetChanges_EnduranceRaceScenario_HandlesLongTimes()
    {
        // Arrange - Simulate endurance race with long times
        var heartbeat = CreateHeartbeat(
            lapsToGo: 0, 
            timeToGo: "00:00:00", 
            timeOfDay: "18:30:00", 
            raceTime: "12:00:00", 
            flagStatus: "Finish");
        
        var currentSessionState = new SessionState
        {
            LapsToGo = 5,
            TimeToGo = "00:15:30",
            LocalTimeOfDay = "18:15:00",
            RunningRaceTime = "11:45:00",
            CurrentFlag = Flags.Green
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentSessionState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.LapsToGo); // Race finished
        Assert.AreEqual("00:00:00", result.TimeToGo); // No time remaining
        Assert.AreEqual("18:30:00", result.LocalTimeOfDay); // Late in the day
        Assert.AreEqual("12:00:00", result.RunningRaceTime); // 12-hour race
        Assert.AreEqual(Flags.Checkered, result.CurrentFlag); // Race finished
    }

    [TestMethod]
    public void GetChanges_PracticeSessionScenario_HandlesCorrectly()
    {
        // Arrange - Simulate practice session
        var heartbeat = CreateHeartbeat(
            lapsToGo: 0, // Practice doesn't have lap limit
            timeToGo: "00:25:30", 
            timeOfDay: "10:35:00", 
            raceTime: "00:34:30", 
            flagStatus: "Green");
        
        var currentSessionState = new SessionState
        {
            LapsToGo = 0,
            TimeToGo = "00:30:00",
            LocalTimeOfDay = "10:30:00",
            RunningRaceTime = "00:30:00",
            CurrentFlag = Flags.Green
        };

        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Act
        var result = stateUpdate.GetChanges(currentSessionState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.LapsToGo); // No change in laps
        Assert.AreEqual("00:25:30", result.TimeToGo); // Session time remaining
        Assert.AreEqual("10:35:00", result.LocalTimeOfDay); // Current time
        Assert.AreEqual("00:34:30", result.RunningRaceTime); // Session elapsed time
        Assert.IsNull(result.CurrentFlag); // No flag change
    }

    [TestMethod]
    public void GetChanges_FlagSequenceProgression_TracksCorrectly()
    {
        // Arrange - Simulate flag progression through a race
        var flagProgression = new[]
        {
            (LapsToGo: 50, Flag: "Green", Description: "Race start"),
            (LapsToGo: 25, Flag: "Yellow", Description: "Caution period"),
            (LapsToGo: 20, Flag: "Green", Description: "Back to green"),
            (LapsToGo: 2, Flag: "White", Description: "White flag"),
            (LapsToGo: 0, Flag: "Finish", Description: "Race finish")
        };

        var currentState = new SessionState
        {
            LapsToGo = 100,
            TimeToGo = "02:00:00",
            LocalTimeOfDay = "14:00:00",
            RunningRaceTime = "00:00:00",
            CurrentFlag = Flags.Unknown
        };

        foreach (var stage in flagProgression)
        {
            // Arrange
            var heartbeat = CreateHeartbeat(
                lapsToGo: stage.LapsToGo, 
                timeToGo: "01:00:00", 
                timeOfDay: "15:00:00", 
                raceTime: "01:00:00", 
                flagStatus: stage.Flag);

            var stateUpdate = new HeartbeatStateUpdate(heartbeat);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for: {stage.Description}");
            Assert.AreEqual(stage.LapsToGo, result.LapsToGo, $"LapsToGo should match for: {stage.Description}");
            
            var expectedFlag = stage.Flag switch
            {
                "Green" => Flags.Green,
                "Yellow" => Flags.Yellow,
                "White" => Flags.White,
                "Finish" => Flags.Checkered,
                _ => Flags.Unknown
            };
            Assert.AreEqual(expectedFlag, result.CurrentFlag, $"Flag should match for: {stage.Description}");

            // Update current state for next iteration
            currentState.LapsToGo = stage.LapsToGo;
            currentState.CurrentFlag = expectedFlag;
        }
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void Heartbeat_Property_ReturnsCorrectValue()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(lapsToGo: 25, timeToGo: "00:15:30", timeOfDay: "14:30:45", raceTime: "01:45:15", flagStatus: "Green");

        // Act
        var stateUpdate = new HeartbeatStateUpdate(heartbeat);

        // Assert
        Assert.AreSame(heartbeat, stateUpdate.Heartbeat);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SameHeartbeatInstance_ReturnsTrue()
    {
        // Arrange
        var heartbeat = CreateHeartbeat(lapsToGo: 25, timeToGo: "00:15:30", timeOfDay: "14:30:45", raceTime: "01:45:15", flagStatus: "Green");
        var stateUpdate1 = new HeartbeatStateUpdate(heartbeat);
        var stateUpdate2 = new HeartbeatStateUpdate(heartbeat);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentHeartbeatInstances_ReturnsFalse()
    {
        // Arrange
        var heartbeat1 = CreateHeartbeat(lapsToGo: 25, timeToGo: "00:15:30", timeOfDay: "14:30:45", raceTime: "01:45:15", flagStatus: "Green");
        var heartbeat2 = CreateHeartbeat(lapsToGo: 20, timeToGo: "00:10:30", timeOfDay: "14:25:45", raceTime: "01:40:15", flagStatus: "Yellow");
        var stateUpdate1 = new HeartbeatStateUpdate(heartbeat1);
        var stateUpdate2 = new HeartbeatStateUpdate(heartbeat2);

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a Heartbeat instance with the specified values for testing.
    /// This method simulates the ToFlag extension method behavior by creating a mock extension.
    /// </summary>
    private static Heartbeat CreateHeartbeat(
        int lapsToGo = 0, 
        string timeToGo = "",
        string timeOfDay = "",
        string raceTime = "",
        string flagStatus = "")
    {
        var heartbeat = new Heartbeat();

        // Use reflection to set properties for testing
        var lapsToGoProp = typeof(Heartbeat).GetProperty("LapsToGo");
        var timeToGoProp = typeof(Heartbeat).GetProperty("TimeToGo");
        var timeOfDayProp = typeof(Heartbeat).GetProperty("TimeOfDay");
        var raceTimeProp = typeof(Heartbeat).GetProperty("RaceTime");
        var flagStatusProp = typeof(Heartbeat).GetProperty("FlagStatus");

        lapsToGoProp?.SetValue(heartbeat, lapsToGo);
        timeToGoProp?.SetValue(heartbeat, timeToGo);
        timeOfDayProp?.SetValue(heartbeat, timeOfDay);
        raceTimeProp?.SetValue(heartbeat, raceTime);
        flagStatusProp?.SetValue(heartbeat, flagStatus);

        return heartbeat;
    }

    #endregion
}

/// <summary>
/// Extension method to simulate ToFlag functionality for testing.
/// This mimics the behavior of the actual ToFlag extension method used in production.
/// </summary>
public static class FlagStatusExtensions
{
    public static Flags ToFlag(this string flagStatus)
    {
        return flagStatus?.Trim() switch
        {
            "Green" => Flags.Green,
            "Yellow" => Flags.Yellow,
            "Red" => Flags.Red,
            "White" => Flags.White,
            "Checkered" => Flags.Checkered,
            _ => Flags.Unknown
        };
    }
}
