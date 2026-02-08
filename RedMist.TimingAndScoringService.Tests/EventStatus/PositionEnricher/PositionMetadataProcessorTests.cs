using RedMist.EventProcessor.EventStatus.PositionEnricher;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.PositionEnricher;

[TestClass]
public class PositionMetadataProcessorTests
{
    #region UpdatePositionChanges Tests

    [TestMethod]
    public void UpdatePositionChanges_CarWithValidOverallPositionsButMissingClassData_CalculatesOverallGained()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 1,
                OverallStartingPosition = 3,  // Gained 2 positions (realistic with 3 cars)
                ClassPosition = 0,  // Missing class position - will be set by UpdateClassPositions
                InClassStartingPosition = 0,  // Missing class starting position
                OverallPositionsGained = 0,
                InClassPositionsGained = 0,
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 2,
                OverallStartingPosition = 2,
                ClassPosition = 0,
                InClassStartingPosition = 0,
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "3",
                Class = "GT3",
                OverallPosition = 3,
                OverallStartingPosition = 1,
                ClassPosition = 0,
                InClassStartingPosition = 0,
                TotalTime = "00:10:02.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert - Find car by number since list gets sorted
        var car1 = positions.First(p => p.Number == "1");

        // Should calculate overall positions gained since overall data is valid
        Assert.AreEqual(2, car1.OverallPositionsGained, "Should calculate OverallPositionsGained when overall data is valid");

        // Should mark class positions gained as invalid since InClassStartingPosition is 0
        Assert.AreEqual(CarPosition.InvalidPosition, car1.InClassPositionsGained, "Should mark InClassPositionsGained as invalid when InClassStartingPosition is missing");
    }

    [TestMethod]
    public void UpdatePositionChanges_CarWithValidClassPositionsButMissingOverallData_CalculatesClassGained()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 0,  // Missing overall position
                OverallStartingPosition = 0,  // Missing overall starting position
                ClassPosition = 0,  // Will be set by UpdateClassPositions
                InClassStartingPosition = 3,  // Valid - with 3 cars, gain of 2 is possible
                OverallPositionsGained = 0,
                InClassPositionsGained = 0,
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 0,
                OverallStartingPosition = 0,
                ClassPosition = 0,
                InClassStartingPosition = 2,
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "3",
                Class = "GT3",
                OverallPosition = 0,
                OverallStartingPosition = 0,
                ClassPosition = 0,
                InClassStartingPosition = 1,
                TotalTime = "00:10:02.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert - Find car by number
        var car1 = positions.First(p => p.Number == "1");

        // Should mark overall positions gained as invalid since overall data is missing
        Assert.AreEqual(CarPosition.InvalidPosition, car1.OverallPositionsGained, "Should mark OverallPositionsGained as invalid when overall data is missing");

        // Should calculate class positions gained since class data is valid
        // UpdateClassPositions will set ClassPosition to 1 (first car when sorted by OverallPosition with 0s last)
        // Gained: 3 - 1 = 2
        Assert.AreEqual(2, car1.InClassPositionsGained, "Should calculate InClassPositionsGained when class data is valid");
    }

    [TestMethod]
    public void UpdatePositionChanges_MostPositionsGained_OnlyAwardsWhenSingleWinner()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 1,
                OverallStartingPosition = 5,  // Gained 4 positions with 5 cars total
                ClassPosition = 1,
                InClassStartingPosition = 3,
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 2,
                OverallStartingPosition = 4,  // Gained 2 positions
                ClassPosition = 2,
                InClassStartingPosition = 2,
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "3",
                Class = "GT3",
                OverallPosition = 3,
                OverallStartingPosition = 3,  // Gained 0 positions
                ClassPosition = 3,
                InClassStartingPosition = 1,
                TotalTime = "00:10:02.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "4",
                Class = "GT3",
                OverallPosition = 4,
                OverallStartingPosition = 2,
                ClassPosition = 4,
                InClassStartingPosition = 4,
                TotalTime = "00:10:03.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "5",
                Class = "GT3",
                OverallPosition = 5,
                OverallStartingPosition = 1,
                ClassPosition = 5,
                InClassStartingPosition = 5,
                TotalTime = "00:10:04.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert - Find cars by number since list gets sorted
        var car1 = positions.First(p => p.Number == "1");
        var car2 = positions.First(p => p.Number == "2");
        var car3 = positions.First(p => p.Number == "3");

        // Car 1 gained 4 positions (most)
        Assert.AreEqual(4, car1.OverallPositionsGained);
        Assert.IsTrue(car1.IsOverallMostPositionsGained, "Car with most positions gained should be marked");

        // Car 2 gained 2 positions
        Assert.AreEqual(2, car2.OverallPositionsGained);
        Assert.IsFalse(car2.IsOverallMostPositionsGained);

        // Car 3 gained 0 positions
        Assert.AreEqual(0, car3.OverallPositionsGained);
        Assert.IsFalse(car3.IsOverallMostPositionsGained);
    }

    [TestMethod]
    public void UpdatePositionChanges_TiedForMostPositionsGained_NoOneGetsAward()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 1,
                OverallStartingPosition = 3,  // Both cars gained 2 positions with 4 cars total
                ClassPosition = 1,
                InClassStartingPosition = 3,
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 2,
                OverallStartingPosition = 4,  // Also gained 2 positions
                ClassPosition = 2,
                InClassStartingPosition = 4,
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "3",
                Class = "GT3",
                OverallPosition = 3,
                OverallStartingPosition = 2,
                ClassPosition = 3,
                InClassStartingPosition = 2,
                TotalTime = "00:10:02.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "4",
                Class = "GT3",
                OverallPosition = 4,
                OverallStartingPosition = 1,
                ClassPosition = 4,
                InClassStartingPosition = 1,
                TotalTime = "00:10:03.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert - Find cars by number since list gets sorted
        var car1 = positions.First(p => p.Number == "1");
        var car2 = positions.First(p => p.Number == "2");

        // Both gained 2 positions - tied
        Assert.AreEqual(2, car1.OverallPositionsGained);
        Assert.AreEqual(2, car2.OverallPositionsGained);

        // Neither should be marked as most gained due to tie
        Assert.IsFalse(car1.IsOverallMostPositionsGained, "Tied cars should not be marked as most gained");
        Assert.IsFalse(car2.IsOverallMostPositionsGained, "Tied cars should not be marked as most gained");
    }

    [TestMethod]
    public void UpdatePositionChanges_ClassMostPositionsGained_CalculatedPerClass()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            // GT3 Class - 3 cars
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 1,
                OverallStartingPosition = 5,
                ClassPosition = 1,
                InClassStartingPosition = 3,  // Gained 2 in class (3 GT3 cars total, so max gain is 2)
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 2,
                OverallStartingPosition = 4,
                ClassPosition = 2,
                InClassStartingPosition = 2,  // Gained 0 in class
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "5",
                Class = "GT3",
                OverallPosition = 5,
                OverallStartingPosition = 6,
                ClassPosition = 3,
                InClassStartingPosition = 1,  // Lost 2 in class
                TotalTime = "00:10:04.000",
                LastLapCompleted = 10
            },
            // LMP2 Class - 2 cars
            new CarPosition
            {
                Number = "3",
                Class = "LMP2",
                OverallPosition = 3,
                OverallStartingPosition = 10,
                ClassPosition = 1,
                InClassStartingPosition = 2,  // Gained 1 in class (only 2 LMP2 cars, so max gain is 1)
                TotalTime = "00:10:02.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "4",
                Class = "LMP2",
                OverallPosition = 4,
                OverallStartingPosition = 8,
                ClassPosition = 2,
                InClassStartingPosition = 1,  // Lost 1 in class
                TotalTime = "00:10:03.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert - Find cars by number since list gets sorted
        var car1 = positions.First(p => p.Number == "1");
        var car2 = positions.First(p => p.Number == "2");
        var car3 = positions.First(p => p.Number == "3");
        var car4 = positions.First(p => p.Number == "4");

        // GT3: Car 1 gained most in class (2)
        Assert.AreEqual(2, car1.InClassPositionsGained);
        Assert.IsTrue(car1.IsClassMostPositionsGained, "Car 1 should be marked as most gained in GT3 class");
        Assert.IsFalse(car2.IsClassMostPositionsGained);

        // LMP2: Car 3 gained most in class (1)
        Assert.AreEqual(1, car3.InClassPositionsGained);
        Assert.IsTrue(car3.IsClassMostPositionsGained, "Car 3 should be marked as most gained in LMP2 class");
        Assert.IsFalse(car4.IsClassMostPositionsGained);
    }

    [TestMethod]
    public void UpdatePositionChanges_NegativePositionsGained_NotMarkedAsMostGained()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 5,
                OverallStartingPosition = 2,  // Lost 3 positions (negative gain) - need 5 cars for this to be valid
                ClassPosition = 1,
                InClassStartingPosition = 1,
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 1,  // Gained 2 positions
                OverallStartingPosition = 3,
                ClassPosition = 2,
                InClassStartingPosition = 2,
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "3",
                Class = "GT3",
                OverallPosition = 2,
                OverallStartingPosition = 4,
                ClassPosition = 3,
                InClassStartingPosition = 3,
                TotalTime = "00:10:02.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "4",
                Class = "GT3",
                OverallPosition = 3,
                OverallStartingPosition = 5,
                ClassPosition = 4,
                InClassStartingPosition = 4,
                TotalTime = "00:10:03.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "5",
                Class = "GT3",
                OverallPosition = 4,
                OverallStartingPosition = 1,
                ClassPosition = 5,
                InClassStartingPosition = 5,
                TotalTime = "00:10:04.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert - Find cars by number since list gets sorted during processing
        var car1 = positions.First(p => p.Number == "1");
        var car2 = positions.First(p => p.Number == "2");
        var car3 = positions.First(p => p.Number == "3");
        var car4 = positions.First(p => p.Number == "4");

        Assert.AreEqual(-3, car1.OverallPositionsGained, "Should calculate negative positions (losses)");
        Assert.AreEqual(2, car2.OverallPositionsGained);
        Assert.AreEqual(2, car3.OverallPositionsGained);
        Assert.AreEqual(2, car4.OverallPositionsGained);

        // Multiple cars gained 2 positions (tied), so none should be marked
        Assert.IsFalse(car1.IsOverallMostPositionsGained, "Car that lost positions should not be marked");
        Assert.IsFalse(car2.IsOverallMostPositionsGained, "Tied cars should not be marked");
        Assert.IsFalse(car3.IsOverallMostPositionsGained, "Tied cars should not be marked");
        Assert.IsFalse(car4.IsOverallMostPositionsGained, "Tied cars should not be marked");
    }

    [TestMethod]
    public void UpdatePositionChanges_CarWithInvalidOverallPositionZero_OverallMarkedAsInvalid()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 0,  // Invalid - UpdateClassPositions will set ClassPosition
                OverallStartingPosition = 5,
                ClassPosition = 0,  // Will be set by UpdateClassPositions
                InClassStartingPosition = 3,
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert
        Assert.AreEqual(CarPosition.InvalidPosition, positions[0].OverallPositionsGained, "Should be invalid when OverallPosition is 0");
        // Note: ClassPosition will be set by UpdateClassPositions, so InClassPositionsGained may be valid
        // depending on InClassStartingPosition
        Assert.IsFalse(positions[0].IsOverallMostPositionsGained);
    }

    [TestMethod]
    public void UpdatePositionChanges_MixedValidAndInvalidData_HandlesCorrectly()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            // Valid overall, missing class starting position
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 1,
                OverallStartingPosition = 5,  // Gained 4 positions with 5 cars total
                ClassPosition = 0,  // Will be set by UpdateClassPositions
                InClassStartingPosition = 0,  // Missing - class gain should be invalid
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            // Missing overall, valid class starting position
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 0,
                OverallStartingPosition = 0,
                ClassPosition = 1,  // Will be updated by UpdateClassPositions
                InClassStartingPosition = 3,  // Valid with 3 GT3 cars
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            },
            // All valid
            new CarPosition
            {
                Number = "3",
                Class = "GT3",
                OverallPosition = 3,
                OverallStartingPosition = 4,  // Gained 1 position
                ClassPosition = 2,
                InClassStartingPosition = 2,
                TotalTime = "00:10:02.000",
                LastLapCompleted = 10
            },
            // Add more cars to make starting position 5 realistic
            new CarPosition
            {
                Number = "4",
                Class = "LMP2",
                OverallPosition = 2,
                OverallStartingPosition = 2,
                ClassPosition = 1,
                InClassStartingPosition = 1,
                TotalTime = "00:10:03.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "5",
                Class = "LMP2",
                OverallPosition = 4,
                OverallStartingPosition = 1,
                ClassPosition = 2,
                InClassStartingPosition = 2,
                TotalTime = "00:10:04.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert - Find cars by number since list gets sorted during processing
        var car1 = positions.First(p => p.Number == "1");
        var car2 = positions.First(p => p.Number == "2");
        var car3 = positions.First(p => p.Number == "3");

        // Car 1: Valid overall, invalid class (InClassStartingPosition is 0)
        Assert.AreEqual(4, car1.OverallPositionsGained, "Car 1 gained 4 positions (5->1)");
        Assert.AreEqual(CarPosition.InvalidPosition, car1.InClassPositionsGained, "Car 1 should have invalid class gain when InClassStartingPosition is 0");
        Assert.IsTrue(car1.IsOverallMostPositionsGained, "Car 1 should win overall most gained");

        // Car 2: Invalid overall (OverallPosition=0, OverallStartingPosition=0), valid class
        // Note: UpdateClassPositions will set ClassPosition, and with InClassStartingPosition=3, it should calculate gain
        Assert.AreEqual(CarPosition.InvalidPosition, car2.OverallPositionsGained, "Car 2 should have invalid overall when OverallPosition is 0");
        // ClassPosition will be set by UpdateClassPositions. Since car 2 has OverallPosition=0, it will be sorted last in class (position 3)
        // Gain: 3 - 3 = 0
        Assert.AreNotEqual(CarPosition.InvalidPosition, car2.InClassPositionsGained, "Car 2 should have valid class gain when class data is valid");

        // Car 3: Both valid
        Assert.AreEqual(1, car3.OverallPositionsGained, "Car 3 gained 1 position (4->3)");
        // ClassPosition is 2, InClassStartingPosition is 2, so gain is 2-2=0
        Assert.AreEqual(0, car3.InClassPositionsGained);
        Assert.IsFalse(car3.IsOverallMostPositionsGained);
    }

    #endregion

    #region ValidatePositionsGained Tests

    [TestMethod]
    public void ValidatePositionsGained_OverallGainedExceedsTotalCars_ResetsToInvalid()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 1,
                OverallStartingPosition = 100,  // Gained 99 positions, but only 3 cars total
                ClassPosition = 1,
                InClassStartingPosition = 1,
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 2,
                OverallStartingPosition = 2,
                ClassPosition = 2,
                InClassStartingPosition = 2,
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "3",
                Class = "GT3",
                OverallPosition = 3,
                OverallStartingPosition = 3,
                ClassPosition = 3,
                InClassStartingPosition = 3,
                TotalTime = "00:10:02.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert
        var car1 = positions.First(p => p.Number == "1");
        Assert.AreEqual(CarPosition.InvalidPosition, car1.OverallPositionsGained, 
            "OverallPositionsGained should be invalid when it exceeds total cars");
        Assert.IsFalse(car1.IsOverallMostPositionsGained, 
            "Car should not be marked as most gained when positions gained is invalid");
    }

    [TestMethod]
    public void ValidatePositionsGained_InClassGainedExceedsCarsInClass_ResetsToInvalid()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            // GT3 class
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 1,
                OverallStartingPosition = 1,
                ClassPosition = 1,
                InClassStartingPosition = 50,  // Gained 49 positions, but only 2 GT3 cars
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 2,
                OverallStartingPosition = 2,
                ClassPosition = 2,
                InClassStartingPosition = 2,
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            },
            // LMP2 class
            new CarPosition
            {
                Number = "3",
                Class = "LMP2",
                OverallPosition = 3,
                OverallStartingPosition = 5,
                ClassPosition = 1,
                InClassStartingPosition = 2,
                TotalTime = "00:10:02.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert
        var car1 = positions.First(p => p.Number == "1");
        Assert.AreEqual(CarPosition.InvalidPosition, car1.InClassPositionsGained,
            "InClassPositionsGained should be invalid when it exceeds cars in class");
        Assert.IsFalse(car1.IsClassMostPositionsGained,
            "Car should not be marked as class most gained when positions gained is invalid");
    }

    [TestMethod]
    public void ValidatePositionsGained_NegativeGainedExceedsTotalCars_ResetsToInvalid()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 100,  // Lost 98 positions, but only 2 cars total
                OverallStartingPosition = 2,
                ClassPosition = 1,
                InClassStartingPosition = 1,
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 1,
                OverallStartingPosition = 1,
                ClassPosition = 2,
                InClassStartingPosition = 2,
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert
        var car1 = positions.First(p => p.Number == "1");
        Assert.AreEqual(CarPosition.InvalidPosition, car1.OverallPositionsGained,
            "OverallPositionsGained should be invalid when absolute value exceeds total cars (negative case)");
    }

    [TestMethod]
    public void ValidatePositionsGained_ValidPositionsGained_NotChanged()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 1,
                OverallStartingPosition = 3,  // Gained 2 positions out of 3 total - valid
                ClassPosition = 1,
                InClassStartingPosition = 2,  // Gained 1 position out of 2 in class - valid
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 2,
                OverallStartingPosition = 2,
                ClassPosition = 2,
                InClassStartingPosition = 3,
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "3",
                Class = "LMP2",
                OverallPosition = 3,
                OverallStartingPosition = 1,
                ClassPosition = 1,
                InClassStartingPosition = 1,
                TotalTime = "00:10:02.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert
        var car1 = positions.First(p => p.Number == "1");
        Assert.AreEqual(2, car1.OverallPositionsGained, "Valid OverallPositionsGained should not be changed");
        Assert.AreEqual(1, car1.InClassPositionsGained, "Valid InClassPositionsGained should not be changed");
    }

    [TestMethod]
    public void ValidatePositionsGained_EdgeCase_GainedEqualsTotalCarsMinusOne_Valid()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 1,
                OverallStartingPosition = 3,  // Gained 2 positions, total cars = 3 (valid because < total)
                ClassPosition = 1,
                InClassStartingPosition = 1,
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 2,
                OverallStartingPosition = 2,
                ClassPosition = 2,
                InClassStartingPosition = 2,
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "3",
                Class = "GT3",
                OverallPosition = 3,
                OverallStartingPosition = 1,
                ClassPosition = 3,
                InClassStartingPosition = 3,
                TotalTime = "00:10:02.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert
        var car1 = positions.First(p => p.Number == "1");
        Assert.AreEqual(2, car1.OverallPositionsGained, 
            "Gaining total-1 positions should be valid (went from last to first)");
    }

    [TestMethod]
    public void ValidatePositionsGained_AlreadyInvalidPosition_RemainsInvalid()
    {
        // Arrange
        var processor = new PositionMetadataProcessor();
        var positions = new List<CarPosition>
        {
            new CarPosition
            {
                Number = "1",
                Class = "GT3",
                OverallPosition = 0,  // Invalid - will result in InvalidPosition
                OverallStartingPosition = 5,
                ClassPosition = 1,
                InClassStartingPosition = 1,
                TotalTime = "00:10:00.000",
                LastLapCompleted = 10
            },
            new CarPosition
            {
                Number = "2",
                Class = "GT3",
                OverallPosition = 1,
                OverallStartingPosition = 1,
                ClassPosition = 2,
                InClassStartingPosition = 2,
                TotalTime = "00:10:01.000",
                LastLapCompleted = 10
            }
        };

        // Act
        processor.UpdateCarPositions(positions);

        // Assert
        var car1 = positions.First(p => p.Number == "1");
        Assert.AreEqual(CarPosition.InvalidPosition, car1.OverallPositionsGained,
            "Position that was already invalid should remain invalid");
    }

    #endregion
}
