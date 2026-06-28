using RedMist.ControlLogs.Announcements;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.ControlLogProcessor;

/// <summary>
/// Tests for parsing external-source race-control announcement text into control log entries.
/// </summary>
[TestClass]
public class AnnouncementControlLogParserTests
{
    private static readonly DateTime Ts = new(2026, 6, 16, 17, 20, 2, DateTimeKind.Utc);

    [TestMethod]
    [DataRow("CAR 134 BEHIND THE WALL", "134")]
    [DataRow("CAR 411 OFF COURSE, TURN 10", "411")]
    [DataRow("CAR 24 STOPPED ON COURSE, TURN 12", "24")]
    [DataRow("CAR 908 CONTINUING SLOWLY FROM TURN Pit Entry", "908")]
    [DataRow("Monitoring Car 58 For Non Functioning Brake Lights", "58")]
    [DataRow("PIT LANE PROCEDURE CAR 58 UNDER REVIEW", "58")]
    [DataRow("Car 134: Penalty - Code 60 Violation - 1 Lap Penalty", "134")]
    [DataRow("Car 24: Pit Lane Speed Violation - Under Review", "24")]
    public void Parse_SingleCar(string text, string car)
    {
        var entries = AnnouncementControlLogParser.Parse(Ts, text).ToList();
        Assert.HasCount(1, entries);
        Assert.AreEqual(car, entries[0].Car1);
        Assert.AreEqual(text, entries[0].Note);
        Assert.AreEqual(Ts, entries[0].Timestamp);
    }

    [TestMethod]
    public void Parse_TrailingNumberIsNotACar()
    {
        // The "1" in "TURN 1" must not be read as a car number.
        var entries = AnnouncementControlLogParser.Parse(Ts, "CAR 14 SPUN AT TURN 1 & CONTINUED").ToList();
        Assert.HasCount(1, entries);
        Assert.AreEqual("14", entries[0].Car1);
    }

    [TestMethod]
    [DataRow("CAR 94, 908 BEHIND THE WALL", new[] { "94", "908" })]
    [DataRow("Car 14, 392, 909: Penalty - Code 60 Violation - 1 Lap Penalty", new[] { "14", "392", "909" })]
    [DataRow("Car 41, 76, 411, 444: Penalty - Code 60 Violation - 1 Lap Penalty", new[] { "41", "76", "411", "444" })]
    [DataRow("INCIDENT INVOLVING CARS 4 & 392 REVIEWED, NO ACTION", new[] { "4", "392" })]
    [DataRow("INTERACTION BETWEEN CAR 4 AND 392 - UNDER REVIEW", new[] { "4", "392" })]
    public void Parse_MultipleCars_OneEntryEach(string text, string[] cars)
    {
        var entries = AnnouncementControlLogParser.Parse(Ts, text).ToList();
        Assert.HasCount(cars.Length, entries);
        CollectionAssert.AreEqual(cars, entries.Select(e => e.Car1).ToList());
        // Each entry carries the full original text.
        Assert.IsTrue(entries.All(e => e.Note == text));
    }

    [TestMethod]
    [DataRow("CODE 60 WILL START AT 17:20:02")]
    [DataRow("CODE 60")]
    [DataRow("GREEN FLAG")]
    [DataRow("GREEN WILL START AT 17:21:05")]
    public void Parse_NoCar_SingleEventLevelEntry(string text)
    {
        var entries = AnnouncementControlLogParser.Parse(Ts, text).ToList();
        Assert.HasCount(1, entries);
        Assert.AreEqual(string.Empty, entries[0].Car1);
        Assert.AreEqual(text, entries[0].Note);
    }

    [TestMethod]
    public void Parse_EmptyCarToken_NoCar()
    {
        var entries = AnnouncementControlLogParser.Parse(Ts, "Car : Pit To Repair- Non-Functioning Race Link").ToList();
        Assert.HasCount(1, entries);
        Assert.AreEqual(string.Empty, entries[0].Car1);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public void Parse_Blank_NoEntries(string? text)
    {
        Assert.HasCount(0, AnnouncementControlLogParser.Parse(Ts, text).ToList());
    }

    [TestMethod]
    public void Parse_LapPenalty_SetsPenaltyActionForCounting()
    {
        var log = new RedMist.ControlLogs.Announcements.AnnouncementControlLog(new AnnouncementControlLogStore());
        var entry = AnnouncementControlLogParser.Parse(Ts, "Car 134: Penalty - Code 60 Violation - 1 Lap Penalty").Single();
        Assert.AreEqual("Penalty", entry.Status);
        Assert.IsTrue(log.LapPenaltyPattern.IsMatch(entry.PenaltyAction), "Lap penalty regex should match the penalty action");
        Assert.AreEqual("1", log.LapPenaltyPattern.Match(entry.PenaltyAction).Groups[1].Value);
    }

    [TestMethod]
    public void Parse_LapPenalty_NoSpaceVariant()
    {
        var log = new RedMist.ControlLogs.Announcements.AnnouncementControlLog(new AnnouncementControlLogStore());
        var entry = AnnouncementControlLogParser.Parse(Ts, "Car 290: Penalty - Exceeding Number Of Permitted Tire Changes - 2 LapPenalty").First();
        Assert.IsTrue(log.LapPenaltyPattern.IsMatch(entry.PenaltyAction));
        Assert.AreEqual("2", log.LapPenaltyPattern.Match(entry.PenaltyAction).Groups[1].Value);
    }

    [TestMethod]
    public void Parse_Warning_SetsPenaltyActionForCounting()
    {
        var log = new RedMist.ControlLogs.Announcements.AnnouncementControlLog(new AnnouncementControlLogStore());
        var entries = AnnouncementControlLogParser.Parse(Ts, "Car 4, 85: Code 60 Violation - (+) Warning").ToList();
        Assert.HasCount(2, entries);
        Assert.IsTrue(entries.All(e => e.Status == "Warning"));
        Assert.IsTrue(entries.All(e => log.WarningPattern.IsMatch(e.PenaltyAction)));
    }

    [TestMethod]
    public void Parse_Observation_HasNoPenaltyAction()
    {
        var entry = AnnouncementControlLogParser.Parse(Ts, "CAR 134 BEHIND THE WALL").Single();
        Assert.AreEqual("Note", entry.Status);
        Assert.AreEqual(string.Empty, entry.PenaltyAction);
    }

    [TestMethod]
    [DataRow("CAR 411 OFF COURSE, TURN 10", "10")]
    [DataRow("CAR 24 STOPPED ON COURSE, TURN 12", "12")]
    [DataRow("CAR 14 SPUN AT TURN 1 & CONTINUED", "1")]
    [DataRow("INCIDENT INVOLVING CARS 444 & 14 UNDER REVIEW - Turn 10", "10")]   // mixed case
    [DataRow("CAR 5 OFF COURSE AT TURN 10A & CONTINUED", "10A")]                 // number + letter
    [DataRow("car 5 off course at turn 7b", "7B")]                              // upper-cased
    public void Parse_PopulatesCornerFromTurn(string text, string corner)
    {
        var entry = AnnouncementControlLogParser.Parse(Ts, text).First();
        Assert.AreEqual(corner, entry.Corner);
    }

    [TestMethod]
    [DataRow("CAR 134 BEHIND THE WALL")]                            // no turn
    [DataRow("CAR 908 CONTINUING SLOWLY FROM TURN Pit Entry")]      // non-numeric turn
    [DataRow("CAR 58 OFF COURSE AT TURN  & CONTINUED")]             // bare "TURN " with no number
    public void Parse_NoCorner_WhenTurnNotNumeric(string text)
    {
        var entry = AnnouncementControlLogParser.Parse(Ts, text).First();
        Assert.AreEqual(string.Empty, entry.Corner);
    }

    [TestMethod]
    public void ParseAll_NumbersByTimestampOldestFirst()
    {
        var t1 = new DateTime(2026, 6, 16, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 16, 11, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);
        // Supplied out of order; ParseAll orders oldest -> newest.
        var anns = new[]
        {
            new Announcement { Timestamp = t3, Text = "GREEN FLAG" },
            new Announcement { Timestamp = t1, Text = "CAR 14 BEHIND THE WALL" },
            new Announcement { Timestamp = t2, Text = "Car 14, 392: Penalty - Code 60 Violation - 1 Lap Penalty" },
        };

        var entries = AnnouncementControlLogParser.ParseAll(anns);

        // Oldest message is 1; a multi-car message shares one number across its per-car entries.
        Assert.AreEqual(1, entries.Single(e => e.Timestamp == t1).OrderId);
        var second = entries.Where(e => e.Timestamp == t2).ToList();
        Assert.HasCount(2, second);
        Assert.IsTrue(second.All(e => e.OrderId == 2), "both cars of a message share its number");
        Assert.AreEqual(3, entries.Single(e => e.Timestamp == t3).OrderId);
    }

    [TestMethod]
    public void ParseAll_AppendingNewerKeepsExistingNumbers()
    {
        var t1 = new DateTime(2026, 6, 16, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 16, 11, 0, 0, DateTimeKind.Utc);
        var a1 = new Announcement { Timestamp = t1, Text = "CAR 14 BEHIND THE WALL" };
        var a2 = new Announcement { Timestamp = t2, Text = "GREEN FLAG" };

        Assert.AreEqual(1, AnnouncementControlLogParser.ParseAll([a1]).Single().OrderId);

        var second = AnnouncementControlLogParser.ParseAll([a2, a1]);
        Assert.AreEqual(1, second.Single(e => e.Timestamp == t1).OrderId);
        Assert.AreEqual(2, second.Single(e => e.Timestamp == t2).OrderId);
    }
}
