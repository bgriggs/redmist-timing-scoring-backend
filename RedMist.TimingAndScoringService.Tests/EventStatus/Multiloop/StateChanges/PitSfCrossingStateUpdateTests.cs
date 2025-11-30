using Microsoft.AspNetCore.Rewrite;
using RedMist.EventProcessor.EventStatus.Multiloop;
using RedMist.EventProcessor.EventStatus.Multiloop.StateChanges;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.Multiloop.StateChanges;

[TestClass]
public class PitSfCrossingStateUpdateTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidLineCrossing_CreatesInstance()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("42", LineCrossingStatus.Pit);

        // Act
        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(lineCrossing, stateUpdate.LineCrossing);
    }

    #endregion

    #region GetChanges Tests - Pit Status Changes

    [TestMethod]
    public void GetChanges_CrossingStatusPit_CarNotInPitSf_UpdatesToTrue()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("42", LineCrossingStatus.Pit);
        var currentState = new CarPosition
        {
            Number = "42",
            IsPitStartFinish = false // Currently not in pit start/finish
        };

        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsTrue(result.IsPitStartFinish);
    }

    [TestMethod]
    public void GetChanges_CrossingStatusTrack_CarInPitSf_UpdatesToFalse()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("23", LineCrossingStatus.Track);
        var currentState = new CarPosition
        {
            Number = "23",
            IsPitStartFinish = true // Currently in pit start/finish
        };

        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("23", result.Number);
        Assert.IsFalse(result.IsPitStartFinish);
    }

    [TestMethod]
    public void GetChanges_CrossingStatusPit_CarAlreadyInPitSf_ReturnsEmptyPatch()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("88", LineCrossingStatus.Pit);
        var currentState = new CarPosition
        {
            Number = "88",
            IsPitStartFinish = true // Already in pit start/finish
        };

        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("88", result.Number);
        Assert.IsNull(result.IsPitStartFinish); // No change needed
    }

    [TestMethod]
    public void GetChanges_CrossingStatusTrack_CarNotInPitSf_ReturnsEmptyPatch()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("99", LineCrossingStatus.Track);
        var currentState = new CarPosition
        {
            Number = "99",
            IsPitStartFinish = false // Already not in pit start/finish
        };

        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("99", result.Number);
        Assert.IsNull(result.IsPitStartFinish); // No change needed
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_EmptyCarNumber_CopiesEmptyNumber()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("", LineCrossingStatus.Pit);
        var currentState = new CarPosition
        {
            Number = "",
            IsPitStartFinish = false
        };

        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("", result.Number);
        Assert.IsTrue(result.IsPitStartFinish);
    }

    [TestMethod]
    public void GetChanges_NullCarNumber_CopiesNullNumber()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("42", LineCrossingStatus.Pit);
        var currentState = new CarPosition
        {
            Number = null,
            IsPitStartFinish = false
        };

        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.Number);
        Assert.IsTrue(result.IsPitStartFinish);
    }

    #endregion

    #region GetChanges Tests - Different Car Numbers

    [TestMethod]
    public void GetChanges_VariousCarNumbers_CopiesCorrectly()
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
            var lineCrossing = CreateLineCrossing(carNumber, LineCrossingStatus.Pit);
            var currentState = new CarPosition
            {
                Number = carNumber,
                IsPitStartFinish = false
            };

            var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for car number: {carNumber}");
            Assert.AreEqual(carNumber, result.Number, $"Car number should match for: {carNumber}");
            Assert.IsTrue(result.IsPitStartFinish, $"IsPitStartFinish should be true for car: {carNumber}");
        }
    }

    #endregion

    #region GetChanges Tests - Multiple Sequential Calls

    [TestMethod]
    public void GetChanges_MultipleCallsWithSameState_ConsistentResults()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("42", LineCrossingStatus.Pit);
        var currentState = new CarPosition
        {
            Number = "42",
            IsPitStartFinish = false
        };

        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Act
        var result1 = stateUpdate.GetChanges(currentState);
        var result2 = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.Number, result2.Number);
        Assert.AreEqual(result1.IsPitStartFinish, result2.IsPitStartFinish);
        Assert.AreEqual("42", result1.Number);
        Assert.IsTrue(result1.IsPitStartFinish);
        Assert.AreEqual("42", result2.Number);
        Assert.IsTrue(result2.IsPitStartFinish);
    }

    [TestMethod]
    public void GetChanges_DifferentStatesSequentially_ReturnsCorrectPatches()
    {
        // Arrange
        var pitLineCrossing = CreateLineCrossing("42", LineCrossingStatus.Pit);
        var trackLineCrossing = CreateLineCrossing("42", LineCrossingStatus.Track);

        var initialState = new CarPosition
        {
            Number = "42",
            IsPitStartFinish = false
        };

        var stateAfterPit = new CarPosition
        {
            Number = "42",
            IsPitStartFinish = true
        };

        var pitStateUpdate = new PitSfCrossingStateUpdate(pitLineCrossing);
        var trackStateUpdate = new PitSfCrossingStateUpdate(trackLineCrossing);

        // Act
        var result1 = pitStateUpdate.GetChanges(initialState);
        var result2 = trackStateUpdate.GetChanges(stateAfterPit);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        
        // First result should change to pit
        Assert.AreEqual("42", result1.Number);
        Assert.IsTrue(result1.IsPitStartFinish);
        
        // Second result should change back to track
        Assert.AreEqual("42", result2.Number);
        Assert.IsFalse(result2.IsPitStartFinish);
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldScenario_WorksCorrectly()
    {
        // Arrange - Simulate a car entering pit start/finish area during a race
        var lineCrossing = CreateLineCrossing("42", LineCrossingStatus.Pit);
        
        var currentCarState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 25,
            Class = "GT3",
            IsPitStartFinish = false, // Car was on track
            IsInPit = false,
            LastLapTime = "01:45.123"
        };

        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsTrue(result.IsPitStartFinish);
        Assert.IsTrue(result.IsInPit);
        
        // Verify that other properties are not affected
        Assert.IsNull(result.LastLapCompleted);
        Assert.IsNull(result.Class);
        Assert.IsNull(result.LastLapTime);
    }

    [TestMethod]
    public void GetChanges_CarExitingPitSfArea_UpdatesCorrectly()
    {
        // Arrange - Simulate a car leaving pit start/finish area
        var lineCrossing = CreateLineCrossing("99", LineCrossingStatus.Track);
        
        var currentCarState = new CarPosition
        {
            Number = "99",
            LastLapCompleted = 12,
            Class = "GTE",
            IsPitStartFinish = true, // Car was in pit start/finish
            IsInPit = true,
            LastLapTime = "02:01.456"
        };

        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("99", result.Number);
        Assert.IsFalse(result.IsPitStartFinish);
    }

    [TestMethod]
    public void GetChanges_MultipleCarScenario_HandlesEachIndependently()
    {
        // Arrange - Test multiple cars with different crossing statuses
        var car42PitCrossing = CreateLineCrossing("42", LineCrossingStatus.Pit);
        var car99TrackCrossing = CreateLineCrossing("99", LineCrossingStatus.Track);
        var car7PitCrossing = CreateLineCrossing("7", LineCrossingStatus.Pit);

        var car42State = new CarPosition { Number = "42", IsPitStartFinish = false };
        var car99State = new CarPosition { Number = "99", IsPitStartFinish = true };
        var car7State = new CarPosition { Number = "7", IsPitStartFinish = false };

        var car42StateUpdate = new PitSfCrossingStateUpdate(car42PitCrossing);
        var car99StateUpdate = new PitSfCrossingStateUpdate(car99TrackCrossing);
        var car7StateUpdate = new PitSfCrossingStateUpdate(car7PitCrossing);

        // Act
        var result42 = car42StateUpdate.GetChanges(car42State);
        var result99 = car99StateUpdate.GetChanges(car99State);
        var result7 = car7StateUpdate.GetChanges(car7State);

        // Assert
        Assert.IsNotNull(result42);
        Assert.AreEqual("42", result42.Number);
        Assert.IsTrue(result42.IsPitStartFinish);

        Assert.IsNotNull(result99);
        Assert.AreEqual("99", result99.Number);
        Assert.IsFalse(result99.IsPitStartFinish);

        Assert.IsNotNull(result7);
        Assert.AreEqual("7", result7.Number);
        Assert.IsTrue(result7.IsPitStartFinish);
    }

    #endregion

    #region Concurrency Tests

    [TestMethod]
    public void GetChanges_ConcurrentCalls_ThreadSafe()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("42", LineCrossingStatus.Pit);
        var currentState = new CarPosition
        {
            Number = "42",
            IsPitStartFinish = false
        };

        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);
        var results = new List<CarPositionPatch?>();

        // Act - Run multiple concurrent calls
        for (int i = 0; i < 10; i++)
        {
            results.Add(stateUpdate.GetChanges(currentState));
        }

        // Assert
        Assert.IsTrue(results.All(r => r is not null));
        Assert.IsTrue(results.All(r => r!.Number == "42"));
        Assert.IsTrue(results.All(r => r!.IsPitStartFinish == true));
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void LineCrossing_Property_ReturnsCorrectValue()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("42", LineCrossingStatus.Pit);

        // Act
        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Assert
        Assert.AreSame(lineCrossing, stateUpdate.LineCrossing);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SameLineCrossingInstance_ReturnsTrue()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("42", LineCrossingStatus.Pit);
        var stateUpdate1 = new PitSfCrossingStateUpdate(lineCrossing);
        var stateUpdate2 = new PitSfCrossingStateUpdate(lineCrossing);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentLineCrossingInstances_ReturnsFalse()
    {
        // Arrange
        var lineCrossing1 = CreateLineCrossing("42", LineCrossingStatus.Pit);
        var lineCrossing2 = CreateLineCrossing("99", LineCrossingStatus.Track);
        var stateUpdate1 = new PitSfCrossingStateUpdate(lineCrossing1);
        var stateUpdate2 = new PitSfCrossingStateUpdate(lineCrossing2);

        // Act & Assert
        Assert.AreNotEqual(stateUpdate1, stateUpdate2);
        Assert.IsFalse(stateUpdate1.Equals(stateUpdate2));
    }

    #endregion

    #region Boundary and Edge Cases

    [TestMethod]
    public void GetChanges_BooleanTransition_True_to_False()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("42", LineCrossingStatus.Track);
        var currentState = new CarPosition
        {
            Number = "42",
            IsPitStartFinish = true
        };

        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsFalse(result.IsPitStartFinish);
    }

    [TestMethod]
    public void GetChanges_BooleanTransition_False_to_True()
    {
        // Arrange
        var lineCrossing = CreateLineCrossing("42", LineCrossingStatus.Pit);
        var currentState = new CarPosition
        {
            Number = "42",
            IsPitStartFinish = false
        };

        var stateUpdate = new PitSfCrossingStateUpdate(lineCrossing);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.IsTrue(result.IsPitStartFinish);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a LineCrossing instance with the specified car number and crossing status for testing.
    /// </summary>
    private static LineCrossing CreateLineCrossing(string carNumber, LineCrossingStatus crossingStatus)
    {
        var lineCrossing = new LineCrossing();

        // Use reflection to set private properties for testing
        var numberProp = typeof(LineCrossing).GetProperty("Number");
        var crossingStatusStrProp = typeof(LineCrossing).GetProperty("CrossingStatusStr");

        numberProp?.SetValue(lineCrossing, carNumber);
        
        // Set the CrossingStatusStr based on the enum value
        var crossingStatusStr = crossingStatus switch
        {
            LineCrossingStatus.Pit => "P",
            LineCrossingStatus.Track => "T",
            _ => "T"
        };
        crossingStatusStrProp?.SetValue(lineCrossing, crossingStatusStr);

        return lineCrossing;
    }

    #endregion
}

/// <summary>
/// Enum representing line crossing status for testing purposes.
/// This should match the actual enum used in the production code.
/// </summary>
public enum LineCrossingStatus
{
    Track,
    Pit
}
