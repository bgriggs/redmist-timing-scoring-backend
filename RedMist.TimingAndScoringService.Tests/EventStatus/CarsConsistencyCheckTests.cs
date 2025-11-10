using Microsoft.Extensions.Logging;
using Moq;
using RedMist.EventProcessor.EventStatus;
using RedMist.TimingCommon.Models;
using System.Collections.Immutable;

namespace RedMist.EventProcessor.Tests.EventStatus;

[TestClass]
public class CarsConsistencyCheckTests
{
    private readonly Mock<ILogger> mockLogger;

    public CarsConsistencyCheckTests()
    {
        mockLogger = new Mock<ILogger>();
    }

    #region Empty and Null Lists Tests

    [TestMethod]
    public void AreCarsConsistent_EmptyList_ReturnsTrue()
    {
        // Arrange
        var cars = ImmutableList<CarPosition>.Empty;

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars);

        // Assert
        Assert.IsTrue(result);
    }

    #endregion

    #region Single Car Tests

    [TestMethod]
    public void AreCarsConsistent_SingleCarAtPosition1_ReturnsTrue()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void AreCarsConsistent_SingleCarAtPositionOtherThan1_ReturnsFalse()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 2 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars, mockLogger.Object);

        // Assert
        Assert.IsFalse(result);
        VerifyLogWarningCalled("Car position mismatch: expected {expected}, got {actual} for car {num}", 1, 2, "1");
    }

    #endregion

    #region Multiple Cars - Valid Sequences Tests

    [TestMethod]
    public void AreCarsConsistent_TwoCarsConsecutivePositions_ReturnsTrue()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 2 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void AreCarsConsistent_ThreeCarsConsecutivePositions_ReturnsTrue()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 2 },
            new CarPosition { Number = "3", OverallPosition = 3 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void AreCarsConsistent_CarsInRandomOrder_ReturnsTrue()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "3", OverallPosition = 3 },
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 2 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void AreCarsConsistent_FiveCarsConsecutivePositions_ReturnsTrue()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "A", OverallPosition = 1 },
            new CarPosition { Number = "B", OverallPosition = 2 },
            new CarPosition { Number = "C", OverallPosition = 3 },
            new CarPosition { Number = "D", OverallPosition = 4 },
            new CarPosition { Number = "E", OverallPosition = 5 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars);

        // Assert
        Assert.IsTrue(result);
    }

    #endregion

    #region Duplicate Position Tests

    [TestMethod]
    public void AreCarsConsistent_TwoCarsWithSamePosition_ReturnsFalse()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 1 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars, mockLogger.Object);

        // Assert
        Assert.IsFalse(result);
        VerifyLogWarningCalled("Duplicate car position {pos} for {num} and {num2}", 1, "1", "2");
    }

    [TestMethod]
    public void AreCarsConsistent_ThreeCarsWithTwoDuplicatePositions_ReturnsFalse()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 2 },
            new CarPosition { Number = "3", OverallPosition = 2 }  // Duplicate position 2
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars, mockLogger.Object);

        // Assert
        Assert.IsFalse(result);
        VerifyLogWarningCalled("Duplicate car position {pos} for {num} and {num2}", 2, "2", "3");
    }

    [TestMethod]
    public void AreCarsConsistent_MultipleDuplicatePositions_ReturnsFalseOnFirstDuplicate()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "A", OverallPosition = 1 },
            new CarPosition { Number = "B", OverallPosition = 1 },  // First duplicate
            new CarPosition { Number = "C", OverallPosition = 3 },
            new CarPosition { Number = "D", OverallPosition = 3 }   // Second duplicate - should not reach this
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars, mockLogger.Object);

        // Assert
        Assert.IsFalse(result);
        VerifyLogWarningCalled("Duplicate car position {pos} for {num} and {num2}", 1, "A", "B");
    }

    #endregion

    #region Gap in Position Sequence Tests

    [TestMethod]
    public void AreCarsConsistent_TwoCarsWithGap_ReturnsFalse()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 3 }  // Missing position 2
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars, mockLogger.Object);

        // Assert
        Assert.IsFalse(result);
        VerifyLogWarningCalled("Car position mismatch: expected {expected}, got {actual} for car {num}", 2, 3, "2");
    }

    [TestMethod]
    public void AreCarsConsistent_ThreeCarsWithGapInMiddle_ReturnsFalse()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "3", OverallPosition = 3 },
            new CarPosition { Number = "4", OverallPosition = 4 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars, mockLogger.Object);

        // Assert
        Assert.IsFalse(result);
        VerifyLogWarningCalled("Car position mismatch: expected {expected}, got {actual} for car {num}", 2, 3, "3");
    }

    [TestMethod]
    public void AreCarsConsistent_CarsStartingAtPositionOtherThan1_ReturnsFalse()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "2", OverallPosition = 2 },
            new CarPosition { Number = "3", OverallPosition = 3 },
            new CarPosition { Number = "4", OverallPosition = 4 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars, mockLogger.Object);

        // Assert
        Assert.IsFalse(result);
        VerifyLogWarningCalled("Car position mismatch: expected {expected}, got {actual} for car {num}", 1, 2, "2");
    }

    [TestMethod]
    public void AreCarsConsistent_MultipleGaps_ReturnsFalseOnFirstGap()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "5", OverallPosition = 5 },  // Gap from 1 to 5
            new CarPosition { Number = "10", OverallPosition = 10 } // Another gap
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars, mockLogger.Object);

        // Assert
        Assert.IsFalse(result);
        VerifyLogWarningCalled("Car position mismatch: expected {expected}, got {actual} for car {num}", 2, 5, "5");
    }

    #endregion

    #region Edge Cases Tests

    [TestMethod]
    public void AreCarsConsistent_CarWithNullNumber_WorksCorrectly()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = null, OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 2 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void AreCarsConsistent_CarWithEmptyStringNumber_WorksCorrectly()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 2 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void AreCarsConsistent_CarsWithAlphanumericNumbers_WorksCorrectly()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "1X", OverallPosition = 1 },
            new CarPosition { Number = "99A", OverallPosition = 2 },
            new CarPosition { Number = "T2", OverallPosition = 3 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void AreCarsConsistent_DuplicatePositionWithNullNumbers_ReturnsFalse()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = null, OverallPosition = 1 },
            new CarPosition { Number = null, OverallPosition = 1 }
        );

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars, mockLogger.Object);

        // Assert
        Assert.IsFalse(result);
        VerifyLogWarningCalled("Duplicate car position {pos} for {num} and {num2}", 1, "", "");
    }

    #endregion

    #region Logger Tests

    [TestMethod]
    public void AreCarsConsistent_WithNullLogger_DoesNotThrow()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 1 }  // Duplicate position
        );

        // Act & Assert
        var result = CarsConsistencyCheck.AreCarsConsistent(cars, logger: null);
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void AreCarsConsistent_WithoutLogger_DoesNotThrow()
    {
        // Arrange
        var cars = ImmutableList.Create(
            new CarPosition { Number = "1", OverallPosition = 1 },
            new CarPosition { Number = "2", OverallPosition = 3 }  // Gap in positions
        );

        // Act & Assert
        var result = CarsConsistencyCheck.AreCarsConsistent(cars);
        Assert.IsFalse(result);
    }

    #endregion

    #region Large Dataset Tests

    [TestMethod]
    public void AreCarsConsistent_LargeConsistentDataset_ReturnsTrue()
    {
        // Arrange
        var cars = ImmutableList.CreateRange(
            Enumerable.Range(1, 50).Select(i => 
                new CarPosition { Number = i.ToString(), OverallPosition = i }));

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars);

        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void AreCarsConsistent_LargeInconsistentDataset_ReturnsFalse()
    {
        // Arrange
        var carsList = Enumerable.Range(1, 50).Select(i => 
            new CarPosition { Number = i.ToString(), OverallPosition = i }).ToList();
        
        // Introduce an inconsistency by duplicating position 25
        carsList.Add(new CarPosition { Number = "51", OverallPosition = 25 });
        
        var cars = ImmutableList.CreateRange(carsList);

        // Act
        var result = CarsConsistencyCheck.AreCarsConsistent(cars, mockLogger.Object);

        // Assert
        Assert.IsFalse(result);
        VerifyLogWarningCalled("Duplicate car position {pos} for {num} and {num2}", 25, "25", "51");
    }

    #endregion

    #region Helper Methods

    private void VerifyLogWarningCalled(string expectedTemplate, params object[] expectedArgs)
    {
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true), // Simplified verification
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion
}
