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
public class PracticeQualifyingStateUpdateTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidRunInformation_CreatesInstance()
    {
        // Arrange
        var runInformation = CreateRunInformation("Practice Session", RunType.Practice);

        // Act
        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(runInformation, stateUpdate.RunInformation);
    }

    #endregion

    #region GetChanges Tests - Practice Sessions

    [TestMethod]
    public void GetChanges_PracticeSession_StateNotPracticeQualifying_UpdatesToTrue()
    {
        // Arrange
        var runInformation = CreateRunInformation("Practice Session", RunType.Practice);
        var currentState = new SessionState
        {
            SessionId = 123,
            IsPracticeQualifying = false // Currently not practice/qualifying
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(123, result.SessionId);
        Assert.AreEqual(true, result.IsPracticeQualifying);
    }

    [TestMethod]
    public void GetChanges_PracticeSession_StateAlreadyPracticeQualifying_ReturnsEmptyPatch()
    {
        // Arrange
        var runInformation = CreateRunInformation("Practice Session", RunType.Practice);
        var currentState = new SessionState
        {
            SessionId = 456,
            IsPracticeQualifying = true // Already practice/qualifying
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(456, result.SessionId);
        Assert.IsNull(result.IsPracticeQualifying); // No change needed
    }

    #endregion

    #region GetChanges Tests - Qualifying Sessions

    [TestMethod]
    public void GetChanges_QualifyingSession_StateNotPracticeQualifying_UpdatesToTrue()
    {
        // Arrange
        var runInformation = CreateRunInformation("Qualifying Session", RunType.Qualifying);
        var currentState = new SessionState
        {
            SessionId = 789,
            IsPracticeQualifying = false // Currently not practice/qualifying
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(789, result.SessionId);
        Assert.AreEqual(true, result.IsPracticeQualifying);
    }

    [TestMethod]
    public void GetChanges_QualifyingSession_StateAlreadyPracticeQualifying_ReturnsEmptyPatch()
    {
        // Arrange
        var runInformation = CreateRunInformation("Qualifying Session", RunType.Qualifying);
        var currentState = new SessionState
        {
            SessionId = 101112,
            IsPracticeQualifying = true // Already practice/qualifying
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(101112, result.SessionId);
        Assert.IsNull(result.IsPracticeQualifying); // No change needed
    }

    #endregion

    #region GetChanges Tests - Single Car Qualifying Sessions

    [TestMethod]
    public void GetChanges_SingleCarQualifyingSession_StateNotPracticeQualifying_UpdatesToTrue()
    {
        // Arrange
        var runInformation = CreateRunInformation("Single Car Qualifying", RunType.SingleCarQualifying);
        var currentState = new SessionState
        {
            SessionId = 131415,
            IsPracticeQualifying = false // Currently not practice/qualifying
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(131415, result.SessionId);
        Assert.AreEqual(true, result.IsPracticeQualifying);
    }

    [TestMethod]
    public void GetChanges_SingleCarQualifyingSession_StateAlreadyPracticeQualifying_ReturnsEmptyPatch()
    {
        // Arrange
        var runInformation = CreateRunInformation("Single Car Qualifying", RunType.SingleCarQualifying);
        var currentState = new SessionState
        {
            SessionId = 161718,
            IsPracticeQualifying = true // Already practice/qualifying
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(161718, result.SessionId);
        Assert.IsNull(result.IsPracticeQualifying); // No change needed
    }

    #endregion

    #region GetChanges Tests - Race Sessions

    [TestMethod]
    public void GetChanges_RaceSession_StatePracticeQualifying_UpdatesToFalse()
    {
        // Arrange
        var runInformation = CreateRunInformation("Race Session", RunType.Race);
        var currentState = new SessionState
        {
            SessionId = 192021,
            IsPracticeQualifying = true // Currently practice/qualifying
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(192021, result.SessionId);
        Assert.AreEqual(false, result.IsPracticeQualifying);
    }

    [TestMethod]
    public void GetChanges_RaceSession_StateNotPracticeQualifying_ReturnsEmptyPatch()
    {
        // Arrange
        var runInformation = CreateRunInformation("Race Session", RunType.Race);
        var currentState = new SessionState
        {
            SessionId = 222324,
            IsPracticeQualifying = false // Already not practice/qualifying
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(222324, result.SessionId);
        Assert.IsNull(result.IsPracticeQualifying); // No change needed
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_ZeroSessionId_CopiesCorrectly()
    {
        // Arrange
        var runInformation = CreateRunInformation("Practice Session", RunType.Practice);
        var currentState = new SessionState
        {
            SessionId = 0,
            IsPracticeQualifying = false
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.SessionId);
        Assert.AreEqual(true, result.IsPracticeQualifying);
    }

    [TestMethod]
    public void GetChanges_NegativeSessionId_CopiesCorrectly()
    {
        // Arrange
        var runInformation = CreateRunInformation("Qualifying Session", RunType.Qualifying);
        var currentState = new SessionState
        {
            SessionId = -1,
            IsPracticeQualifying = false
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(-1, result.SessionId);
        Assert.AreEqual(true, result.IsPracticeQualifying);
    }

    #endregion

    #region GetChanges Tests - All RunType Values

    [TestMethod]
    public void GetChanges_AllPracticeQualifyingRunTypes_SetsPracticeQualifyingToTrue()
    {
        var practiceQualifyingRunTypes = new[]
        {
            RunType.Practice,
            RunType.Qualifying,
            RunType.SingleCarQualifying
        };

        foreach (var runType in practiceQualifyingRunTypes)
        {
            // Arrange
            var runInformation = CreateRunInformation($"{runType} Session", runType);
            var currentState = new SessionState
            {
                SessionId = 100,
                IsPracticeQualifying = false
            };

            var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for RunType: {runType}");
            Assert.AreEqual(true, result.IsPracticeQualifying, $"IsPracticeQualifying should be true for RunType: {runType}");
        }
    }

    [TestMethod]
    public void GetChanges_RaceRunType_SetsPracticeQualifyingToFalse()
    {
        // Arrange
        var runInformation = CreateRunInformation("Race Session", RunType.Race);
        var currentState = new SessionState
        {
            SessionId = 200,
            IsPracticeQualifying = true
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(false, result.IsPracticeQualifying);
    }

    #endregion

    #region GetChanges Tests - Multiple Sequential Calls

    [TestMethod]
    public void GetChanges_MultipleCallsWithSameState_ConsistentResults()
    {
        // Arrange
        var runInformation = CreateRunInformation("Practice Session", RunType.Practice);
        var currentState = new SessionState
        {
            SessionId = 300,
            IsPracticeQualifying = false
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result1 = stateUpdate.GetChanges(currentState);
        var result2 = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.SessionId, result2.SessionId);
        Assert.AreEqual(result1.IsPracticeQualifying, result2.IsPracticeQualifying);
        Assert.AreEqual(true, result1.IsPracticeQualifying);
        Assert.AreEqual(true, result2.IsPracticeQualifying);
    }

    [TestMethod]
    public void GetChanges_DifferentStatesSequentially_ReturnsCorrectPatches()
    {
        // Arrange
        var practiceRunInformation = CreateRunInformation("Practice Session", RunType.Practice);
        var raceRunInformation = CreateRunInformation("Race Session", RunType.Race);

        var practiceState = new SessionState
        {
            SessionId = 400,
            IsPracticeQualifying = false // Will change to true
        };

        var raceState = new SessionState
        {
            SessionId = 401,
            IsPracticeQualifying = true // Will change to false
        };

        var practiceStateUpdate = new PracticeQualifyingStateUpdate(practiceRunInformation);
        var raceStateUpdate = new PracticeQualifyingStateUpdate(raceRunInformation);

        // Act
        var practiceResult = practiceStateUpdate.GetChanges(practiceState);
        var raceResult = raceStateUpdate.GetChanges(raceState);

        // Assert
        Assert.IsNotNull(practiceResult);
        Assert.IsNotNull(raceResult);
        
        // Practice session should set to true
        Assert.AreEqual(400, practiceResult.SessionId);
        Assert.AreEqual(true, practiceResult.IsPracticeQualifying);
        
        // Race session should set to false
        Assert.AreEqual(401, raceResult.SessionId);
        Assert.AreEqual(false, raceResult.IsPracticeQualifying);
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldPracticeScenario_WorksCorrectly()
    {
        // Arrange - Simulate a practice session during a race weekend
        var runInformation = CreateRunInformation("Free Practice 1", RunType.Practice);
        
        var currentSessionState = new SessionState
        {
            SessionId = 20241215,
            EventId = 100,
            SessionName = "Free Practice 1",
            IsPracticeQualifying = false, // Was set incorrectly
            IsLive = true
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentSessionState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(20241215, result.SessionId);
        Assert.AreEqual(true, result.IsPracticeQualifying);
        
        // Verify that other properties are not touched
        Assert.IsNull(result.EventId);
        Assert.IsNull(result.SessionName);
        Assert.IsNull(result.IsLive);
    }

    [TestMethod]
    public void GetChanges_RealWorldQualifyingScenario_WorksCorrectly()
    {
        // Arrange - Simulate a qualifying session
        var runInformation = CreateRunInformation("Qualifying Session", RunType.Qualifying);
        
        var currentSessionState = new SessionState
        {
            SessionId = 555,
            EventId = 200,
            SessionName = "Qualifying",
            IsPracticeQualifying = false, // Transitioning from practice
            IsLive = true
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentSessionState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(555, result.SessionId);
        Assert.AreEqual(true, result.IsPracticeQualifying);
    }

    [TestMethod]
    public void GetChanges_RealWorldRaceScenario_WorksCorrectly()
    {
        // Arrange - Simulate transitioning from qualifying to race
        var runInformation = CreateRunInformation("Main Race", RunType.Race);
        
        var currentSessionState = new SessionState
        {
            SessionId = 777,
            EventId = 300,
            SessionName = "Main Race",
            IsPracticeQualifying = true, // Was qualifying, now race
            IsLive = true
        };

        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Act
        var result = stateUpdate.GetChanges(currentSessionState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(777, result.SessionId);
        Assert.AreEqual(false, result.IsPracticeQualifying);
    }

    [TestMethod]
    public void GetChanges_WeekendProgression_HandlesCorrectly()
    {
        // Arrange - Simulate a race weekend progression
        var sessions = new[]
        {
            (Name: "Free Practice 1", Type: RunType.Practice, ExpectedFlag: true),
            (Name: "Free Practice 2", Type: RunType.Practice, ExpectedFlag: true),
            (Name: "Qualifying", Type: RunType.Qualifying, ExpectedFlag: true),
            (Name: "Race 1", Type: RunType.Race, ExpectedFlag: false),
            (Name: "Race 2", Type: RunType.Race, ExpectedFlag: false)
        };

        foreach (var session in sessions)
        {
            // Arrange
            var runInformation = CreateRunInformation(session.Name, session.Type);
            var currentState = new SessionState
            {
                SessionId = 888,
                IsPracticeQualifying = !session.ExpectedFlag // Opposite of expected
            };

            var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for session: {session.Name}");
            Assert.AreEqual(session.ExpectedFlag, result.IsPracticeQualifying, 
                $"IsPracticeQualifying should be {session.ExpectedFlag} for session: {session.Name}");
        }
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void RunInformation_Property_ReturnsCorrectValue()
    {
        // Arrange
        var runInformation = CreateRunInformation("Test Session", RunType.Practice);

        // Act
        var stateUpdate = new PracticeQualifyingStateUpdate(runInformation);

        // Assert
        Assert.AreSame(runInformation, stateUpdate.RunInformation);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SameRunInformationInstance_ReturnsTrue()
    {
        // Arrange
        var runInformation = CreateRunInformation("Test Session", RunType.Practice);
        var stateUpdate1 = new PracticeQualifyingStateUpdate(runInformation);
        var stateUpdate2 = new PracticeQualifyingStateUpdate(runInformation);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentRunInformationInstances_ReturnsFalse()
    {
        // Arrange
        var runInformation1 = CreateRunInformation("Practice Session", RunType.Practice);
        var runInformation2 = CreateRunInformation("Qualifying Session", RunType.Qualifying);
        var stateUpdate1 = new PracticeQualifyingStateUpdate(runInformation1);
        var stateUpdate2 = new PracticeQualifyingStateUpdate(runInformation2);

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a RunInformation instance with the specified values for testing.
    /// </summary>
    private static RunInformation CreateRunInformation(string runName, RunType runType)
    {
        var runInformation = new RunInformation();

        // Use reflection to set private properties for testing
        var runNameProp = typeof(RunInformation).GetProperty("RunName");
        var runTypeStrProp = typeof(RunInformation).GetProperty("RunTypeStr");

        runNameProp?.SetValue(runInformation, runName);
        
        // Set the RunTypeStr based on the enum value to match the switch statement in RunInformation
        var runTypeStr = runType switch
        {
            RunType.Practice => "P",
            RunType.Qualifying => "Q", 
            RunType.SingleCarQualifying => "S",
            RunType.Race => "R",
            _ => "R" // Default to Race
        };
        runTypeStrProp?.SetValue(runInformation, runTypeStr);

        return runInformation;
    }

    #endregion
}

/// <summary>
/// Enum representing run types for testing purposes.
/// This should match the actual enum used in the production code.
/// </summary>
public enum RunType
{
    Race,
    Practice,
    Qualifying,
    SingleCarQualifying
}
