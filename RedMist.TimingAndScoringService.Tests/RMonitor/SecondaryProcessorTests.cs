using RedMist.TimingAndScoringService.EventStatus.RMonitor;
using RedMist.TimingCommon.Models;

namespace RedMist.TimingAndScoringService.Tests.RMonitor;

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
}
