using RedMist.EventProcessor.EventStatus.FlagData.StateChanges;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.FlagData.StateChanges;

[TestClass]
public class FlagsStateChangeTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidFlagDurations_CreatesInstance()
    {
        // Arrange
        var flagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, DateTime.Now, DateTime.Now.AddMinutes(30)),
            CreateFlagDuration(Flags.Yellow, DateTime.Now.AddMinutes(30), DateTime.Now.AddMinutes(35))
        };

        // Act
        var stateChange = new FlagsStateChange(flagDurations);

        // Assert
        Assert.IsNotNull(stateChange);
        Assert.AreSame(flagDurations, stateChange.FlagDurations);
        Assert.HasCount(2, stateChange.FlagDurations);
    }

    [TestMethod]
    public void Constructor_EmptyFlagDurations_CreatesInstance()
    {
        // Arrange
        var flagDurations = new List<FlagDuration>();

        // Act
        var stateChange = new FlagsStateChange(flagDurations);

        // Assert
        Assert.IsNotNull(stateChange);
        Assert.AreSame(flagDurations, stateChange.FlagDurations);
        Assert.HasCount(0, stateChange.FlagDurations);
    }

    #endregion

    #region GetChanges Tests - Different Counts

    [TestMethod]
    public void GetChanges_SameCountButDifferentFlags_ReturnsPatch()
    {
        // Arrange
        var baseTime = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, baseTime.AddMinutes(30)),
            CreateFlagDuration(Flags.Yellow, baseTime.AddMinutes(30), baseTime.AddMinutes(35))
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, baseTime.AddMinutes(30)),
            CreateFlagDuration(Flags.Red, baseTime.AddMinutes(30), baseTime.AddMinutes(35)) // Different flag
        };

        var currentState = new SessionState
        {
            SessionId = 123,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreSame(newFlagDurations, result.FlagDurations);
        Assert.HasCount(2, result.FlagDurations!);
    }

    [TestMethod]
    public void GetChanges_SameCountAndSameFlags_ReturnsNull()
    {
        // Arrange
        var baseTime = DateTime.Now;
        var flagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, baseTime.AddMinutes(30)),
            CreateFlagDuration(Flags.Yellow, baseTime.AddMinutes(30), baseTime.AddMinutes(35))
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, baseTime.AddMinutes(30)),
            CreateFlagDuration(Flags.Yellow, baseTime.AddMinutes(30), baseTime.AddMinutes(35))
        };

        var currentState = new SessionState
        {
            SessionId = 123,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(flagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNull(result); // Same flags should return null
    }

    #endregion

    #region GetChanges Tests - Timing Differences

    [TestMethod]
    public void GetChanges_SameFlagsDifferentStartTime_ReturnsPatch()
    {
        // Arrange
        var baseTime = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, baseTime.AddMinutes(30))
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime.AddMinutes(1), baseTime.AddMinutes(30)) // Different start time
        };

        var currentState = new SessionState
        {
            SessionId = 123,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreSame(newFlagDurations, result.FlagDurations);
    }

    [TestMethod]
    public void GetChanges_SameFlagsDifferentEndTime_ReturnsPatch()
    {
        // Arrange
        var baseTime = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, baseTime.AddMinutes(30))
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, baseTime.AddMinutes(25)) // Different end time
        };

        var currentState = new SessionState
        {
            SessionId = 123,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreSame(newFlagDurations, result.FlagDurations);
    }

    [TestMethod]
    public void GetChanges_OngoingFlagToCompletedFlag_ReturnsPatch()
    {
        // Arrange
        var baseTime = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, baseTime.AddMinutes(30)) // Completed flag
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, null) // Ongoing flag
        };

        var currentState = new SessionState
        {
            SessionId = 123,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreSame(newFlagDurations, result.FlagDurations);
    }

    [TestMethod]
    public void GetChanges_CompletedFlagToOngoingFlag_ReturnsPatch()
    {
        // Arrange
        var baseTime = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, null) // Ongoing flag
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, baseTime.AddMinutes(30)) // Completed flag
        };

        var currentState = new SessionState
        {
            SessionId = 123,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreSame(newFlagDurations, result.FlagDurations);
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_EmptyFlagDurationsLists_ReturnsNull()
    {
        // Arrange
        var emptyFlagDurations = new List<FlagDuration>();

        var currentState = new SessionState
        {
            SessionId = 123,
            FlagDurations = new List<FlagDuration>() // Also empty
        };

        var stateChange = new FlagsStateChange(emptyFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNull(result); // Both empty lists should return null
    }

    #endregion

    #region GetChanges Tests - All Flag Types

    [TestMethod]
    public void GetChanges_AllFlagTypes_HandlesCorrectly()
    {
        var allFlagTypes = new[]
        {
            Flags.Unknown,
            Flags.Green,
            Flags.Yellow,
            Flags.Red,
            Flags.White,
            Flags.Checkered
        };

        var baseTime = DateTime.Now;

        foreach (var flag in allFlagTypes)
        {
            // Arrange
            var newFlagDurations = new List<FlagDuration>
            {
                CreateFlagDuration(flag, baseTime, baseTime.AddMinutes(10))
            };

            var currentStateFlagDurations = new List<FlagDuration>
            {
                CreateFlagDuration(Flags.Green, baseTime, baseTime.AddMinutes(10)) // Different flag
            };

            var currentState = new SessionState
            {
                SessionId = 123,
                FlagDurations = currentStateFlagDurations
            };

            var stateChange = new FlagsStateChange(newFlagDurations);

            // Act
            var result = stateChange.GetChanges(currentState);

            // Assert
            if (flag == Flags.Green)
            {
                Assert.IsNull(result, $"Same flag {flag} should return null");
            }
            else
            {
                Assert.IsNotNull(result, $"Different flag {flag} should return patch");
                Assert.AreEqual(flag, result.FlagDurations![0].Flag);
            }
        }
    }

    #endregion

    #region GetChanges Tests - Multiple Flags

    [TestMethod]
    public void GetChanges_MultipleFlagsFirstDifferent_ReturnsPatch()
    {
        // Arrange
        var baseTime = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Yellow, baseTime, baseTime.AddMinutes(5)), // Different (was Green)
            CreateFlagDuration(Flags.Green, baseTime.AddMinutes(5), baseTime.AddMinutes(30)),
            CreateFlagDuration(Flags.Checkered, baseTime.AddMinutes(30), null)
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, baseTime.AddMinutes(5)), // Different
            CreateFlagDuration(Flags.Green, baseTime.AddMinutes(5), baseTime.AddMinutes(30)),
            CreateFlagDuration(Flags.Checkered, baseTime.AddMinutes(30), null)
        };

        var currentState = new SessionState
        {
            SessionId = 123,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreSame(newFlagDurations, result.FlagDurations);
        Assert.HasCount(3, result.FlagDurations!);
    }

    [TestMethod]
    public void GetChanges_MultipleFlagsLastDifferent_ReturnsPatch()
    {
        // Arrange
        var baseTime = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Yellow, baseTime, baseTime.AddMinutes(5)),
            CreateFlagDuration(Flags.Green, baseTime.AddMinutes(5), baseTime.AddMinutes(30)),
            CreateFlagDuration(Flags.Red, baseTime.AddMinutes(30), null) // Different (was Checkered)
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Yellow, baseTime, baseTime.AddMinutes(5)),
            CreateFlagDuration(Flags.Green, baseTime.AddMinutes(5), baseTime.AddMinutes(30)),
            CreateFlagDuration(Flags.Checkered, baseTime.AddMinutes(30), null) // Different
        };

        var currentState = new SessionState
        {
            SessionId = 123,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreSame(newFlagDurations, result.FlagDurations);
        Assert.HasCount(3, result.FlagDurations!);
    }

    [TestMethod]
    public void GetChanges_MultipleFlagsMiddleDifferent_ReturnsPatch()
    {
        // Arrange
        var baseTime = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Yellow, baseTime, baseTime.AddMinutes(5)),
            CreateFlagDuration(Flags.Red, baseTime.AddMinutes(5), baseTime.AddMinutes(30)), // Different (was Green)
            CreateFlagDuration(Flags.Checkered, baseTime.AddMinutes(30), null)
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Yellow, baseTime, baseTime.AddMinutes(5)),
            CreateFlagDuration(Flags.Green, baseTime.AddMinutes(5), baseTime.AddMinutes(30)), // Different
            CreateFlagDuration(Flags.Checkered, baseTime.AddMinutes(30), null)
        };

        var currentState = new SessionState
        {
            SessionId = 123,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreSame(newFlagDurations, result.FlagDurations);
        Assert.HasCount(3, result.FlagDurations!);
    }

    #endregion

    #region GetChanges Tests - Large Data Sets

    [TestMethod]
    public void GetChanges_LargeFlagDurationSet_ProcessesEfficiently()
    {
        // Arrange
        var baseTime = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>();
        var currentStateFlagDurations = new List<FlagDuration>();

        // Create 100 flag durations
        for (int i = 0; i < 100; i++)
        {
            var flag = (Flags)(i % 6); // Cycle through all flag types
            var startTime = baseTime.AddMinutes(i * 5);
            DateTime? endTime = i == 99 ? null : startTime.AddMinutes(5); // Last one is ongoing

            newFlagDurations.Add(CreateFlagDuration(flag, startTime, endTime));
            
            // Make the last one different to trigger a change
            if (i == 99)
            {
                currentStateFlagDurations.Add(CreateFlagDuration(Flags.Green, startTime, endTime)); // Different flag
            }
            else
            {
                currentStateFlagDurations.Add(CreateFlagDuration(flag, startTime, endTime));
            }
        }

        var currentState = new SessionState
        {
            SessionId = 123,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreSame(newFlagDurations, result.FlagDurations);
        Assert.HasCount(100, result.FlagDurations!);
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldRaceScenario_WorksCorrectly()
    {
        // Arrange - Simulate a real race flag progression
        var raceStart = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, raceStart, raceStart.AddMinutes(15)), // Race start
            CreateFlagDuration(Flags.Yellow, raceStart.AddMinutes(15), raceStart.AddMinutes(20)), // Caution
            CreateFlagDuration(Flags.Green, raceStart.AddMinutes(20), raceStart.AddMinutes(58)), // Back to racing
            CreateFlagDuration(Flags.White, raceStart.AddMinutes(58), raceStart.AddMinutes(60)), // Last lap
            CreateFlagDuration(Flags.Checkered, raceStart.AddMinutes(60), null) // Race finish (ongoing)
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, raceStart, raceStart.AddMinutes(15)),
            CreateFlagDuration(Flags.Yellow, raceStart.AddMinutes(15), raceStart.AddMinutes(20)),
            CreateFlagDuration(Flags.Green, raceStart.AddMinutes(20), raceStart.AddMinutes(58)),
            CreateFlagDuration(Flags.White, raceStart.AddMinutes(58), raceStart.AddMinutes(60)),
            CreateFlagDuration(Flags.White, raceStart.AddMinutes(60), null) // Different - was White, now Checkered
        };

        var currentState = new SessionState
        {
            SessionId = 456,
            EventId = 123,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreSame(newFlagDurations, result.FlagDurations);
        Assert.HasCount(5, result.FlagDurations!);
        
        // Verify the last flag changed to Checkered
        var lastFlag = result.FlagDurations?.Last();
        Assert.AreEqual(Flags.Checkered, lastFlag?.Flag);
        Assert.IsNull(lastFlag?.EndTime); // Should be ongoing
        
        // Verify that other properties are not touched
        Assert.IsNull(result.SessionId);
        Assert.IsNull(result.EventId);
    }

    [TestMethod]
    public void GetChanges_EnduranceRaceScenario_HandlesLongSequence()
    {
        // Arrange - Simulate endurance race with many flag changes
        var raceStart = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>();
        var currentStateFlagDurations = new List<FlagDuration>();

        // Build a long sequence of flag changes over 12 hours
        var currentTime = raceStart;
        var isGreen = true;

        for (int hour = 0; hour < 12; hour++)
        {
            var flag = isGreen ? Flags.Green : Flags.Yellow;
            var duration = isGreen ? 45 : 15; // Green for 45 minutes, Yellow for 15 minutes
            DateTime? endTime = hour == 11 && !isGreen ? null : currentTime.AddMinutes(duration);

            newFlagDurations.Add(CreateFlagDuration(flag, currentTime, endTime));
            
            // Make one flag different in the middle
            if (hour == 6 && isGreen)
            {
                currentStateFlagDurations.Add(CreateFlagDuration(Flags.Red, currentTime, endTime)); // Different
            }
            else
            {
                currentStateFlagDurations.Add(CreateFlagDuration(flag, currentTime, endTime));
            }

            if (endTime.HasValue)
            {
                currentTime = endTime.Value;
            }
            isGreen = !isGreen;
        }

        var currentState = new SessionState
        {
            SessionId = 789,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreSame(newFlagDurations, result.FlagDurations);
        Assert.HasCount(12, result.FlagDurations!);
    }

    [TestMethod]
    public void GetChanges_PracticeSessionScenario_MinimalFlags()
    {
        // Arrange - Simulate practice session (mostly green)
        var sessionStart = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, sessionStart, null) // Just green flag for entire session
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, sessionStart, null) // Same
        };

        var currentState = new SessionState
        {
            SessionId = 101,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.IsNull(result); // Should be no changes
    }

    [TestMethod]
    public void GetChanges_QualifyingSessionScenario_RedFlagRestart()
    {
        // Arrange - Simulate qualifying with red flag restart
        var sessionStart = DateTime.Now;
        var newFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, sessionStart, sessionStart.AddMinutes(10)),
            CreateFlagDuration(Flags.Red, sessionStart.AddMinutes(10), sessionStart.AddMinutes(25)), // Red flag stoppage
            CreateFlagDuration(Flags.Green, sessionStart.AddMinutes(25), null) // Session restart
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, sessionStart, sessionStart.AddMinutes(10)),
            CreateFlagDuration(Flags.Red, sessionStart.AddMinutes(10), null) // Still red (not restarted yet)
        };

        var currentState = new SessionState
        {
            SessionId = 202,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(newFlagDurations);

        // Act
        var result = stateChange.GetChanges(currentState);

        // Assert
        Assert.HasCount(newFlagDurations.Count, result!.FlagDurations!);
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void FlagDurations_Property_ReturnsCorrectValue()
    {
        // Arrange
        var flagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, DateTime.Now, DateTime.Now.AddMinutes(30))
        };

        // Act
        var stateChange = new FlagsStateChange(flagDurations);

        // Assert
        Assert.AreSame(flagDurations, stateChange.FlagDurations);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SameFlagDurationsInstance_ReturnsTrue()
    {
        // Arrange
        var flagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, DateTime.Now, DateTime.Now.AddMinutes(30))
        };

        var stateChange1 = new FlagsStateChange(flagDurations);
        var stateChange2 = new FlagsStateChange(flagDurations);

        // Act & Assert
        Assert.AreEqual(stateChange1, stateChange2);
        Assert.IsTrue(stateChange1.Equals(stateChange2));
        Assert.AreEqual(stateChange1.GetHashCode(), stateChange2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentFlagDurationsInstances_ReturnsFalse()
    {
        // Arrange
        var flagDurations1 = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, DateTime.Now, DateTime.Now.AddMinutes(30))
        };

        var flagDurations2 = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Yellow, DateTime.Now, DateTime.Now.AddMinutes(30))
        };

        var stateChange1 = new FlagsStateChange(flagDurations1);
        var stateChange2 = new FlagsStateChange(flagDurations2);

        // Act & Assert
        Assert.AreNotEqual(stateChange1, stateChange2);
        Assert.IsFalse(stateChange1.Equals(stateChange2));
    }

    #endregion

    #region Performance Tests

    [TestMethod]
    public void GetChanges_HighFrequencyUpdates_PerformsEfficiently()
    {
        // Arrange
        var baseTime = DateTime.Now;
        var flagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Green, baseTime, baseTime.AddMinutes(30))
        };

        var currentStateFlagDurations = new List<FlagDuration>
        {
            CreateFlagDuration(Flags.Yellow, baseTime, baseTime.AddMinutes(30)) // Different
        };

        var currentState = new SessionState
        {
            SessionId = 999,
            FlagDurations = currentStateFlagDurations
        };

        var stateChange = new FlagsStateChange(flagDurations);

        // Act & Assert - Run many iterations to test performance
        for (int i = 0; i < 10000; i++)
        {
            var result = stateChange.GetChanges(currentState);
            Assert.IsNotNull(result);
            Assert.AreSame(flagDurations, result.FlagDurations);
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a FlagDuration instance with the specified values for testing.
    /// </summary>
    private static FlagDuration CreateFlagDuration(Flags flag, DateTime startTime, DateTime? endTime)
    {
        return new FlagDuration
        {
            Flag = flag,
            StartTime = startTime,
            EndTime = endTime
        };
    }

    #endregion
}
