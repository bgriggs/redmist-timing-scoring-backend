using RedMist.EventProcessor.EventStatus.PositionEnricher;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.PositionEnricher;

/// <summary>
/// Tests for the gap and difference values used when the UI sorts cars by their best lap time,
/// such as for qualifying.
/// </summary>
[TestClass]
public class PositionMetadataProcessorFastTimeTests
{
    private readonly PositionMetadataProcessor processor = new();

    private static CarPosition CreateCar(string number, string carClass, int overallPosition, string? bestTime,
        string totalTime = "00:10:00.000", int lastLapCompleted = 5, int bestLap = 0)
    {
        return new CarPosition
        {
            Number = number,
            Class = carClass,
            OverallPosition = overallPosition,
            OverallStartingPosition = overallPosition,
            BestTime = bestTime,
            BestLap = bestLap,
            TotalTime = totalTime,
            LastLapCompleted = lastLapCompleted,
        };
    }

    #region Overall

    [TestMethod]
    public void FastTime_Overall_FastestCarHasNoGapOrDiff()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000"),
            CreateCar("2", "GT3", 2, "00:01:31.500"),
        };

        processor.UpdateCarPositions(positions);

        var car1 = positions.First(p => p.Number == "1");
        Assert.AreEqual("", car1.OverallGapByFastTime);
        Assert.AreEqual("", car1.OverallDifferenceByFastTime);
    }

    [TestMethod]
    public void FastTime_Overall_GapIsToCarAheadAndDiffIsToFastest()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000"),
            CreateCar("2", "GT3", 2, "00:01:30.500"),
            CreateCar("3", "GT3", 3, "00:01:32.250"),
        };

        processor.UpdateCarPositions(positions);

        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("0.500", car2.OverallGapByFastTime);
        Assert.AreEqual("0.500", car2.OverallDifferenceByFastTime);

        var car3 = positions.First(p => p.Number == "3");
        Assert.AreEqual("1.750", car3.OverallGapByFastTime, "Gap is to the car ahead by best time, not the fastest car");
        Assert.AreEqual("2.250", car3.OverallDifferenceByFastTime);
    }

    /// <summary>
    /// The ordering comes from the best lap time and must ignore the car's position on track.
    /// </summary>
    [TestMethod]
    public void FastTime_Overall_OrderedByBestTimeNotOverallPosition()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:33.000"), // Leading on track, slowest lap
            CreateCar("2", "GT3", 2, "00:01:31.000"),
            CreateCar("3", "GT3", 3, "00:01:30.000"), // Last on track, fastest lap
        };

        processor.UpdateCarPositions(positions);

        var car3 = positions.First(p => p.Number == "3");
        Assert.AreEqual("", car3.OverallGapByFastTime, "Fastest lap car is the leader by best time");
        Assert.AreEqual("", car3.OverallDifferenceByFastTime);

        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("1.000", car2.OverallGapByFastTime);
        Assert.AreEqual("1.000", car2.OverallDifferenceByFastTime);

        var car1 = positions.First(p => p.Number == "1");
        Assert.AreEqual("2.000", car1.OverallGapByFastTime);
        Assert.AreEqual("3.000", car1.OverallDifferenceByFastTime);
    }

    [TestMethod]
    public void FastTime_Overall_TrackGapAndDiffAreUnaffected()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:33.000", totalTime: "00:10:00.000"),
            CreateCar("2", "GT3", 2, "00:01:30.000", totalTime: "00:10:01.000"),
        };

        processor.UpdateCarPositions(positions);

        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("1.000", car2.OverallGap, "Position based gap still comes from total time");
        Assert.AreEqual("1.000", car2.OverallDifference);
        Assert.AreEqual("", car2.OverallGapByFastTime, "Car 2 has the fastest lap so leads by best time");
        Assert.AreEqual("", car2.OverallDifferenceByFastTime);
    }

    [TestMethod]
    public void FastTime_Overall_GapOverAMinuteUsesMinuteFormat()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000"),
            CreateCar("2", "GT3", 2, "00:03:00.500"),
        };

        processor.UpdateCarPositions(positions);

        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("1:30.500", car2.OverallGapByFastTime);
        Assert.AreEqual("1:30.500", car2.OverallDifferenceByFastTime);
    }

    /// <summary>
    /// A best time is a time of day from the timing system so a slow lap recorded during a long
    /// stoppage can put an hour or more between two cars. The hours must not be dropped.
    /// </summary>
    [TestMethod]
    public void FastTime_Overall_GapOverAnHourKeepsTheHours()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000"),
            CreateCar("2", "GT3", 2, "01:01:35.250"),
            CreateCar("3", "GT3", 3, "01:31:30.000"),
        };

        processor.UpdateCarPositions(positions);

        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("1:00:05.250", car2.OverallGapByFastTime);
        Assert.AreEqual("1:00:05.250", car2.OverallDifferenceByFastTime);

        var car3 = positions.First(p => p.Number == "3");
        Assert.AreEqual("29:54.750", car3.OverallGapByFastTime);
        Assert.AreEqual("1:30:00.000", car3.OverallDifferenceByFastTime);
    }

    [TestMethod]
    public void FastTime_Overall_IdenticalBestTimesHaveZeroGap()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000"),
            CreateCar("2", "GT3", 2, "00:01:30.000"),
        };

        processor.UpdateCarPositions(positions);

        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("0.000", car2.OverallGapByFastTime);
        Assert.AreEqual("0.000", car2.OverallDifferenceByFastTime);
    }

    /// <summary>
    /// Two cars can post the same best time. The car that set it on the earlier lap is ranked first,
    /// which does not change as the cars move around the track.
    /// </summary>
    [TestMethod]
    public void FastTime_MatchingBestTimes_AreOrderedByTheLapTheTimeWasSetOn()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000", bestLap: 9),
            CreateCar("2", "GT3", 2, "00:01:30.000", bestLap: 4),
            CreateCar("3", "GT3", 3, "00:01:30.000", bestLap: 12),
        };

        processor.UpdateCarPositions(positions);

        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("", car2.OverallGapByFastTime, "Car 2 set the time first so it leads the tie");
        Assert.AreEqual("", car2.OverallDifferenceByFastTime);
        Assert.AreEqual("", car2.InClassGapByFastTime);
        Assert.AreEqual("", car2.InClassDifferenceByFastTime);

        foreach (var number in new[] { "1", "3" })
        {
            var car = positions.First(p => p.Number == number);
            Assert.AreEqual("0.000", car.OverallGapByFastTime, $"Car {number}");
            Assert.AreEqual("0.000", car.OverallDifferenceByFastTime, $"Car {number}");
        }
    }

    /// <summary>
    /// A tie is not allowed to change the order as the cars change position on track, otherwise the
    /// values flip back and forth and are sent to clients on every update.
    /// </summary>
    [TestMethod]
    public void FastTime_MatchingBestTimes_OrderDoesNotFollowPositionOnTrack()
    {
        var car1 = CreateCar("1", "GT3", 1, "00:01:30.000", bestLap: 4);
        var car2 = CreateCar("2", "GT3", 2, "00:01:30.000", bestLap: 9);
        var positions = new List<CarPosition> { car1, car2 };

        processor.UpdateCarPositions(positions);

        Assert.AreEqual("", car1.OverallGapByFastTime);
        Assert.AreEqual("0.000", car2.OverallGapByFastTime);

        // Car 2 passes car 1 on track
        car1.OverallPosition = 2;
        car2.OverallPosition = 1;
        processor.UpdateCarPositions(positions);

        Assert.AreEqual("", car1.OverallGapByFastTime, "Car 1 still set the time on the earlier lap");
        Assert.AreEqual("0.000", car2.OverallGapByFastTime);
    }

    /// <summary>
    /// Cars are ranked by number when there is nothing else to separate them so that the order is
    /// repeatable rather than depending on the order the cars arrived in.
    /// </summary>
    [TestMethod]
    public void FastTime_MatchingBestTimes_WithoutABestLap_AreOrderedByCarNumber()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("22", "GT3", 0, "00:01:30.000"),
            CreateCar("11", "GT3", 0, "00:01:30.000"),
        };

        processor.UpdateCarPositions(positions);

        var car11 = positions.First(p => p.Number == "11");
        Assert.AreEqual("", car11.OverallGapByFastTime);
        Assert.AreEqual("", car11.OverallDifferenceByFastTime);

        var car22 = positions.First(p => p.Number == "22");
        Assert.AreEqual("0.000", car22.OverallGapByFastTime);
        Assert.AreEqual("0.000", car22.OverallDifferenceByFastTime);
    }

    /// <summary>
    /// Cars whose best lap is not known are ranked after cars that reported one.
    /// </summary>
    [TestMethod]
    public void FastTime_MatchingBestTimes_CarsWithoutABestLapAreRankedLast()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000"), // No best lap reported
            CreateCar("2", "GT3", 2, "00:01:30.000", bestLap: 7),
        };

        processor.UpdateCarPositions(positions);

        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("", car2.OverallGapByFastTime);
        Assert.AreEqual("", car2.OverallDifferenceByFastTime);

        var car1 = positions.First(p => p.Number == "1");
        Assert.AreEqual("0.000", car1.OverallGapByFastTime);
        Assert.AreEqual("0.000", car1.OverallDifferenceByFastTime);
    }

    [TestMethod]
    public void FastTime_Overall_BestTimeWithoutMillisecondsIsSupported()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30"),
            CreateCar("2", "GT3", 2, "00:01:32"),
        };

        processor.UpdateCarPositions(positions);

        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("2.000", car2.OverallGapByFastTime);
        Assert.AreEqual("2.000", car2.OverallDifferenceByFastTime);
    }

    #endregion

    #region No lap time

    [TestMethod]
    public void FastTime_CarsWithoutABestTimeAreNotRanked()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000"),
            CreateCar("2", "GT3", 2, null, lastLapCompleted: 0),
            CreateCar("3", "GT3", 3, "", lastLapCompleted: 0),
            CreateCar("4", "GT3", 4, "00:00:00.000", lastLapCompleted: 0), // No lap time reported yet
            CreateCar("5", "GT3", 5, "00:01:32.000"),
        };

        processor.UpdateCarPositions(positions);

        foreach (var number in new[] { "2", "3", "4" })
        {
            var car = positions.First(p => p.Number == number);
            Assert.AreEqual("", car.OverallGapByFastTime, $"Car {number} has no lap time");
            Assert.AreEqual("", car.OverallDifferenceByFastTime, $"Car {number} has no lap time");
            Assert.AreEqual("", car.InClassGapByFastTime, $"Car {number} has no lap time");
            Assert.AreEqual("", car.InClassDifferenceByFastTime, $"Car {number} has no lap time");
        }

        // Cars without a lap time do not break the ranking of the cars that have one
        var car5 = positions.First(p => p.Number == "5");
        Assert.AreEqual("2.000", car5.OverallGapByFastTime);
        Assert.AreEqual("2.000", car5.OverallDifferenceByFastTime);
    }

    [TestMethod]
    public void FastTime_NoCarsHaveALapTime_AllEmpty()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, null, lastLapCompleted: 0),
            CreateCar("2", "GT3", 2, null, lastLapCompleted: 0),
        };

        processor.UpdateCarPositions(positions);

        foreach (var car in positions)
        {
            Assert.AreEqual("", car.OverallGapByFastTime);
            Assert.AreEqual("", car.OverallDifferenceByFastTime);
            Assert.AreEqual("", car.InClassGapByFastTime);
            Assert.AreEqual("", car.InClassDifferenceByFastTime);
        }
    }

    #endregion

    #region In class

    [TestMethod]
    public void FastTime_InClass_IsCalculatedWithinEachClass()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000"),
            CreateCar("2", "GT4", 2, "00:01:31.000"),
            CreateCar("3", "GT3", 3, "00:01:32.500"),
            CreateCar("4", "GT4", 4, "00:01:33.000"),
        };

        processor.UpdateCarPositions(positions);

        // GT3
        var car1 = positions.First(p => p.Number == "1");
        Assert.AreEqual("", car1.InClassGapByFastTime, "Fastest in GT3");
        Assert.AreEqual("", car1.InClassDifferenceByFastTime);

        var car3 = positions.First(p => p.Number == "3");
        Assert.AreEqual("2.500", car3.InClassGapByFastTime, "Gap is to the GT3 car ahead, skipping the GT4 cars");
        Assert.AreEqual("2.500", car3.InClassDifferenceByFastTime);

        // GT4
        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("", car2.InClassGapByFastTime, "Fastest in GT4");
        Assert.AreEqual("", car2.InClassDifferenceByFastTime);

        var car4 = positions.First(p => p.Number == "4");
        Assert.AreEqual("2.000", car4.InClassGapByFastTime);
        Assert.AreEqual("2.000", car4.InClassDifferenceByFastTime);

        // Overall values span both classes
        Assert.AreEqual("", car1.OverallGapByFastTime);
        Assert.AreEqual("1.000", car2.OverallGapByFastTime);
        Assert.AreEqual("1.500", car3.OverallGapByFastTime);
        Assert.AreEqual("0.500", car4.OverallGapByFastTime);
        Assert.AreEqual("3.000", car4.OverallDifferenceByFastTime);
    }

    [TestMethod]
    public void FastTime_InClass_OrderedByBestTimeNotClassPosition()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:34.000"), // Class leader on track, slowest lap in class
            CreateCar("2", "GT3", 2, "00:01:31.000"),
            CreateCar("3", "GT3", 3, "00:01:30.000"),
        };

        processor.UpdateCarPositions(positions);

        var car3 = positions.First(p => p.Number == "3");
        Assert.AreEqual("", car3.InClassGapByFastTime);
        Assert.AreEqual("", car3.InClassDifferenceByFastTime);
        Assert.AreEqual(3, car3.ClassPosition, "Class position is still based on the position on track");

        var car1 = positions.First(p => p.Number == "1");
        Assert.AreEqual("3.000", car1.InClassGapByFastTime);
        Assert.AreEqual("4.000", car1.InClassDifferenceByFastTime);
        Assert.AreEqual(1, car1.ClassPosition);
    }

    [TestMethod]
    public void FastTime_InClass_SingleCarClassHasNoGapOrDiff()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000"),
            CreateCar("2", "GT4", 2, "00:01:31.000"),
        };

        processor.UpdateCarPositions(positions);

        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("", car2.InClassGapByFastTime, "Only car in GT4");
        Assert.AreEqual("", car2.InClassDifferenceByFastTime);
        Assert.AreEqual("1.000", car2.OverallGapByFastTime);
    }

    #endregion

    #region Recalculation

    /// <summary>
    /// Best times improve during a session. The gap and difference need to follow the new order.
    /// </summary>
    [TestMethod]
    public void FastTime_RecalculatesWhenBestTimeImproves()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000"),
            CreateCar("2", "GT3", 2, "00:01:31.000"),
        };

        processor.UpdateCarPositions(positions);

        var car1 = positions.First(p => p.Number == "1");
        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("1.000", car2.OverallGapByFastTime);

        // Car 2 sets a new fastest lap
        car2.BestTime = "00:01:28.500";
        processor.UpdateCarPositions(positions);

        Assert.AreEqual("", car2.OverallGapByFastTime);
        Assert.AreEqual("", car2.OverallDifferenceByFastTime);
        Assert.AreEqual("1.500", car1.OverallGapByFastTime);
        Assert.AreEqual("1.500", car1.OverallDifferenceByFastTime);
    }

    /// <summary>
    /// Best times are cleared on a reset. The gap and difference have to be cleared with them so that
    /// clients do not keep showing a stale value.
    /// </summary>
    [TestMethod]
    public void FastTime_ClearsValuesWhenBestTimeIsRemoved()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000"),
            CreateCar("2", "GT3", 2, "00:01:31.000"),
        };

        processor.UpdateCarPositions(positions);

        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("1.000", car2.OverallGapByFastTime);

        foreach (var car in positions)
        {
            car.BestTime = null;
        }
        processor.UpdateCarPositions(positions);

        Assert.AreEqual("", car2.OverallGapByFastTime);
        Assert.AreEqual("", car2.OverallDifferenceByFastTime);
        Assert.AreEqual("", car2.InClassGapByFastTime);
        Assert.AreEqual("", car2.InClassDifferenceByFastTime);
    }

    /// <summary>
    /// Cars can be in the state before the timing system has reported a number for them.
    /// </summary>
    [TestMethod]
    public void FastTime_CarWithoutANumber_DoesNotThrow()
    {
        var positions = new List<CarPosition>
        {
            CreateCar("1", "GT3", 1, "00:01:30.000"),
            CreateCar("2", "GT3", 2, "00:01:31.000"),
        };
        positions.Add(new CarPosition { Number = null, Class = "GT3", OverallPosition = 3, BestTime = "00:01:32.000" });

        processor.UpdateCarPositions(positions);

        var car2 = positions.First(p => p.Number == "2");
        Assert.AreEqual("1.000", car2.OverallGapByFastTime);
        Assert.AreEqual("1.000", positions.Last().OverallGapByFastTime, "The unnamed car is ranked on its best time");
        Assert.AreEqual("2.000", positions.Last().OverallDifferenceByFastTime);
    }

    #endregion
}
