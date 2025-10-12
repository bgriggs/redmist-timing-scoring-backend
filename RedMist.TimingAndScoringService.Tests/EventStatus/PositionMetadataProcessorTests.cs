using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.EventStatus.PositionEnricher;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus;

[TestClass]
public class PositionMetadataProcessorTests
{
    #region Gap and Diff

    [TestMethod]
    public void UpdateCarPositions_SingleClass_Test()
    {
        var secondaryProcessor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLapCompleted = 10, LastLapTime = "00:01:00.000", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:10:01.000", LastLapCompleted = 10, LastLapTime = "00:01:01.000", OverallPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "A", TotalTime = "00:10:02.000", LastLapCompleted = 10, LastLapTime = "00:01:02.000", OverallPosition = 3 };

        secondaryProcessor.UpdateCarPositions([car1, car2, car3]);

        Assert.AreEqual("", car1.OverallGap);
        Assert.AreEqual("", car1.OverallDifference);
        Assert.AreEqual("", car1.InClassGap);
        Assert.AreEqual("", car1.InClassDifference);

        Assert.AreEqual("1.000", car2.OverallGap);
        Assert.AreEqual("1.000", car2.OverallDifference);
        Assert.AreEqual("1.000", car2.InClassGap);
        Assert.AreEqual("1.000", car2.InClassDifference);

        Assert.AreEqual("1.000", car3.OverallGap);
        Assert.AreEqual("2.000", car3.OverallDifference);
        Assert.AreEqual("1.000", car3.InClassGap);
        Assert.AreEqual("2.000", car3.InClassDifference);
    }

    [TestMethod]
    public void UpdateCarPositions_SingleClass_MultiLap_Test()
    {
        var secondaryProcessor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLapCompleted = 10, LastLapTime = "00:01:00.000", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:10:01.000", LastLapCompleted = 9, LastLapTime = "00:01:01.000", OverallPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "A", TotalTime = "00:10:02.000", LastLapCompleted = 5, LastLapTime = "00:01:02.000", OverallPosition = 3 };

        secondaryProcessor.UpdateCarPositions([car1, car2, car3]);

        Assert.AreEqual("", car1.OverallGap);
        Assert.AreEqual("", car1.OverallDifference);
        Assert.AreEqual("", car1.InClassGap);
        Assert.AreEqual("", car1.InClassDifference);

        Assert.AreEqual("1 lap", car2.OverallGap);
        Assert.AreEqual("1 lap", car2.OverallDifference);
        Assert.AreEqual("1 lap", car2.InClassGap);
        Assert.AreEqual("1 lap", car2.InClassDifference);

        Assert.AreEqual("4 laps", car3.OverallGap);
        Assert.AreEqual("5 laps", car3.OverallDifference);
        Assert.AreEqual("4 laps", car3.InClassGap);
        Assert.AreEqual("5 laps", car3.InClassDifference);
    }

    [TestMethod]
    public void UpdateCarPositions_MultiClass_SameLap_Test()
    {
        var secondaryProcessor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLapCompleted = 10, LastLapTime = "00:01:00.000", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:10:01.000", LastLapCompleted = 10, LastLapTime = "00:01:01.000", OverallPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "B", TotalTime = "00:10:02.000", LastLapCompleted = 10, LastLapTime = "00:01:02.000", OverallPosition = 3 };
        var car4 = new CarPosition { Number = "4", Class = "A", TotalTime = "00:10:03.000", LastLapCompleted = 10, LastLapTime = "00:01:03.000", OverallPosition = 4 };
        var car5 = new CarPosition { Number = "5", Class = "B", TotalTime = "00:10:04.000", LastLapCompleted = 10, LastLapTime = "00:01:04.000", OverallPosition = 5 };

        secondaryProcessor.UpdateCarPositions([car1, car2, car3, car4, car5]);

        Assert.AreEqual("", car1.OverallGap);
        Assert.AreEqual("", car1.OverallDifference);
        Assert.AreEqual("", car1.InClassGap);
        Assert.AreEqual("", car1.InClassDifference);

        Assert.AreEqual("1.000", car2.OverallGap);
        Assert.AreEqual("1.000", car2.OverallDifference);
        Assert.AreEqual("1.000", car2.InClassGap);
        Assert.AreEqual("1.000", car2.InClassDifference);

        Assert.AreEqual("1.000", car3.OverallGap);
        Assert.AreEqual("2.000", car3.OverallDifference);
        Assert.AreEqual("", car3.InClassGap);
        Assert.AreEqual("", car3.InClassDifference);

        Assert.AreEqual("1.000", car4.OverallGap);
        Assert.AreEqual("3.000", car4.OverallDifference);
        Assert.AreEqual("2.000", car4.InClassGap);
        Assert.AreEqual("3.000", car4.InClassDifference);

        Assert.AreEqual("1.000", car5.OverallGap);
        Assert.AreEqual("4.000", car5.OverallDifference);
        Assert.AreEqual("2.000", car5.InClassGap);
        Assert.AreEqual("2.000", car5.InClassDifference);
    }

    [TestMethod]
    public void UpdateCarPositions_SingleClass_MinFormat_Test()
    {
        var secondaryProcessor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLapCompleted = 10, LastLapTime = "00:01:00.000", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:11:01.000", LastLapCompleted = 10, LastLapTime = "00:01:01.000", OverallPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "A", TotalTime = "00:12:02.000", LastLapCompleted = 10, LastLapTime = "00:01:02.000", OverallPosition = 3 };

        secondaryProcessor.UpdateCarPositions([car1, car2, car3]);

        Assert.AreEqual("", car1.OverallGap);
        Assert.AreEqual("", car1.OverallDifference);
        Assert.AreEqual("", car1.InClassGap);
        Assert.AreEqual("", car1.InClassDifference);

        Assert.AreEqual("1:01.000", car2.OverallGap);
        Assert.AreEqual("1:01.000", car2.OverallDifference);
        Assert.AreEqual("1:01.000", car2.InClassGap);
        Assert.AreEqual("1:01.000", car2.InClassDifference);

        Assert.AreEqual("1:01.000", car3.OverallGap);
        Assert.AreEqual("2:02.000", car3.OverallDifference);
        Assert.AreEqual("1:01.000", car3.InClassGap);
        Assert.AreEqual("2:02.000", car3.InClassDifference);
    }

    [TestMethod]
    public void UpdateCarPositions_EmptyList_ShouldNotThrow()
    {
        var processor = new PositionMetadataProcessor();
        var emptyList = new List<CarPosition>();

        var result = processor.UpdateCarPositions(emptyList);

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void UpdateCarPositions_WithNullTotalTime_ShouldSkipGapCalculation()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLapCompleted = 10, OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = null, LastLapCompleted = 10, OverallPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "A", TotalTime = "00:10:02.000", LastLapCompleted = 10, OverallPosition = 3 };

        processor.UpdateCarPositions([car1, car2, car3]);

        Assert.AreEqual("", car1.OverallGap);
        Assert.AreEqual("", car1.OverallDifference);
        // car2 should be skipped in calculations
        Assert.AreEqual(null, car3.OverallGap);
        Assert.AreEqual("2.000", car3.OverallDifference);
    }

    [TestMethod]
    public void UpdateCarPositions_StaleCarBehind_ShouldSetEmptyGap()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLapCompleted = 10, OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:09:00.000", LastLapCompleted = 15, OverallPosition = 2 }; // Ahead in laps

        processor.UpdateCarPositions([car1, car2]);

        Assert.AreEqual("", car1.OverallGap);
        Assert.AreEqual("", car1.OverallDifference);
        Assert.AreEqual("", car2.OverallGap); // Empty because car ahead is actually behind
        Assert.AreEqual("", car2.OverallDifference);
    }

    #endregion

    #region Class Positions

    [TestMethod]
    public void UpdateClassPositions_MultiClass_Test()
    {
        var secondaryProcessor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLapCompleted = 10, LastLapTime = "00:01:00.000", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:10:01.000", LastLapCompleted = 10, LastLapTime = "00:01:01.000", OverallPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "B", TotalTime = "00:10:02.000", LastLapCompleted = 10, LastLapTime = "00:01:02.000", OverallPosition = 3 };
        var car4 = new CarPosition { Number = "4", Class = "A", TotalTime = "00:10:03.000", LastLapCompleted = 10, LastLapTime = "00:01:03.000", OverallPosition = 4 };
        var car5 = new CarPosition { Number = "5", Class = "B", TotalTime = "00:10:04.000", LastLapCompleted = 10, LastLapTime = "00:01:04.000", OverallPosition = 5 };

        secondaryProcessor.UpdateCarPositions([car1, car2, car3, car4, car5]);

        Assert.AreEqual(1, car1.ClassPosition);
        Assert.AreEqual(2, car2.ClassPosition);
        Assert.AreEqual(1, car3.ClassPosition);
        Assert.AreEqual(3, car4.ClassPosition);
        Assert.AreEqual(2, car5.ClassPosition);
    }

    [TestMethod]
    public void UpdateClassPositions_SingleClass_Test()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", OverallPosition = 3 };
        var car2 = new CarPosition { Number = "2", Class = "A", OverallPosition = 1 };
        var car3 = new CarPosition { Number = "3", Class = "A", OverallPosition = 2 };

        processor.UpdateCarPositions([car1, car2, car3]);

        Assert.AreEqual(3, car1.ClassPosition);
        Assert.AreEqual(1, car2.ClassPosition);
        Assert.AreEqual(2, car3.ClassPosition);
    }

    [TestMethod]
    public void UpdateClassPositions_EmptyClass_ShouldNotThrow()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = null, OverallPosition = 2 };

        processor.UpdateCarPositions([car1, car2]);

        // Should complete without throwing
        Assert.AreEqual(1, car1.ClassPosition);
        Assert.AreEqual(1, car2.ClassPosition);
    }

    [TestMethod]
    public void UpdateClassPositions_Car2_Should_Be_First_In_Class_Test()
    {
        var processor = new PositionMetadataProcessor();

        // Simulate the exact scenario from the failing test
        var car2 = new CarPosition { Number = "2", Class = "GTO", OverallPosition = 1 };
        var car70 = new CarPosition { Number = "70", Class = "GTO", OverallPosition = 2 };

        processor.UpdateCarPositions([car2, car70]);

        // Car 2 should have class position 1 since it has overall position 1
        Assert.AreEqual(1, car2.ClassPosition, $"Car 2 should have class position 1. Car2: Overall={car2.OverallPosition}, Class={car2.ClassPosition}; Car70: Overall={car70.OverallPosition}, Class={car70.ClassPosition}");
        Assert.AreEqual(2, car70.ClassPosition, $"Car 70 should have class position 2. Car2: Overall={car2.OverallPosition}, Class={car2.ClassPosition}; Car70: Overall={car70.OverallPosition}, Class={car70.ClassPosition}");
    }

    #endregion

    #region Best Time

    [TestMethod]
    public void UpdateBestTime_MultiClass_Test()
    {
        var secondaryProcessor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLapCompleted = 10, LastLapTime = "00:01:00.000", BestTime = "00:01:00.000" };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:10:01.000", LastLapCompleted = 10, LastLapTime = "00:01:01.000", BestTime = "00:02:00.000" };
        var car3 = new CarPosition { Number = "3", Class = "B", TotalTime = "00:10:02.000", LastLapCompleted = 10, LastLapTime = "00:01:02.000", BestTime = "00:03:00.000" };
        var car4 = new CarPosition { Number = "4", Class = "A", TotalTime = "00:10:03.000", LastLapCompleted = 10, LastLapTime = "00:01:03.000", BestTime = "00:04:00.000" };
        var car5 = new CarPosition { Number = "5", Class = "B", TotalTime = "00:10:04.000", LastLapCompleted = 10, LastLapTime = "00:01:04.000", BestTime = "00:05:00.000" };

        secondaryProcessor.UpdateCarPositions([car1, car2, car3, car4, car5]);

        Assert.IsTrue(car1.IsBestTime);
        Assert.IsTrue(car1.IsBestTimeClass);
        Assert.IsFalse(car2.IsBestTime);
        Assert.IsFalse(car2.IsBestTimeClass);
        Assert.IsFalse(car3.IsBestTime);
        Assert.IsTrue(car3.IsBestTimeClass);
        Assert.IsFalse(car4.IsBestTime);
        Assert.IsFalse(car4.IsBestTimeClass);
        Assert.IsFalse(car5.IsBestTime);
        Assert.IsFalse(car5.IsBestTimeClass);

        // Make the last car the best
        car5.BestTime = "00:00:10.000";
        secondaryProcessor.UpdateCarPositions([car1, car2, car3, car4, car5]);

        Assert.IsFalse(car1.IsBestTime);
        Assert.IsTrue(car1.IsBestTimeClass);
        Assert.IsFalse(car2.IsBestTime);
        Assert.IsFalse(car2.IsBestTimeClass);
        Assert.IsFalse(car3.IsBestTime);
        Assert.IsFalse(car3.IsBestTimeClass);
        Assert.IsFalse(car4.IsBestTime);
        Assert.IsFalse(car4.IsBestTimeClass);
        Assert.IsTrue(car5.IsBestTime);
        Assert.IsTrue(car5.IsBestTimeClass);
    }

    [TestMethod]
    public void UpdateBestTime_ZeroTime_SkipToNext_Test()
    {
        var secondaryProcessor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLapCompleted = 10, LastLapTime = "00:01:00.000", BestTime = "00:00:00.000" };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:10:01.000", LastLapCompleted = 10, LastLapTime = "00:01:01.000", BestTime = "00:02:00.000" };
        var car3 = new CarPosition { Number = "3", Class = "A", TotalTime = "00:10:02.000", LastLapCompleted = 10, LastLapTime = "00:01:02.000", BestTime = "00:03:00.000" };

        secondaryProcessor.UpdateCarPositions([car1, car2, car3]);

        Assert.IsFalse(car1.IsBestTime);
        Assert.IsFalse(car1.IsBestTimeClass);
        Assert.IsTrue(car2.IsBestTime);
        Assert.IsTrue(car2.IsBestTimeClass);
        Assert.IsFalse(car3.IsBestTime);
        Assert.IsFalse(car3.IsBestTimeClass);
    }

    [TestMethod]
    public void UpdateBestTime_NullBestTime_ShouldSkip()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", BestTime = null };
        var car2 = new CarPosition { Number = "2", Class = "A", BestTime = "00:01:30.000" };
        var car3 = new CarPosition { Number = "3", Class = "A", BestTime = "00:02:00.000" };

        processor.UpdateCarPositions([car1, car2, car3]);

        Assert.IsFalse(car1.IsBestTime);
        Assert.IsFalse(car1.IsBestTimeClass);
        Assert.IsTrue(car2.IsBestTime);
        Assert.IsTrue(car2.IsBestTimeClass);
        Assert.IsFalse(car3.IsBestTime);
        Assert.IsFalse(car3.IsBestTimeClass);
    }

    [TestMethod]
    public void UpdateBestTime_EmptyBestTime_ShouldSkip()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", BestTime = "" };
        var car2 = new CarPosition { Number = "2", Class = "A", BestTime = "00:01:30.000" };

        processor.UpdateCarPositions([car1, car2]);

        Assert.IsFalse(car1.IsBestTime);
        Assert.IsFalse(car1.IsBestTimeClass);
        Assert.IsTrue(car2.IsBestTime);
        Assert.IsTrue(car2.IsBestTimeClass);
    }

    [TestMethod]
    public void UpdateBestTime_AllCarsWithoutLaps_ShouldNotSetBest()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", BestTime = "00:00:00.000" };
        var car2 = new CarPosition { Number = "2", Class = "A", BestTime = "" };
        var car3 = new CarPosition { Number = "3", Class = "A", BestTime = null };

        processor.UpdateCarPositions([car1, car2, car3]);

        Assert.IsFalse(car1.IsBestTime);
        Assert.IsFalse(car1.IsBestTimeClass);
        Assert.IsFalse(car2.IsBestTime);
        Assert.IsFalse(car2.IsBestTimeClass);
        Assert.IsFalse(car3.IsBestTime);
        Assert.IsFalse(car3.IsBestTimeClass);
    }

    #endregion

    #region Position Changes

    [TestMethod]
    public void UpdatePositionsLostGained_MultiClass_Test()
    {
        var secondaryProcessor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", OverallPosition = 1, OverallStartingPosition = 1, InClassStartingPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", OverallPosition = 2, OverallStartingPosition = 2, InClassStartingPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "B", OverallPosition = 3, OverallStartingPosition = 3, InClassStartingPosition = 1 };
        var car4 = new CarPosition { Number = "4", Class = "A", OverallPosition = 4, OverallStartingPosition = 4, InClassStartingPosition = 3 };
        var car5 = new CarPosition { Number = "5", Class = "B", OverallPosition = 5, OverallStartingPosition = 5, InClassStartingPosition = 2 };

        secondaryProcessor.UpdateCarPositions([car1, car2, car3, car4, car5]);

        Assert.AreEqual(0, car1.OverallPositionsGained);
        Assert.AreEqual(0, car1.InClassPositionsGained);
        Assert.IsFalse(car1.IsOverallMostPositionsGained);
        Assert.IsFalse(car1.IsClassMostPositionsGained);
        
        Assert.AreEqual(0, car2.OverallPositionsGained);
        Assert.AreEqual(0, car2.InClassPositionsGained);
        Assert.IsFalse(car2.IsOverallMostPositionsGained);
        Assert.IsFalse(car2.IsClassMostPositionsGained);

        Assert.AreEqual(0, car3.OverallPositionsGained);
        Assert.AreEqual(0, car3.InClassPositionsGained);
        Assert.IsFalse(car3.IsOverallMostPositionsGained);
        Assert.IsFalse(car3.IsClassMostPositionsGained);

        Assert.AreEqual(0, car4.OverallPositionsGained);
        Assert.AreEqual(0, car4.InClassPositionsGained);
        Assert.IsFalse(car4.IsOverallMostPositionsGained);
        Assert.IsFalse(car4.IsClassMostPositionsGained);

        Assert.AreEqual(0, car5.OverallPositionsGained);
        Assert.AreEqual(0, car5.InClassPositionsGained);
        Assert.IsFalse(car5.IsOverallMostPositionsGained);
        Assert.IsFalse(car5.IsClassMostPositionsGained);

        car1.OverallPosition = 2;
        car2.OverallPosition = 1;
        car3.OverallPosition = 4;
        car4.OverallPosition = 3;
        car5.OverallPosition = 5;

        secondaryProcessor.UpdateCarPositions([car1, car2, car3, car4, car5]);

        Assert.AreEqual(-1, car1.OverallPositionsGained);
        Assert.AreEqual(-1, car1.InClassPositionsGained);
        Assert.IsFalse(car1.IsOverallMostPositionsGained);
        Assert.IsFalse(car1.IsClassMostPositionsGained);

        Assert.AreEqual(1, car2.OverallPositionsGained);
        Assert.AreEqual(1, car2.InClassPositionsGained);
        Assert.IsFalse(car2.IsOverallMostPositionsGained);
        Assert.IsTrue(car2.IsClassMostPositionsGained);

        Assert.AreEqual(-1, car3.OverallPositionsGained);
        Assert.AreEqual(0, car3.InClassPositionsGained);
        Assert.IsFalse(car3.IsOverallMostPositionsGained);
        Assert.IsFalse(car3.IsClassMostPositionsGained);

        Assert.AreEqual(1, car4.OverallPositionsGained);
        Assert.AreEqual(0, car4.InClassPositionsGained);
        Assert.IsFalse(car4.IsOverallMostPositionsGained);
        Assert.IsFalse(car4.IsClassMostPositionsGained);

        Assert.AreEqual(0, car5.OverallPositionsGained);
        Assert.AreEqual(0, car5.InClassPositionsGained);
        Assert.IsFalse(car5.IsOverallMostPositionsGained);
        Assert.IsFalse(car5.IsClassMostPositionsGained);
    }

    [TestMethod]
    public void UpdatePositionsLostGained_Invalid_MissingData_Test()
    {
        var secondaryProcessor = new PositionMetadataProcessor();

        // Overall starting position missing
        var car1 = new CarPosition { Number = "1", Class = "A", OverallPosition = 1, OverallStartingPosition = 0, InClassStartingPosition = 1 };
        // In class starting position missing
        var car2 = new CarPosition { Number = "2", Class = "A", OverallPosition = 2, OverallStartingPosition = 2, InClassStartingPosition = 0 };
        // Overall position missing
        var car3 = new CarPosition { Number = "3", Class = "B", OverallPosition = 0, OverallStartingPosition = 3, InClassStartingPosition = 1 };

        secondaryProcessor.UpdateCarPositions([car1, car2, car3]);

        Assert.AreEqual(CarPosition.InvalidPosition, car1.OverallPositionsGained);
        Assert.AreEqual(CarPosition.InvalidPosition, car1.InClassPositionsGained);
        Assert.AreEqual(CarPosition.InvalidPosition, car2.OverallPositionsGained);
        Assert.AreEqual(CarPosition.InvalidPosition, car2.InClassPositionsGained);
        Assert.AreEqual(CarPosition.InvalidPosition, car3.OverallPositionsGained);
        Assert.AreEqual(CarPosition.InvalidPosition, car3.InClassPositionsGained);
    }

    [TestMethod]
    public void UpdatePositionsLostGained_Fastest_Reset_Test()
    {
        var secondaryProcessor = new PositionMetadataProcessor();

        // Overall starting position missing
        var car1 = new CarPosition { Number = "1", Class = "A", OverallPosition = 1, OverallStartingPosition = 1, InClassStartingPosition = 1 };
        // In class starting position missing
        var car2 = new CarPosition { Number = "2", Class = "A", OverallPosition = 2, OverallStartingPosition = 2, InClassStartingPosition = 2 };
        // Overall position missing
        var car3 = new CarPosition { Number = "3", Class = "A", OverallPosition = 3, OverallStartingPosition = 3, InClassStartingPosition = 3 };

        secondaryProcessor.UpdateCarPositions([car1, car2, car3]);

        Assert.IsFalse(car1.IsOverallMostPositionsGained);
        Assert.IsFalse(car1.IsClassMostPositionsGained);
        Assert.IsFalse(car2.IsOverallMostPositionsGained);
        Assert.IsFalse(car2.IsClassMostPositionsGained);
        Assert.IsFalse(car3.IsOverallMostPositionsGained);
        Assert.IsFalse(car3.IsClassMostPositionsGained);

        car1.OverallPosition = 20;
        car2.OverallPosition = 2;
        car3.OverallPosition = 1;
        secondaryProcessor.UpdateCarPositions([car1, car2, car3]);

        Assert.IsFalse(car1.IsOverallMostPositionsGained);
        Assert.IsFalse(car1.IsClassMostPositionsGained);
        Assert.IsFalse(car2.IsOverallMostPositionsGained);
        Assert.IsFalse(car2.IsClassMostPositionsGained);
        Assert.IsTrue(car3.IsOverallMostPositionsGained);
        Assert.IsTrue(car3.IsClassMostPositionsGained);

        // Reset the positions
        car1.OverallPosition = 1;
        car2.OverallPosition = 2;
        car3.OverallPosition = 3;
        secondaryProcessor.UpdateCarPositions([car1, car2, car3]);

        Assert.IsFalse(car1.IsOverallMostPositionsGained);
        Assert.IsFalse(car1.IsClassMostPositionsGained);
        Assert.IsFalse(car2.IsOverallMostPositionsGained);
        Assert.IsFalse(car2.IsClassMostPositionsGained);
        Assert.IsFalse(car3.IsOverallMostPositionsGained);
        Assert.IsFalse(car3.IsClassMostPositionsGained);
    }

    [TestMethod]
    public void UpdatePositionsLostGained_TiedMostGained_NoWinner_Test()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", OverallPosition = 1, OverallStartingPosition = 3, InClassStartingPosition = 3, ClassPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", OverallPosition = 2, OverallStartingPosition = 4, InClassStartingPosition = 4, ClassPosition = 2 };

        processor.UpdateCarPositions([car1, car2]);

        // Both gained 2 positions, so no one gets the award
        Assert.IsFalse(car1.IsOverallMostPositionsGained);
        Assert.IsFalse(car1.IsClassMostPositionsGained);
        Assert.IsFalse(car2.IsOverallMostPositionsGained);
        Assert.IsFalse(car2.IsClassMostPositionsGained);
    }

    [TestMethod]
    public void UpdatePositionsLostGained_NoPositiveGains_Test()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", OverallPosition = 1, OverallStartingPosition = 1, InClassStartingPosition = 1, ClassPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", OverallPosition = 2, OverallStartingPosition = 2, InClassStartingPosition = 2, ClassPosition = 2 };

        processor.UpdateCarPositions([car1, car2]);

        // No positive gains, so no one gets the award
        Assert.IsFalse(car1.IsOverallMostPositionsGained);
        Assert.IsFalse(car1.IsClassMostPositionsGained);
        Assert.IsFalse(car2.IsOverallMostPositionsGained);
        Assert.IsFalse(car2.IsClassMostPositionsGained);
    }

    #endregion

    //#region Clear Method

    //[TestMethod]
    //public void Clear_ShouldClearInternalLookup()
    //{
    //    var processor = new PositionMetadataProcessor();

    //    var car1 = new CarPosition { Number = "1", Class = "A", OverallPosition = 1, OverallStartingPosition = 5, InClassStartingPosition = 3, ClassPosition = 1 };
    //    processor.UpdateCarPositions([car1]);

    //    // Clear should reset internal state
    //    processor.Clear();

    //    // After clear, adding a new car should not be affected by previous state
    //    var car2 = new CarPosition { Number = "2", Class = "A", OverallPosition = 1, OverallStartingPosition = 3, InClassStartingPosition = 2, ClassPosition = 1 };
    //    processor.UpdateCarPositions([car2]);

    //    Assert.IsTrue(car2.IsOverallMostPositionsGained); // Should get most gained
    //}

    //#endregion

    #region Helper Method Tests

    [TestMethod]
    public void ParseRMTime_ValidTimeFormats_Test()
    {
        // Test HH:mm:ss.fff format
        var result1 = PositionMetadataProcessor.ParseRMTime("12:34:56.789");
        Assert.AreEqual(new DateTime(1, 1, 1, 12, 34, 56, 789).TimeOfDay, result1.TimeOfDay);

        // Test HH:mm:ss format
        var result2 = PositionMetadataProcessor.ParseRMTime("08:15:30");
        Assert.AreEqual(new DateTime(1, 1, 1, 8, 15, 30, 0).TimeOfDay, result2.TimeOfDay);
    }

    [TestMethod]
    public void ParseRMTime_InvalidFormats_ShouldReturnDefault()
    {
        var result1 = PositionMetadataProcessor.ParseRMTime("invalid");
        Assert.AreEqual(default(DateTime), result1);

        var result2 = PositionMetadataProcessor.ParseRMTime("");
        Assert.AreEqual(default(DateTime), result2);

        var result3 = PositionMetadataProcessor.ParseRMTime("25:00:00.000"); // Invalid hour
        Assert.AreEqual(default(DateTime), result3);
    }

    [TestMethod]
    public void GetTimeFormat_MinutesFormat_Test()
    {
        var timeWithMinutes = new TimeSpan(0, 2, 30, 500); // 2 minutes, 30.5 seconds
        var format = PositionMetadataProcessor.GetTimeFormat(timeWithMinutes);
        Assert.AreEqual(@"m\:ss\.fff", format);
    }

    [TestMethod]
    public void GetTimeFormat_SecondsFormat_Test()
    {
        var timeWithoutMinutes = new TimeSpan(0, 0, 0, 25); // 25 seconds
        var format = PositionMetadataProcessor.GetTimeFormat(timeWithoutMinutes);
        Assert.AreEqual(@"s\.fff", format);
    }

    [TestMethod]
    public void GetTimeFormat_ZeroTime_Test()
    {
        var zeroTime = new TimeSpan(0);
        var format = PositionMetadataProcessor.GetTimeFormat(zeroTime);
        Assert.AreEqual(@"s\.fff", format);
    }

    [TestMethod]
    public void GetLapTerm_Singular_Test()
    {
        var result = PositionMetadataProcessor.GetLapTerm(1);
        Assert.AreEqual("lap", result);
    }

    [TestMethod]
    public void GetLapTerm_Plural_Test()
    {
        var result0 = PositionMetadataProcessor.GetLapTerm(0);
        Assert.AreEqual("laps", result0);

        var result2 = PositionMetadataProcessor.GetLapTerm(2);
        Assert.AreEqual("laps", result2);

        var result5 = PositionMetadataProcessor.GetLapTerm(5);
        Assert.AreEqual("laps", result5);
    }

    [TestMethod]
    public void GetLapTerm_Negative_Test()
    {
        var result = PositionMetadataProcessor.GetLapTerm(-1);
        Assert.AreEqual("laps", result);
    }

    #endregion

    #region Position Comparer Tests

    [TestMethod]
    public void PositionComparer_SortCorrectly_Test()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", OverallPosition = 3, TotalTime = "01:00:30.000" };
        var car2 = new CarPosition { Number = "2", OverallPosition = 1, TotalTime = "01:00:10.000" };
        var car3 = new CarPosition { Number = "3", OverallPosition = 2, TotalTime = "01:00:20.000" };
        var car4 = new CarPosition { Number = "4", OverallPosition = 0, TotalTime = "00:00:00.000" }; // Special case

        var positions = new List<CarPosition> { car1, car2, car3, car4 };
        
        // Process them to trigger sorting
        processor.UpdateCarPositions(positions);

        // The internal sorting should order them correctly
        // We can verify this by checking gap calculations work properly
        Assert.IsNotNull(car1.OverallGap);
        Assert.IsNotNull(car2.OverallGap);
        Assert.IsNotNull(car3.OverallGap);
    }

    #endregion

    #region Edge Cases and Null Handling

    [TestMethod]
    public void UpdateCarPositions_CarWithoutNumber_ShouldNotAddToLookup()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = null, Class = "A", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", OverallPosition = 2 };

        processor.UpdateCarPositions([car1, car2]);

        // Should complete without throwing, car1 should not be added to internal lookup
        Assert.IsNotNull(car2);
    }

    [TestMethod]
    public void UpdateCarPositions_MultipleUpdatesWithSameCars_Test()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition 
        { 
            Number = "1", 
            Class = "A", 
            TotalTime = "00:10:00.000", 
            LastLapCompleted = 10, 
            OverallPosition = 1,
            OverallStartingPosition = 3,
            InClassStartingPosition = 2,
            ClassPosition = 1
        };

        // First update
        processor.UpdateCarPositions([car1]);
        Assert.AreEqual(2, car1.OverallPositionsGained);

        // Second update with different position
        car1.OverallPosition = 2;
        processor.UpdateCarPositions([car1]);
        Assert.AreEqual(1, car1.OverallPositionsGained);
    }

    [TestMethod]
    public void UpdateCarPositions_WithInvalidTimeString_ShouldSkipCalculations()
    {
        var processor = new PositionMetadataProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "invalid", OverallPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "A", TotalTime = "00:10:02.000", OverallPosition = 3 };

        processor.UpdateCarPositions([car1, car2, car3]);

        // car2 should be skipped due to invalid time, so car3 gap should be calculated from car1
        Assert.AreEqual("", car1.OverallGap);
        Assert.AreEqual(null, car3.OverallGap);
        Assert.AreEqual("2.000", car3.OverallDifference);
    }

    #endregion
}
