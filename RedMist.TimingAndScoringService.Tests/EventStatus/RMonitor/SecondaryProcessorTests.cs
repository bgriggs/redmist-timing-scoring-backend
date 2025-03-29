using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.RMonitor;

[TestClass]
public class SecondaryProcessorTests
{
    #region Gap and Diff

    [TestMethod]
    public void UpdateCarPositions_SingleClass_Test()
    {
        var secondaryProcessor = new SecondaryProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLap = 10, LastTime = "00:01:00.000", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:10:01.000", LastLap = 10, LastTime = "00:01:01.000", OverallPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "A", TotalTime = "00:10:02.000", LastLap = 10, LastTime = "00:01:02.000", OverallPosition = 3 };

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
        var secondaryProcessor = new SecondaryProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLap = 10, LastTime = "00:01:00.000", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:10:01.000", LastLap = 9, LastTime = "00:01:01.000", OverallPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "A", TotalTime = "00:10:02.000", LastLap = 5, LastTime = "00:01:02.000", OverallPosition = 3 };

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
        var secondaryProcessor = new SecondaryProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLap = 10, LastTime = "00:01:00.000", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:10:01.000", LastLap = 10, LastTime = "00:01:01.000", OverallPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "B", TotalTime = "00:10:02.000", LastLap = 10, LastTime = "00:01:02.000", OverallPosition = 3 };
        var car4 = new CarPosition { Number = "4", Class = "A", TotalTime = "00:10:03.000", LastLap = 10, LastTime = "00:01:03.000", OverallPosition = 4 };
        var car5 = new CarPosition { Number = "5", Class = "B", TotalTime = "00:10:04.000", LastLap = 10, LastTime = "00:01:04.000", OverallPosition = 5 };

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
        var secondaryProcessor = new SecondaryProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLap = 10, LastTime = "00:01:00.000", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:11:01.000", LastLap = 10, LastTime = "00:01:01.000", OverallPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "A", TotalTime = "00:12:02.000", LastLap = 10, LastTime = "00:01:02.000", OverallPosition = 3 };

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

    #endregion

    #region Class Positions

    [TestMethod]
    public void UpdateClassPositions_MultiClass_Test()
    {
        var secondaryProcessor = new SecondaryProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLap = 10, LastTime = "00:01:00.000", OverallPosition = 1 };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:10:01.000", LastLap = 10, LastTime = "00:01:01.000", OverallPosition = 2 };
        var car3 = new CarPosition { Number = "3", Class = "B", TotalTime = "00:10:02.000", LastLap = 10, LastTime = "00:01:02.000", OverallPosition = 3 };
        var car4 = new CarPosition { Number = "4", Class = "A", TotalTime = "00:10:03.000", LastLap = 10, LastTime = "00:01:03.000", OverallPosition = 4 };
        var car5 = new CarPosition { Number = "5", Class = "B", TotalTime = "00:10:04.000", LastLap = 10, LastTime = "00:01:04.000", OverallPosition = 5 };

        secondaryProcessor.UpdateCarPositions([car1, car2, car3, car4, car5]);

        Assert.AreEqual(1, car1.ClassPosition);
        Assert.AreEqual(2, car2.ClassPosition);
        Assert.AreEqual(1, car3.ClassPosition);
        Assert.AreEqual(3, car4.ClassPosition);
        Assert.AreEqual(2, car5.ClassPosition);
    }

    #endregion

    #region Best Time

    [TestMethod]
    public void UpdateBestTime_MultiClass_Test()
    {
        var secondaryProcessor = new SecondaryProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLap = 10, LastTime = "00:01:00.000", BestTime = "00:01:00.000" };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:10:01.000", LastLap = 10, LastTime = "00:01:01.000", BestTime = "00:02:00.000" };
        var car3 = new CarPosition { Number = "3", Class = "B", TotalTime = "00:10:02.000", LastLap = 10, LastTime = "00:01:02.000", BestTime = "00:03:00.000" };
        var car4 = new CarPosition { Number = "4", Class = "A", TotalTime = "00:10:03.000", LastLap = 10, LastTime = "00:01:03.000", BestTime = "00:04:00.000" };
        var car5 = new CarPosition { Number = "5", Class = "B", TotalTime = "00:10:04.000", LastLap = 10, LastTime = "00:01:04.000", BestTime = "00:05:00.000" };

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
        secondaryProcessor.UpdateCarPositions([car5]);

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
        var secondaryProcessor = new SecondaryProcessor();

        var car1 = new CarPosition { Number = "1", Class = "A", TotalTime = "00:10:00.000", LastLap = 10, LastTime = "00:01:00.000", BestTime = "00:00:00.000" };
        var car2 = new CarPosition { Number = "2", Class = "A", TotalTime = "00:10:01.000", LastLap = 10, LastTime = "00:01:01.000", BestTime = "00:02:00.000" };
        var car3 = new CarPosition { Number = "3", Class = "A", TotalTime = "00:10:02.000", LastLap = 10, LastTime = "00:01:02.000", BestTime = "00:03:00.000" };

        secondaryProcessor.UpdateCarPositions([car1, car2, car3]);

        Assert.IsFalse(car1.IsBestTime);
        Assert.IsFalse(car1.IsBestTimeClass);
        Assert.IsTrue(car2.IsBestTime);
        Assert.IsTrue(car2.IsBestTimeClass);
        Assert.IsFalse(car3.IsBestTime);
        Assert.IsFalse(car3.IsBestTimeClass);
    }

    #endregion

    #region Position Changes

    [TestMethod]
    public void UpdatePositionsLostGained_MultiClass_Test()
    {
        var secondaryProcessor = new SecondaryProcessor();

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
        var secondaryProcessor = new SecondaryProcessor();

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
        var secondaryProcessor = new SecondaryProcessor();

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

    #endregion
}
