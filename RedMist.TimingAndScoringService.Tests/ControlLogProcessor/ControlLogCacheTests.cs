using RedMist.ControlLogs;
using RedMist.TimingCommon.Models;
using System.Text.RegularExpressions;

namespace RedMist.EventProcessor.Tests.ControlLogProcessor;

/// <summary>
/// Unit tests for ControlLogCache functionality, including worksheet change scenarios
/// Reference Google Sheet: https://docs.google.com/spreadsheets/d/1teBnLDWmf4Gu7GTAPcXRQJUKCFXaVDCPEAg3ci1boq0/edit?pli=1&gid=1634163143#gid=1634163143
/// 
/// The main issue being tested: When a Google Sheets control log changes from one worksheet to another,
/// the column mappings cache in GoogleSheetsControlLog is not cleared, causing incorrect parsing.
/// </summary>
[TestClass]
public class ControlLogCacheTests
{
    private static readonly Regex WarningRegex = new(@".*Warning.*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LapPenaltyRegex = new(@"(\d+)\s+(Lap|Laps)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlackFlagRegex = new(@".*Drive Through Penalty.*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    #region Original Working Tests (Static Method Tests)

    [TestMethod]
    public void GetWarningsAndPenalties_Warning_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenaltyAction = "waRniNg" };
        logs["1"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(1, results["1"].Warnings);
        Assert.AreEqual(0, results["1"].Laps);
        Assert.AreEqual(0, results["1"].BlackFlags);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Warning_Invalid_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenaltyAction = "warns" };
        logs["1"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].Warnings);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Lap_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenaltyAction = "1 lap" };
        logs["1"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].Warnings);
        Assert.AreEqual(1, results["1"].Laps);
        Assert.AreEqual(0, results["1"].BlackFlags);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Lap_Invalid_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenaltyAction = "1 loop" };
        logs["1"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].Warnings);
        Assert.AreEqual(0, results["1"].Laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Laps_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenaltyAction = "10 laps" };
        logs["1"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].Warnings);
        Assert.AreEqual(10, results["1"].Laps);
        Assert.AreEqual(0, results["1"].BlackFlags);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Laps_Multicar_NoCarSelected_DefaultsToCar1_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenaltyAction = "10 laps" };
        logs["1"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results  );
        Assert.AreEqual(0, results["1"].Warnings);
        // When no car is highlighted, penalty defaults to Car1
        Assert.AreEqual(10, results["1"].Laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Laps_Multicar_FirstCarSelected_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenaltyAction = "10 laps", IsCar1Highlighted = true };
        logs["1"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].Warnings);
        Assert.AreEqual(10, results["1"].Laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Laps_Multicar_SecondCarSelected_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenaltyAction = "10 laps", IsCar2Highlighted = true };
        logs["1"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].Warnings);
        // Car1 is not highlighted, so no penalty for car 1
        Assert.AreEqual(0, results["1"].Laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Laps_Multicar_SecondCarSelected_UseSecond_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenaltyAction = "10 laps", IsCar2Highlighted = true };
        logs["1"] = [log];
        logs["2"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(2, results);
        Assert.AreEqual(0, results["2"].Warnings);
        Assert.AreEqual(10, results["2"].Laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Laps_Invalid_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenaltyAction = "xx Laps" };
        logs["1"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].Warnings);
        Assert.AreEqual(0, results["1"].Laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Warnings_And_Laps_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log1 = new ControlLogEntry { Car1 = "1", PenaltyAction = "2 Laps" };
        var log2 = new ControlLogEntry { Car1 = "1", PenaltyAction = "Warning" };
        logs["1"] = [log1, log2];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(1, results["1"].Warnings);
        Assert.AreEqual(2, results["1"].Laps);
        Assert.AreEqual(0, results["1"].BlackFlags);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_BlackFlag_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenaltyAction = "Drive Through Penalty" };
        logs["1"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].Warnings);
        Assert.AreEqual(0, results["1"].Laps);
        Assert.AreEqual(1, results["1"].BlackFlags);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_BlackFlag_CaseInsensitive_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenaltyAction = "drive through penalty" };
        logs["1"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(1, results["1"].BlackFlags);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_BlackFlag_Invalid_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenaltyAction = "Drive through" };
        logs["1"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].BlackFlags);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_BlackFlag_Multicar_HighlightedCar_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenaltyAction = "Drive Through Penalty", IsCar2Highlighted = true };
        logs["1"] = [log];
        logs["2"] = [log];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(2, results);
        Assert.AreEqual(0, results["1"].BlackFlags, "Car 1 should not have a black flag (not highlighted)");
        Assert.AreEqual(1, results["2"].BlackFlags, "Car 2 should have 1 black flag (highlighted)");
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Warnings_Laps_And_BlackFlags_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log1 = new ControlLogEntry { Car1 = "1", PenaltyAction = "2 Laps" };
        var log2 = new ControlLogEntry { Car1 = "1", PenaltyAction = "Warning" };
        var log3 = new ControlLogEntry { Car1 = "1", PenaltyAction = "Drive Through Penalty" };
        logs["1"] = [log1, log2, log3];
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);
        Assert.HasCount(1, results);
        Assert.AreEqual(1, results["1"].Warnings);
        Assert.AreEqual(2, results["1"].Laps);
        Assert.AreEqual(1, results["1"].BlackFlags);
    }

    #endregion

    #region ChampCar BlackFlag Pattern Tests

    private static readonly Regex ChampCarBlackFlagRegex = new(@".*Drive.*Through.*|\d+.Min(ute)?.*|.*Min(ute)?\s*Hold.*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [TestMethod]
    [DataRow("Driver Through")]
    [DataRow("2 Minute Hold")]
    [DataRow("5-minute hold")]
    [DataRow("30-minute hold and driver wristband removed for the race")]
    [DataRow("Driver Counseling Drive Through/RD Discretion")]
    [DataRow("Driver Counseling Drive Through")]
    [DataRow("1-Minute")]
    [DataRow("5-Minute")]
    [DataRow("10-Minute hold")]
    [DataRow("3 min hold at pit out")]
    [DataRow("Drive through and firmware update")]
    [DataRow("Drive-through to fix GPS connection or replace")]
    [DataRow("Drive through penalty")]
    public void GetPenaltyCounts_ChampCar_BlackFlag_Matches(string penaltyAction)
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>
        {
            ["1"] = [new ControlLogEntry { Car1 = "1", PenaltyAction = penaltyAction }]
        };
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, ChampCarBlackFlagRegex);
        Assert.AreEqual(1, results["1"].BlackFlags, $"'{penaltyAction}' should match ChampCar black flag pattern");
    }

    [TestMethod]
    [DataRow("Warning")]
    [DataRow("2 Laps")]
    [DataRow("No Action")]
    public void GetPenaltyCounts_ChampCar_BlackFlag_DoesNotMatch(string penaltyAction)
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>
        {
            ["1"] = [new ControlLogEntry { Car1 = "1", PenaltyAction = penaltyAction }]
        };
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, ChampCarBlackFlagRegex);
        Assert.AreEqual(0, results["1"].BlackFlags, $"'{penaltyAction}' should NOT match ChampCar black flag pattern");
    }

    #endregion

    #region Worksheet Change Demonstration Tests

    /// <summary>
    /// This test demonstrates the core issue with worksheet changes.
    /// It simulates what should happen when a Google Sheets control log switches worksheets.
    /// The test validates that the static penalty calculation works correctly for different datasets.
    /// 
    /// The real issue is in GoogleSheetsControlLog where columnIndexMappings is cached and not cleared
    /// when the worksheet parameter changes, causing parsing to fail when column layouts differ.
    /// </summary>
    [TestMethod]
    public void GetWarningsAndPenalties_Worksheet_Change_Scenario_Test()
    {
        // Arrange - Simulate first worksheet data from reference Google Sheet
        // ChampCar format: Time, Car #, Car #, Corner, Cause, Flag State, Penalty/Action, Status, Other Notes
        var worksheet1Logs = new Dictionary<string, List<ControlLogEntry>>
        {
            ["15"] = [new ControlLogEntry 
            { 
                Car1 = "15", 
                Corner = "T1", 
                Note = "Off track", 
                Status = "Observed", 
                PenaltyAction = "Warning", 
                OtherNotes = "Car went wide in turn 1",
                Timestamp = new DateTime(2025, 1, 15, 14, 30, 0),
                OrderId = 1
            }],
            ["23"] = [new ControlLogEntry 
            { 
                Car1 = "23", 
                Corner = "T3", 
                Note = "Contact with barriers", 
                Status = "Incident",
                PenaltyAction = "1 Lap", 
                OtherNotes = "Hit outside wall",
                Timestamp = new DateTime(2025, 1, 15, 14, 35, 0),
                OrderId = 2
            }],
            ["7"] = [new ControlLogEntry 
            { 
                Car1 = "7", 
                Car2 = "42", 
                Corner = "T5", 
                Note = "Racing incident", 
                Status = "No Action", 
                OtherNotes = "Both cars racing for position", 
                IsCar1Highlighted = false,  // No penalty applied to either car
                IsCar2Highlighted = false,
                Timestamp = new DateTime(2025, 1, 15, 14, 40, 0),
                OrderId = 3
            }]
        };

        // Simulate second worksheet data (completely different entries)
        var worksheet2Logs = new Dictionary<string, List<ControlLogEntry>>
        {
            ["88"] = [new ControlLogEntry 
            { 
                Car1 = "88", 
                Corner = "Pit Lane", 
                Note = "Speeding in pits", 
                Status = "Penalty",
                PenaltyAction = "2 Laps", 
                OtherNotes = "Exceeded pit speed limit by 5mph",
                Timestamp = new DateTime(2025, 1, 15, 15, 0, 0),
                OrderId = 1
            }],
            ["33"] = [new ControlLogEntry 
            { 
                Car1 = "12", 
                Car2 = "33", 
                Corner = "T4", 
                Note = "Blocking", 
                Status = "Incident",
                PenaltyAction = "Warning", 
                OtherNotes = "Impeding during qualifying", 
                IsCar2Highlighted = true,  // Car 33 gets the penalty
                Timestamp = new DateTime(2025, 1, 15, 15, 5, 0),
                OrderId = 2
            }],
            ["99"] = [new ControlLogEntry 
            { 
                Car1 = "99", 
                Corner = "Start/Finish", 
                Note = "Jump start", 
                Status = "Penalty",
                PenaltyAction = "Drive Through Penalty", 
                OtherNotes = "Left grid position before green flag",
                Timestamp = new DateTime(2025, 1, 15, 15, 10, 0),
                OrderId = 3
            }]
        };

        // Act - Process first worksheet
        var worksheet1Results = ControlLogCache.GetPenaltyCounts(worksheet1Logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);

        // Assert - Verify first worksheet results
        Assert.HasCount(3, worksheet1Results, "Should have entries for 3 cars from worksheet 1");
        
        // Car 15 gets a warning
        Assert.IsTrue(worksheet1Results.ContainsKey("15"), "Should have penalties for car 15");
        Assert.AreEqual(1, worksheet1Results["15"].Warnings, "Car 15 should have 1 warning");
        Assert.AreEqual(0, worksheet1Results["15"].Laps, "Car 15 should have 0 lap penalties");
        Assert.AreEqual(0, worksheet1Results["15"].BlackFlags, "Car 15 should have 0 black flags");
        
        // Car 23 gets 1 lap penalty
        Assert.IsTrue(worksheet1Results.ContainsKey("23"), "Should have penalties for car 23");
        Assert.AreEqual(0, worksheet1Results["23"].Warnings, "Car 23 should have 0 warnings");
        Assert.AreEqual(1, worksheet1Results["23"].Laps, "Car 23 should have 1 lap penalty");
        Assert.AreEqual(0, worksheet1Results["23"].BlackFlags, "Car 23 should have 0 black flags");
        
        // Car 7 gets no penalty (no action status)
        Assert.IsTrue(worksheet1Results.ContainsKey("7"), "Should have entry for car 7");
        Assert.AreEqual(0, worksheet1Results["7"].Warnings, "Car 7 should have 0 warnings");
        Assert.AreEqual(0, worksheet1Results["7"].Laps, "Car 7 should have 0 lap penalties");
        Assert.AreEqual(0, worksheet1Results["7"].BlackFlags, "Car 7 should have 0 black flags");

        // Act - Process second worksheet (simulating worksheet change)
        var worksheet2Results = ControlLogCache.GetPenaltyCounts(worksheet2Logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);

        // Assert - Verify second worksheet results (completely different data)
        Assert.HasCount(3, worksheet2Results, "Should have entries for 3 cars from worksheet 2");
        
        // Verify old worksheet cars are not present
        Assert.IsFalse(worksheet2Results.ContainsKey("15"), "Should NOT have car 15 from old worksheet");
        Assert.IsFalse(worksheet2Results.ContainsKey("23"), "Should NOT have car 23 from old worksheet");
        
        // Verify new worksheet cars are present with correct penalties
        Assert.IsTrue(worksheet2Results.ContainsKey("88"), "Should have penalties for car 88 from new worksheet");
        Assert.AreEqual(0, worksheet2Results["88"].Warnings, "Car 88 should have 0 warnings");
        Assert.AreEqual(2, worksheet2Results["88"].Laps, "Car 88 should have 2 lap penalties");
        Assert.AreEqual(0, worksheet2Results["88"].BlackFlags, "Car 88 should have 0 black flags");

        Assert.IsTrue(worksheet2Results.ContainsKey("33"), "Should have penalties for car 33 (highlighted car)");
        Assert.AreEqual(1, worksheet2Results["33"].Warnings, "Car 33 should have 1 warning");
        Assert.AreEqual(0, worksheet2Results["33"].Laps, "Car 33 should have 0 lap penalties");
        Assert.AreEqual(0, worksheet2Results["33"].BlackFlags, "Car 33 should have 0 black flags");

        Assert.IsTrue(worksheet2Results.ContainsKey("99"), "Should have entry for car 99 from new worksheet");
        Assert.AreEqual(0, worksheet2Results["99"].Warnings, "Car 99 should have 0 warnings");
        Assert.AreEqual(0, worksheet2Results["99"].Laps, "Car 99 should have 0 lap penalties");
        Assert.AreEqual(1, worksheet2Results["99"].BlackFlags, "Car 99 should have 1 black flag (Drive Through Penalty)");
    }

    /// <summary>
    /// Test the highlighting behavior that determines which car in a multi-car incident gets the penalty
    /// This demonstrates the logic that should work correctly when worksheets change
    /// </summary>
    [TestMethod]
    public void GetWarningsAndPenalties_MultiCar_Highlighting_Behavior_Test()
    {
        // Arrange - Test various highlighting scenarios
        var logs = new Dictionary<string, List<ControlLogEntry>>
        {
            // Car 15 highlighted - gets the penalty
            ["15"] = [new ControlLogEntry 
            { 
                Car1 = "15", 
                Car2 = "23",
                PenaltyAction = "Warning", 
                IsCar1Highlighted = true 
            }],
            
            // Car 42 highlighted - gets the penalty (not car 7)
            ["42"] = [new ControlLogEntry 
            { 
                Car1 = "7", 
                Car2 = "42",
                PenaltyAction = "1 Lap", 
                IsCar2Highlighted = true 
            }],
            
            // No highlighting - car 88 (Car1) gets penalty by default
            ["88"] = [new ControlLogEntry 
            { 
                Car1 = "88", 
                Car2 = "99",
                PenaltyAction = "2 Laps" 
            }],
            
            // Single car - always gets the penalty
            ["12"] = [new ControlLogEntry 
            { 
                Car1 = "12",
                PenaltyAction = "Warning" 
            }]
        };

        // Act
        var results = ControlLogCache.GetPenaltyCounts(logs, WarningRegex, LapPenaltyRegex, BlackFlagRegex);

        // Assert
        Assert.HasCount(4, results, "Should have penalties for 4 cars");

        // Verify highlighted cars get penalties
        Assert.AreEqual(1, results["15"].Warnings, "Car 15 (highlighted) should have 1 warning");
        Assert.AreEqual(1, results["42"].Laps, "Car 42 (highlighted) should have 1 lap penalty");
        Assert.AreEqual(2, results["88"].Laps, "Car 88 (default) should have 2 lap penalties");
        Assert.AreEqual(1, results["12"].Warnings, "Car 12 (single car) should have 1 warning");

        // Verify non-highlighted cars don't appear in results
        Assert.IsFalse(results.ContainsKey("23"), "Car 23 should not have penalties (not highlighted)");
        Assert.IsFalse(results.ContainsKey("7"), "Car 7 should not have penalties (not highlighted)");
        Assert.IsFalse(results.ContainsKey("99"), "Car 99 should not have penalties (not highlighted)");
    }

    #endregion
}
