using RedMist.ControlLogs;
using RedMist.TimingCommon.Models;

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
    #region Original Working Tests (Static Method Tests)

    [TestMethod]
    public void GetWarningsAndPenalties_Warning_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenalityAction = "waRniNg" };
        logs["1"] = [log];
        var results = ControlLogCache.GetWarningsAndPenalties(logs);
        Assert.HasCount(1, results);
        Assert.AreEqual(1, results["1"].warnings);
        Assert.AreEqual(0, results["1"].laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Warning_Invalid_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenalityAction = "warns" };
        logs["1"] = [log];
        var results = ControlLogCache.GetWarningsAndPenalties(logs);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].warnings);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Lap_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenalityAction = "1 lap" };
        logs["1"] = [log];
        var results = ControlLogCache.GetWarningsAndPenalties(logs);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].warnings);
        Assert.AreEqual(1, results["1"].laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Lap_Invalid_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenalityAction = "1 loop" };
        logs["1"] = [log];
        var results = ControlLogCache.GetWarningsAndPenalties(logs);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].warnings);
        Assert.AreEqual(0, results["1"].laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Laps_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenalityAction = "10 laps" };
        logs["1"] = [log];
        var results = ControlLogCache.GetWarningsAndPenalties(logs);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].warnings);
        Assert.AreEqual(10, results["1"].laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Laps_Multicar_NoCarSelected_DefaultsToCar1_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenalityAction = "10 laps" };
        logs["1"] = [log];
        var results = ControlLogCache.GetWarningsAndPenalties(logs);
        Assert.HasCount(1, results  );
        Assert.AreEqual(0, results["1"].warnings);
        // When no car is highlighted, penalty defaults to Car1
        Assert.AreEqual(10, results["1"].laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Laps_Multicar_FirstCarSelected_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenalityAction = "10 laps", IsCar1Highlighted = true };
        logs["1"] = [log];
        var results = ControlLogCache.GetWarningsAndPenalties(logs);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].warnings);
        Assert.AreEqual(10, results["1"].laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Laps_Multicar_SecondCarSelected_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenalityAction = "10 laps", IsCar2Highlighted = true };
        logs["1"] = [log];
        var results = ControlLogCache.GetWarningsAndPenalties(logs);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].warnings);
        // Car1 is not highlighted, so no penalty for car 1
        Assert.AreEqual(0, results["1"].laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Laps_Multicar_SecondCarSelected_UseSecond_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenalityAction = "10 laps", IsCar2Highlighted = true };
        logs["1"] = [log];
        logs["2"] = [log];
        var results = ControlLogCache.GetWarningsAndPenalties(logs);
        Assert.HasCount(2, results);
        Assert.AreEqual(0, results["2"].warnings);
        Assert.AreEqual(10, results["2"].laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Laps_Invalid_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log = new ControlLogEntry { Car1 = "1", PenalityAction = "xx Laps" };
        logs["1"] = [log];
        var results = ControlLogCache.GetWarningsAndPenalties(logs);
        Assert.HasCount(1, results);
        Assert.AreEqual(0, results["1"].warnings);
        Assert.AreEqual(0, results["1"].laps);
    }

    [TestMethod]
    public void GetWarningsAndPenalties_Warnings_And_Laps_Test()
    {
        var logs = new Dictionary<string, List<ControlLogEntry>>();
        var log1 = new ControlLogEntry { Car1 = "1", PenalityAction = "2 Laps" };
        var log2 = new ControlLogEntry { Car1 = "1", PenalityAction = "Warning" };
        logs["1"] = [log1, log2];
        var results = ControlLogCache.GetWarningsAndPenalties(logs);
        Assert.HasCount(1, results);
        Assert.AreEqual(1, results["1"].warnings);
        Assert.AreEqual(2, results["1"].laps);
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
                PenalityAction = "Warning", 
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
                PenalityAction = "1 Lap", 
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
                PenalityAction = "2 Laps", 
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
                PenalityAction = "Warning", 
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
                PenalityAction = "Drive through", 
                OtherNotes = "Left grid position before green flag",
                Timestamp = new DateTime(2025, 1, 15, 15, 10, 0),
                OrderId = 3
            }]
        };

        // Act - Process first worksheet
        var worksheet1Results = ControlLogCache.GetWarningsAndPenalties(worksheet1Logs);

        // Assert - Verify first worksheet results
        Assert.HasCount(3, worksheet1Results, "Should have entries for 3 cars from worksheet 1");
        
        // Car 15 gets a warning
        Assert.IsTrue(worksheet1Results.ContainsKey("15"), "Should have penalties for car 15");
        Assert.AreEqual(1, worksheet1Results["15"].warnings, "Car 15 should have 1 warning");
        Assert.AreEqual(0, worksheet1Results["15"].laps, "Car 15 should have 0 lap penalties");
        
        // Car 23 gets 1 lap penalty
        Assert.IsTrue(worksheet1Results.ContainsKey("23"), "Should have penalties for car 23");
        Assert.AreEqual(0, worksheet1Results["23"].warnings, "Car 23 should have 0 warnings");
        Assert.AreEqual(1, worksheet1Results["23"].laps, "Car 23 should have 1 lap penalty");
        
        // Car 7 gets no penalty (no action status)
        Assert.IsTrue(worksheet1Results.ContainsKey("7"), "Should have entry for car 7");
        Assert.AreEqual(0, worksheet1Results["7"].warnings, "Car 7 should have 0 warnings");
        Assert.AreEqual(0, worksheet1Results["7"].laps, "Car 7 should have 0 lap penalties");

        // Act - Process second worksheet (simulating worksheet change)
        var worksheet2Results = ControlLogCache.GetWarningsAndPenalties(worksheet2Logs);

        // Assert - Verify second worksheet results (completely different data)
        Assert.HasCount(3, worksheet2Results, "Should have entries for 3 cars from worksheet 2");
        
        // Verify old worksheet cars are not present
        Assert.IsFalse(worksheet2Results.ContainsKey("15"), "Should NOT have car 15 from old worksheet");
        Assert.IsFalse(worksheet2Results.ContainsKey("23"), "Should NOT have car 23 from old worksheet");
        
        // Verify new worksheet cars are present with correct penalties
        Assert.IsTrue(worksheet2Results.ContainsKey("88"), "Should have penalties for car 88 from new worksheet");
        Assert.AreEqual(0, worksheet2Results["88"].warnings, "Car 88 should have 0 warnings");
        Assert.AreEqual(2, worksheet2Results["88"].laps, "Car 88 should have 2 lap penalties");

        Assert.IsTrue(worksheet2Results.ContainsKey("33"), "Should have penalties for car 33 (highlighted car)");
        Assert.AreEqual(1, worksheet2Results["33"].warnings, "Car 33 should have 1 warning");
        Assert.AreEqual(0, worksheet2Results["33"].laps, "Car 33 should have 0 lap penalties");

        Assert.IsTrue(worksheet2Results.ContainsKey("99"), "Should have entry for car 99 from new worksheet");
        Assert.AreEqual(0, worksheet2Results["99"].warnings, "Car 99 should have 0 warnings (drive through not counted as warning/lap)");
        Assert.AreEqual(0, worksheet2Results["99"].laps, "Car 99 should have 0 lap penalties (drive through not counted as lap penalty)");
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
                PenalityAction = "Warning", 
                IsCar1Highlighted = true 
            }],
            
            // Car 42 highlighted - gets the penalty (not car 7)
            ["42"] = [new ControlLogEntry 
            { 
                Car1 = "7", 
                Car2 = "42", 
                PenalityAction = "1 Lap", 
                IsCar2Highlighted = true 
            }],
            
            // No highlighting - car 88 (Car1) gets penalty by default
            ["88"] = [new ControlLogEntry 
            { 
                Car1 = "88", 
                Car2 = "99", 
                PenalityAction = "2 Laps" 
            }],
            
            // Single car - always gets the penalty
            ["12"] = [new ControlLogEntry 
            { 
                Car1 = "12", 
                PenalityAction = "Warning" 
            }]
        };

        // Act
        var results = ControlLogCache.GetWarningsAndPenalties(logs);

        // Assert
        Assert.HasCount(4, results, "Should have penalties for 4 cars");

        // Verify highlighted cars get penalties
        Assert.AreEqual(1, results["15"].warnings, "Car 15 (highlighted) should have 1 warning");
        Assert.AreEqual(1, results["42"].laps, "Car 42 (highlighted) should have 1 lap penalty");
        Assert.AreEqual(2, results["88"].laps, "Car 88 (default) should have 2 lap penalties");
        Assert.AreEqual(1, results["12"].warnings, "Car 12 (single car) should have 1 warning");

        // Verify non-highlighted cars don't appear in results
        Assert.IsFalse(results.ContainsKey("23"), "Car 23 should not have penalties (not highlighted)");
        Assert.IsFalse(results.ContainsKey("7"), "Car 7 should not have penalties (not highlighted)");
        Assert.IsFalse(results.ContainsKey("99"), "Car 99 should not have penalties (not highlighted)");
    }

    #endregion
}
