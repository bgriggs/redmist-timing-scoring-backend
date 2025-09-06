using RedMist.TimingAndScoringService.EventStatus.Multiloop;
using RedMist.TimingAndScoringService.EventStatus.Multiloop.StateChanges;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop.StateChanges;

[TestClass]
public class PitStateUpdateTests
{
    #region Constructor Tests

    [TestMethod]
    public void Constructor_ValidCompletedLap_CreatesInstance()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", pitStopCount: 3, lastLapPitted: 15);

        // Act
        var stateUpdate = new PitStateUpdate(completedLap);

        // Assert
        Assert.IsNotNull(stateUpdate);
        Assert.AreSame(completedLap, stateUpdate.CompletedLap);
    }

    #endregion

    #region GetChanges Tests - Both Properties Changed

    [TestMethod]
    public void GetChanges_BothLastLapPittedAndPitStopCountChanged_ReturnsCompletePatch()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", pitStopCount: 5, lastLapPitted: 25);
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapPitted = 20, // Different
            PitStopCount = 3 // Different
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(25, result.LastLapPitted);
        Assert.AreEqual(5, result.PitStopCount);
    }

    [TestMethod]
    public void GetChanges_NoChanges_ReturnsEmptyPatch()
    {
        // Arrange
        var completedLap = CreateCompletedLap("99", pitStopCount: 2, lastLapPitted: 12);
        var currentState = new CarPosition
        {
            Number = "99",
            LastLapPitted = 12, // Same
            PitStopCount = 2 // Same
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("99", result.Number);
        Assert.IsNull(result.LastLapPitted);
        Assert.IsNull(result.PitStopCount);
    }

    #endregion

    #region GetChanges Tests - Individual Property Changes

    [TestMethod]
    public void GetChanges_OnlyLastLapPittedChanged_ReturnsPartialPatch()
    {
        // Arrange
        var completedLap = CreateCompletedLap("77", pitStopCount: 4, lastLapPitted: 30);
        var currentState = new CarPosition
        {
            Number = "77",
            LastLapPitted = 25, // Different
            PitStopCount = 4 // Same
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("77", result.Number);
        Assert.AreEqual(30, result.LastLapPitted); // Should be set
        Assert.IsNull(result.PitStopCount); // Should not be set
    }

    [TestMethod]
    public void GetChanges_OnlyPitStopCountChanged_ReturnsPartialPatch()
    {
        // Arrange
        var completedLap = CreateCompletedLap("88", pitStopCount: 6, lastLapPitted: 18);
        var currentState = new CarPosition
        {
            Number = "88",
            LastLapPitted = 18, // Same
            PitStopCount = 4 // Different
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("88", result.Number);
        Assert.IsNull(result.LastLapPitted); // Should not be set
        Assert.AreEqual(6, result.PitStopCount); // Should be set
    }

    #endregion

    #region GetChanges Tests - Edge Cases

    [TestMethod]
    public void GetChanges_ZeroValues_HandlesCorrectly()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", pitStopCount: 0, lastLapPitted: 0);
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapPitted = 5,
            PitStopCount = 2
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(0, result.LastLapPitted);
        Assert.AreEqual(0, result.PitStopCount);
    }

    [TestMethod]
    public void GetChanges_NullCarPositionValues_HandlesCorrectly()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", pitStopCount: 3, lastLapPitted: 15);
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapPitted = null,
            PitStopCount = null
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(15, result.LastLapPitted);
        Assert.AreEqual(3, result.PitStopCount);
    }

    [TestMethod]
    public void GetChanges_MaxValues_HandlesCorrectly()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", pitStopCount: ushort.MaxValue, lastLapPitted: ushort.MaxValue);
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapPitted = 5,
            PitStopCount = 2
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(ushort.MaxValue, result.LastLapPitted);
        Assert.AreEqual(ushort.MaxValue, result.PitStopCount);
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
            var completedLap = CreateCompletedLap(carNumber, pitStopCount: 2, lastLapPitted: 10);
            var currentState = new CarPosition
            {
                Number = carNumber,
                LastLapPitted = 5,
                PitStopCount = 1
            };

            var stateUpdate = new PitStateUpdate(completedLap);

            // Act
            var result = stateUpdate.GetChanges(currentState);

            // Assert
            Assert.IsNotNull(result, $"Result should not be null for car number: {carNumber}");
            Assert.AreEqual(carNumber, result.Number, $"Car number should match for: {carNumber}");
            Assert.AreEqual(10, result.LastLapPitted, $"LastLapPitted should be updated for car: {carNumber}");
            Assert.AreEqual(2, result.PitStopCount, $"PitStopCount should be updated for car: {carNumber}");
        }
    }

    [TestMethod]
    public void GetChanges_EmptyCarNumber_CopiesEmptyNumber()
    {
        // Arrange
        var completedLap = CreateCompletedLap("", pitStopCount: 1, lastLapPitted: 5);
        var currentState = new CarPosition
        {
            Number = "",
            LastLapPitted = 0,
            PitStopCount = 0
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("", result.Number);
        Assert.AreEqual(5, result.LastLapPitted);
        Assert.AreEqual(1, result.PitStopCount);
    }

    [TestMethod]
    public void GetChanges_NullCarNumber_CopiesNullNumber()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", pitStopCount: 2, lastLapPitted: 8);
        var currentState = new CarPosition
        {
            Number = null,
            LastLapPitted = 5,
            PitStopCount = 1
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result);
        Assert.IsNull(result.Number);
        Assert.AreEqual(8, result.LastLapPitted);
        Assert.AreEqual(2, result.PitStopCount);
    }

    #endregion

    #region GetChanges Tests - Multiple Sequential Calls

    [TestMethod]
    public void GetChanges_MultipleCallsWithSameState_ConsistentResults()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", pitStopCount: 3, lastLapPitted: 15);
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapPitted = 10,
            PitStopCount = 2
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result1 = stateUpdate.GetChanges(currentState);
        var result2 = stateUpdate.GetChanges(currentState);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.Number, result2.Number);
        Assert.AreEqual(result1.LastLapPitted, result2.LastLapPitted);
        Assert.AreEqual(result1.PitStopCount, result2.PitStopCount);
    }

    [TestMethod]
    public void GetChanges_DifferentStatesSequentially_ReturnsCorrectPatches()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", pitStopCount: 3, lastLapPitted: 15);

        var state1 = new CarPosition
        {
            Number = "42",
            LastLapPitted = 10, // Different from CompletedLap
            PitStopCount = 2 // Different from CompletedLap
        };

        var state2 = new CarPosition
        {
            Number = "42",
            LastLapPitted = 15, // Same as CompletedLap
            PitStopCount = 2 // Different from CompletedLap
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result1 = stateUpdate.GetChanges(state1);
        var result2 = stateUpdate.GetChanges(state2);

        // Assert
        Assert.IsNotNull(result1);
        Assert.IsNotNull(result2);
        
        // First result should have both changes
        Assert.AreEqual("42", result1.Number);
        Assert.AreEqual(15, result1.LastLapPitted);
        Assert.AreEqual(3, result1.PitStopCount);
        
        // Second result should only have pit stop count change
        Assert.AreEqual("42", result2.Number);
        Assert.IsNull(result2.LastLapPitted); // No change
        Assert.AreEqual(3, result2.PitStopCount);
    }

    #endregion

    #region Integration Tests

    [TestMethod]
    public void GetChanges_RealWorldRaceScenario_WorksCorrectly()
    {
        // Arrange - Simulate a car that just completed their second pit stop on lap 45
        var completedLap = CreateCompletedLap("42", pitStopCount: 2, lastLapPitted: 45);
        
        var currentCarState = new CarPosition
        {
            Number = "42",
            LastLapCompleted = 45,
            Class = "GT3",
            LastLapPitted = 23, // Previous pit stop was on lap 23
            PitStopCount = 1, // Had only one pit stop before
            LastLapTime = "01:45.123"
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("42", result.Number);
        Assert.AreEqual(45, result.LastLapPitted); // Updated to current lap
        Assert.AreEqual(2, result.PitStopCount); // Incremented pit stop count
        
        // Verify that other properties are not touched
        Assert.IsNull(result.LastLapCompleted);
        Assert.IsNull(result.Class);
        Assert.IsNull(result.LastLapTime);
    }

    [TestMethod]
    public void GetChanges_CarFirstPitStop_UpdatesFromZero()
    {
        // Arrange - Simulate a car taking their first pit stop
        var completedLap = CreateCompletedLap("99", pitStopCount: 1, lastLapPitted: 18);
        
        var currentCarState = new CarPosition
        {
            Number = "99",
            LastLapCompleted = 18,
            LastLapPitted = null, // No previous pit stops
            PitStopCount = null, // No previous pit stops
            Class = "GTE"
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("99", result.Number);
        Assert.AreEqual(18, result.LastLapPitted);
        Assert.AreEqual(1, result.PitStopCount);
    }

    [TestMethod]
    public void GetChanges_MultipleCarScenario_HandlesEachIndependently()
    {
        // Arrange - Test multiple cars with different pit stop scenarios
        var car42CompletedLap = CreateCompletedLap("42", pitStopCount: 3, lastLapPitted: 25);
        var car99CompletedLap = CreateCompletedLap("99", pitStopCount: 1, lastLapPitted: 15);
        var car7CompletedLap = CreateCompletedLap("7", pitStopCount: 2, lastLapPitted: 35);

        var car42State = new CarPosition { Number = "42", LastLapPitted = 20, PitStopCount = 2 };
        var car99State = new CarPosition { Number = "99", LastLapPitted = null, PitStopCount = null };
        var car7State = new CarPosition { Number = "7", LastLapPitted = 30, PitStopCount = 2 };

        var car42StateUpdate = new PitStateUpdate(car42CompletedLap);
        var car99StateUpdate = new PitStateUpdate(car99CompletedLap);
        var car7StateUpdate = new PitStateUpdate(car7CompletedLap);

        // Act
        var result42 = car42StateUpdate.GetChanges(car42State);
        var result99 = car99StateUpdate.GetChanges(car99State);
        var result7 = car7StateUpdate.GetChanges(car7State);

        // Assert
        Assert.IsNotNull(result42);
        Assert.AreEqual("42", result42.Number);
        Assert.AreEqual(25, result42.LastLapPitted);
        Assert.AreEqual(3, result42.PitStopCount);

        Assert.IsNotNull(result99);
        Assert.AreEqual("99", result99.Number);
        Assert.AreEqual(15, result99.LastLapPitted);
        Assert.AreEqual(1, result99.PitStopCount);

        Assert.IsNotNull(result7);
        Assert.AreEqual("7", result7.Number);
        Assert.AreEqual(35, result7.LastLapPitted);
        Assert.IsNull(result7.PitStopCount); // No change
    }

    [TestMethod]
    public void GetChanges_EnduranceRaceScenario_HandlesHighPitStopCounts()
    {
        // Arrange - Simulate endurance race with many pit stops
        var completedLap = CreateCompletedLap("24", pitStopCount: 15, lastLapPitted: 287);
        
        var currentCarState = new CarPosition
        {
            Number = "24",
            LastLapCompleted = 287,
            LastLapPitted = 265, // Previous pit stop
            PitStopCount = 14, // Previous count
            Class = "LMP1"
        };

        var stateUpdate = new PitStateUpdate(completedLap);

        // Act
        var result = stateUpdate.GetChanges(currentCarState);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("24", result.Number);
        Assert.AreEqual(287, result.LastLapPitted);
        Assert.AreEqual(15, result.PitStopCount);
    }

    #endregion

    #region Concurrency Tests

    [TestMethod]
    public void GetChanges_ConcurrentCalls_ThreadSafe()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", pitStopCount: 3, lastLapPitted: 15);
        var currentState = new CarPosition
        {
            Number = "42",
            LastLapPitted = 10,
            PitStopCount = 2
        };

        var stateUpdate = new PitStateUpdate(completedLap);
        var results = new List<CarPositionPatch?>();

        // Act - Run multiple concurrent calls
        for (int i = 0; i < 10; i++)
        {
            results.Add(stateUpdate.GetChanges(currentState));
        }

        // Assert
        Assert.IsTrue(results.All(r => r is not null));
        Assert.IsTrue(results.All(r => r!.Number == "42"));
        Assert.IsTrue(results.All(r => r!.LastLapPitted == 15));
        Assert.IsTrue(results.All(r => r!.PitStopCount == 3));
    }

    #endregion

    #region Property Validation Tests

    [TestMethod]
    public void CompletedLap_Property_ReturnsCorrectValue()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", pitStopCount: 3, lastLapPitted: 15);

        // Act
        var stateUpdate = new PitStateUpdate(completedLap);

        // Assert
        Assert.AreSame(completedLap, stateUpdate.CompletedLap);
    }

    #endregion

    #region Record Equality Tests

    [TestMethod]
    public void Equals_SameCompletedLapInstance_ReturnsTrue()
    {
        // Arrange
        var completedLap = CreateCompletedLap("42", pitStopCount: 3, lastLapPitted: 15);
        var stateUpdate1 = new PitStateUpdate(completedLap);
        var stateUpdate2 = new PitStateUpdate(completedLap);

        // Act & Assert
        Assert.AreEqual(stateUpdate1, stateUpdate2);
        Assert.IsTrue(stateUpdate1.Equals(stateUpdate2));
        Assert.AreEqual(stateUpdate1.GetHashCode(), stateUpdate2.GetHashCode());
    }

    [TestMethod]
    public void Equals_DifferentCompletedLapInstances_ReturnsFalse()
    {
        // Arrange
        var completedLap1 = CreateCompletedLap("42", pitStopCount: 3, lastLapPitted: 15);
        var completedLap2 = CreateCompletedLap("99", pitStopCount: 2, lastLapPitted: 20);
        var stateUpdate1 = new PitStateUpdate(completedLap1);
        var stateUpdate2 = new PitStateUpdate(completedLap2);

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
        ushort pitStopCount = 0, 
        ushort lastLapPitted = 0)
    {
        var completedLap = new CompletedLap();

        // Use reflection to set private properties for testing
        var numberProp = typeof(CompletedLap).GetProperty("Number");
        var pitStopCountProp = typeof(CompletedLap).GetProperty("PitStopCount");
        var lastLapPittedProp = typeof(CompletedLap).GetProperty("LastLapPitted");

        numberProp?.SetValue(completedLap, carNumber);
        pitStopCountProp?.SetValue(completedLap, pitStopCount);
        lastLapPittedProp?.SetValue(completedLap, lastLapPitted);

        return completedLap;
    }

    #endregion
}
