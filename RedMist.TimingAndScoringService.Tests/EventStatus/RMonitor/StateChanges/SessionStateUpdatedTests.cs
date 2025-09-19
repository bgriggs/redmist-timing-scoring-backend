using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedMist.TimingAndScoringService.EventStatus.RMonitor.StateChanges;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.RMonitor.StateChanges;

[TestClass]
public class SessionStateUpdatedTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidParameters_CreatesInstance()
    {
        // Arrange
        var sessionId = 123;
        var sessionName = "Practice Session 1";

        // Act
        var stateUpdate = new SessionStateUpdate(sessionId, sessionName);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreEqual(sessionId, stateUpdate.SessionId);
        Assert.AreEqual(sessionName, stateUpdate.SessionName);
    }

    [TestMethod]
    public void Constructor_ZeroSessionId_CreatesInstance()
    {
        // Arrange
        var sessionId = 0;
        var sessionName = "Test Session";

        // Act
        var stateUpdate = new SessionStateUpdate(sessionId, sessionName);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreEqual(0, stateUpdate.SessionId);
        Assert.AreEqual("Test Session", stateUpdate.SessionName);
    }

    [TestMethod]
    public void Constructor_NegativeSessionId_CreatesInstance()
    {
        // Arrange
        var sessionId = -1;
        var sessionName = "Invalid Session";

        // Act
        var stateUpdate = new SessionStateUpdate(sessionId, sessionName);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreEqual(-1, stateUpdate.SessionId);
        Assert.AreEqual("Invalid Session", stateUpdate.SessionName);
    }

    [TestMethod]
    public void Constructor_EmptySessionName_CreatesInstance()
    {
        // Arrange
        var sessionId = 456;
        var sessionName = "";

        // Act
        var stateUpdate = new SessionStateUpdate(sessionId, sessionName);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreEqual(456, stateUpdate.SessionId);
        Assert.AreEqual("", stateUpdate.SessionName);
    }

    [TestMethod]
    public void Constructor_NullSessionName_CreatesInstance()
    {
        // Arrange
        var sessionId = 789;
        string sessionName = null!;

        // Act
        var stateUpdate = new SessionStateUpdate(sessionId, sessionName);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreEqual(789, stateUpdate.SessionId);
        Assert.IsNull(stateUpdate.SessionName);
    }

    #endregion

    #region GetChanges Tests - Both Properties Changed

    [TestMethod]
    public void GetChanges_BothSessionIdAndNameChanged_ReturnsCompletePatch()
    {
        // Arrange
        var stateUpdate = new SessionStateUpdate(456, "Qualifying Session");
        var currentState = new SessionState
        {
            SessionId = 123, // Different
            SessionName = "Practice Session" // Different
        };

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(456, result.SessionId);
        Assert.AreEqual("Qualifying Session", result.SessionName);
    }

    [TestMethod]
    public void GetChanges_NoChanges_ReturnsNull()
    {
        // Arrange
        var stateUpdate = new SessionStateUpdate(123, "Practice Session");
        var currentState = new SessionState
        {
            SessionId = 123, // Same
            SessionName = "Practice Session" // Same
        };

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNull(result);
    }

    #endregion

    #region GetChanges Tests - Individual Property Changes

    [TestMethod]
    public void GetChanges_OnlySessionIdChanged_ReturnsCompletePatch()
    {
        // Arrange
        var stateUpdate = new SessionStateUpdate(456, "Practice Session");
        var currentState = new SessionState
        {
            SessionId = 123, // Different
            SessionName = "Practice Session" // Same
        };

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(456, result.SessionId);
        Assert.AreEqual("Practice Session", result.SessionName);
    }

    [TestMethod]
    public void GetChanges_OnlySessionNameChanged_ReturnsCompletePatch()
    {
        // Arrange
        var stateUpdate = new SessionStateUpdate(123, "Qualifying Session");
        var currentState = new SessionState
        {
            SessionId = 123, // Same
            SessionName = "Practice Session" // Different
        };

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(123, result.SessionId);
        Assert.AreEqual("Qualifying Session", result.SessionName);
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_ZeroSessionIds_ComparedCorrectly()
    {
        // Arrange
        var stateUpdate = new SessionStateUpdate(0, "Zero Session");
        var currentState = new SessionState
        {
            SessionId = 0, // Same
            SessionName = "Zero Session" // Same
        };

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNull(result); // No changes
    }

    [TestMethod]
    public void GetChanges_NegativeSessionIds_ComparedCorrectly()
    {
        // Arrange
        var stateUpdate = new SessionStateUpdate(-1, "Negative Session");
        var currentState = new SessionState
        {
            SessionId = -1, // Same
            SessionName = "Negative Session" // Same
        };

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNull(result); // No changes
    }

    [TestMethod]
    public void GetChanges_EmptySessionNames_ComparedCorrectly()
    {
        // Arrange
        var stateUpdate = new SessionStateUpdate(123, "");
        var currentState = new SessionState
        {
            SessionId = 123, // Same
            SessionName = "" // Same
        };

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNull(result); // No changes
    }

    [TestMethod]
    public void GetChanges_StringToNullSessionName_ReturnsCompletePatch()
    {
        // Arrange
        var stateUpdate = new SessionStateUpdate(123, null!);
        var currentState = new SessionState
        {
            SessionId = 123, // Same
            SessionName = "Old Session" // Different (string to null)
        };

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(123, result.SessionId);
        Assert.IsNull(result.SessionName);
    }

    #endregion

    #region GetChanges Tests - Session Name Variations

    [TestMethod]
    public void GetChanges_DifferentSessionNameFormats_HandlesCorrectly()
    {
        var sessionNameTestCases = new[]
        {
            "Practice Session 1",
            "Qualifying",
            "Race 1",
            "Free Practice",
            "Sprint Race",
            "Endurance Race",
            "Test Session",
            "Warm-up",
            "Session with Numbers 123",
            "Session with Special!@#$%^&*()Characters",
            "VeryLongSessionNameThatExceedsTypicalLengthLimitsButShouldStillBeHandledCorrectlyByTheSystem"
        };

        foreach (var sessionName in sessionNameTestCases)
        {
            // Arrange
            var stateUpdate = new SessionStateUpdate(100, sessionName);
            var currentState = new SessionState
            {
                SessionId = 100, // Same
                SessionName = "Different Session" // Different
            };

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for session name: {sessionName}");
            Assert.AreEqual(100, result.SessionId);
            Assert.AreEqual(sessionName, result.SessionName, $"Session name should match for: {sessionName}");
        }
    }

    [TestMethod]
    public void GetChanges_WhitespaceInSessionNames_PreservesExactly()
    {
        var whitespaceTestCases = new[]
        {
            " Practice Session ",
            "  Practice Session",
            "Practice Session  ",
            "\tPractice Session\t",
            "\nPractice Session\n",
            "Practice\tSession",
            "Practice\nSession"
        };

        foreach (var sessionName in whitespaceTestCases)
        {
            // Arrange
            var stateUpdate = new SessionStateUpdate(200, sessionName);
            var currentState = new SessionState
            {
                SessionId = 200, // Same
                SessionName = "Clean Session" // Different
            };

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for whitespace session name: {sessionName}");
            Assert.AreEqual(sessionName, result.SessionName, $"Session name with whitespace should be preserved exactly: '{sessionName}'");
        }
    }

    #endregion

    #region GetChanges Tests - Session ID Variations

    [TestMethod]
    public void GetChanges_DifferentSessionIdRanges_HandlesCorrectly()
    {
        var sessionIdTestCases = new[]
        {
            int.MinValue,
            -1000,
            -1,
            0,
            1,
            100,
            1000,
            int.MaxValue
        };

        foreach (var sessionId in sessionIdTestCases)
        {
            // Arrange
            var stateUpdate = new SessionStateUpdate(sessionId, "Test Session");
            var currentState = new SessionState
            {
                SessionId = 999, // Different
                SessionName = "Test Session" // Same
            };

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for session ID: {sessionId}");
            Assert.AreEqual(sessionId, result.SessionId, $"Session ID should match for: {sessionId}");
            Assert.AreEqual("Test Session", result.SessionName);
        }
    }

    #endregion

    #region GetChanges Tests - Multiple Sequential Calls

    [TestMethod]
    public void GetChanges_MultipleCallsWithSameState_ConsistentResults()
    {
        // Arrange
        var stateUpdate = new SessionStateUpdate(456, "Qualifying Session");
        var currentState = new SessionState
        {
            SessionId = 123,
            SessionName = "Practice Session"
        };

        // Act
        var result1 = stateUpdate.GetChanges(currentState);
        var result2 = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.SessionId, result2.SessionId);
        Assert.AreEqual(result1.SessionName, result2.SessionName);
    }

    [TestMethod]
    public void GetChanges_DifferentStatesSequentially_ReturnsCorrectPatches()
    {
        // Arrange
        var stateUpdate = new SessionStateUpdate(789, "Race Session");

        var state1 = new SessionState
        {
            SessionId = 456, // Different from stateUpdate
            SessionName = "Practice Session" // Different from stateUpdate
        };

        var state2 = new SessionState
        {
            SessionId = 789, // Same as stateUpdate
            SessionName = "Practice Session" // Different from stateUpdate
        };

        var state3 = new SessionState
        {
            SessionId = 789, // Same as stateUpdate
            SessionName = "Race Session" // Same as stateUpdate
        };

        // Act
        var result1 = stateUpdate.GetChanges(state1);
        var result2 = stateUpdate.GetChanges(state2);
        var result3 = stateUpdate.GetChanges(state3);

        // Assert
        Assert.IsNotNull(result1); // Both different
        Assert.AreEqual(789, result1.SessionId);
        Assert.AreEqual("Race Session", result1.SessionName);

        Assert.IsNotNull(result2); // Name different
        Assert.AreEqual(789, result2.SessionId);
        Assert.AreEqual("Race Session", result2.SessionName);

        Assert.IsNull(result3); // Both same
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldSessionTransition_WorksCorrectly()
    {
        // Arrange - Simulate session transition from Practice to Qualifying
        var stateUpdate = new SessionStateUpdate(2, "Qualifying Session");
        
        var currentSessionState = new SessionState
        {
            SessionId = 1,
            SessionName = "Practice Session",
            EventId = 100,
            IsLive = true,
            LapsToGo = 0,
            TimeToGo = "00:30:00"
        };

        // Act
        var result = stateUpdate.GetChanges(currentSessionState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.SessionId);
        Assert.AreEqual("Qualifying Session", result.SessionName);
        
        // Verify that other properties are not touched
        Assert.IsNull(result.EventId);
        Assert.IsNull(result.IsLive);
        Assert.IsNull(result.LapsToGo);
        Assert.IsNull(result.TimeToGo);
    }

    [TestMethod]
    public void GetChanges_RaceWeekendProgression_HandlesSequence()
    {
        // Arrange - Simulate a complete race weekend progression
        var weekendSessions = new[]
        {
            (Id: 1, Name: "Free Practice 1"),
            (Id: 2, Name: "Free Practice 2"),
            (Id: 3, Name: "Free Practice 3"),
            (Id: 4, Name: "Qualifying"),
            (Id: 5, Name: "Sprint Race"),
            (Id: 6, Name: "Main Race")
        };

        var currentState = new SessionState
        {
            SessionId = 0,
            SessionName = "Pre-Event"
        };

        foreach (var session in weekendSessions)
        {
            // Arrange
            var stateUpdate = new SessionStateUpdate(session.Id, session.Name);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for session: {session.Name}");
            Assert.AreEqual(session.Id, result.SessionId, $"Session ID should match for: {session.Name}");
            Assert.AreEqual(session.Name, result.SessionName, $"Session name should match for: {session.Name}");

            // Update current state for next iteration
            currentState.SessionId = session.Id;
            currentState.SessionName = session.Name;
        }
    }

    [TestMethod]
    public void GetChanges_SessionNameUpdate_PreservesSessionId()
    {
        // Arrange - Simulate session name change without ID change
        var stateUpdate = new SessionStateUpdate(10, "Updated Session Name");
        
        var currentSessionState = new SessionState
        {
            SessionId = 10, // Same ID
            SessionName = "Original Session Name", // Different name
            EventId = 500,
            IsLive = true
        };

        // Act
        var result = stateUpdate.GetChanges(currentSessionState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(10, result.SessionId); // Same ID maintained
        Assert.AreEqual("Updated Session Name", result.SessionName); // New name applied
    }

    [TestMethod]
    public void GetChanges_SessionIdUpdate_PreservesSessionName()
    {
        // Arrange - Simulate session ID change without name change
        var stateUpdate = new SessionStateUpdate(20, "Consistent Session Name");
        
        var currentSessionState = new SessionState
        {
            SessionId = 15, // Different ID
            SessionName = "Consistent Session Name", // Same name
            EventId = 600
        };

        // Act
        var result = stateUpdate.GetChanges(currentSessionState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(20, result.SessionId); // New ID applied
        Assert.AreEqual("Consistent Session Name", result.SessionName); // Same name maintained
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void SessionId_Property_ReturnsCorrectValue()
    {
        // Arrange
        var sessionId = 12345;
        var sessionName = "Test Session";

        // Act
        var stateUpdate = new SessionStateUpdate(sessionId, sessionName);

        // Assert
        Assert.AreEqual(12345, stateUpdate.SessionId);
        Assert.AreEqual("Test Session", stateUpdate.SessionName);
    }

    [TestMethod]
    public void SessionName_Property_ReturnsCorrectValue()
    {
        // Arrange
        var sessionId = 67890;
        var sessionName = "Another Test Session";

        // Act
        var stateUpdate = new SessionStateUpdate(sessionId, sessionName);

        // Assert
        Assert.AreEqual(67890, stateUpdate.SessionId);
        Assert.AreEqual("Another Test Session", stateUpdate.SessionName);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SameParameters_ReturnsTrue()
    {
        // Arrange
        var sessionId = 123;
        var sessionName = "Practice Session";

        var stateUpdate1 = new SessionStateUpdate(sessionId, sessionName);
        var stateUpdate2 = new SessionStateUpdate(sessionId, sessionName);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentSessionId_ReturnsFalse()
    {
        // Arrange
        var stateUpdate1 = new SessionStateUpdate(123, "Practice Session");
        var stateUpdate2 = new SessionStateUpdate(456, "Practice Session");

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    [TestMethod]
    public void Equals_DifferentSessionName_ReturnsFalse()
    {
        // Arrange
        var stateUpdate1 = new SessionStateUpdate(123, "Practice Session");
        var stateUpdate2 = new SessionStateUpdate(123, "Qualifying Session");

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    [TestMethod]
    public void Equals_DifferentBothParameters_ReturnsFalse()
    {
        // Arrange
        var stateUpdate1 = new SessionStateUpdate(123, "Practice Session");
        var stateUpdate2 = new SessionStateUpdate(456, "Qualifying Session");

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    [TestMethod]
    public void GetHashCode_SameParameters_SameHashCode()
    {
        // Arrange
        var stateUpdate1 = new SessionStateUpdate(789, "Race Session");
        var stateUpdate2 = new SessionStateUpdate(789, "Race Session");

        // Act & Assert
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void GetHashCode_DifferentParameters_DifferentHashCode()
    {
        // Arrange
        var stateUpdate1 = new SessionStateUpdate(111, "Session One");
        var stateUpdate2 = new SessionStateUpdate(222, "Session Two");

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    #endregion

    #region Performance Tests

    [TestMethod]
    public void GetChanges_HighFrequencyUpdates_PerformsEfficiently()
    {
        // Arrange
        var stateUpdate = new SessionStateUpdate(1000, "Performance Test Session");
        var currentState = new SessionState
        {
            SessionId = 999,
            SessionName = "Different Session"
        };

        // Act & Assert - Run many iterations to test performance
        for (int i = 0; i < 10000; i++)
        {
            var result = stateUpdate.GetChanges(currentState);
            Assert.IsNotNull(result);
            Assert.AreEqual(1000, result.SessionId);
            Assert.AreEqual("Performance Test Session", result.SessionName);
        }
    }

    [TestMethod]
    public void GetChanges_LargeSessionNames_HandlesEfficiently()
    {
        // Arrange
        var largeSessionName = new string('A', 10000); // Very large session name
        var stateUpdate = new SessionStateUpdate(2000, largeSessionName);
        var currentState = new SessionState
        {
            SessionId = 2000,
            SessionName = "Small Name" // Different
        };

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(2000, result.SessionId);
        Assert.AreEqual(largeSessionName, result.SessionName);
        Assert.AreEqual(10000, result.SessionName!.Length);
    }

    #endregion

    #region Thread Safety Tests

    [TestMethod]
    public void GetChanges_ConcurrentAccess_ThreadSafe()
    {
        // Arrange
        var stateUpdate = new SessionStateUpdate(3000, "Concurrent Session");
        var currentState = new SessionState
        {
            SessionId = 2999,
            SessionName = "Previous Session"
        };

        var results = new List<SessionStatePatch?>();
        var tasks = new List<Task>();

        // Act - Run multiple concurrent calls
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var result = stateUpdate.GetChanges(currentState);
                lock (results)
                {
                    results.Add(result);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert
        Assert.AreEqual(100, results.Count);
        Assert.IsTrue(results.All(r => r is not null));
        Assert.IsTrue(results.All(r => r!.SessionId == 3000));
        Assert.IsTrue(results.All(r => r!.SessionName == "Concurrent Session"));
    }

    #endregion
}
