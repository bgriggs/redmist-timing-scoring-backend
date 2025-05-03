//using RedMist.ControlLogs;
//using RedMist.TimingCommon.Models;

//namespace RedMist.TimingAndScoringService.Tests;

//[TestClass]
//public class ControlLogCacheTests
//{
//    [TestMethod]
//    public void GetWarningsAndPenalties_Warning_Test()
//    {
//        var logs = new Dictionary<string, List<ControlLogEntry>>();
//        var log = new ControlLogEntry { Car1 = "1", PenalityAction = "waRniNg" };
//        logs["1"] = [log];
//        var results = ControlLogCache.GetWarningsAndPenalties(logs);
//        Assert.AreEqual(1, results.Count);
//        Assert.AreEqual(1, results["1"].warnings);
//        Assert.AreEqual(0, results["1"].laps);
//    }

//    [TestMethod]
//    public void GetWarningsAndPenalties_Warning_Invalid_Test()
//    {
//        var logs = new Dictionary<string, List<ControlLogEntry>>();
//        var log = new ControlLogEntry { Car1 = "1", PenalityAction = "warns" };
//        logs["1"] = [log];
//        var results = ControlLogCache.GetWarningsAndPenalties(logs);
//        Assert.AreEqual(1, results.Count);
//        Assert.AreEqual(0, results["1"].warnings);
//    }

//    [TestMethod]
//    public void GetWarningsAndPenalties_Lap_Test()
//    {
//        var logs = new Dictionary<string, List<ControlLogEntry>>();
//        var log = new ControlLogEntry { Car1 = "1", PenalityAction = "1 lap" };
//        logs["1"] = [log];
//        var results = ControlLogCache.GetWarningsAndPenalties(logs);
//        Assert.AreEqual(1, results.Count);
//        Assert.AreEqual(0, results["1"].warnings);
//        Assert.AreEqual(1, results["1"].laps);
//    }

//    [TestMethod]
//    public void GetWarningsAndPenalties_Lap_Invalid_Test()
//    {
//        var logs = new Dictionary<string, List<ControlLogEntry>>();
//        var log = new ControlLogEntry { Car1 = "1", PenalityAction = "1 loop" };
//        logs["1"] = [log];
//        var results = ControlLogCache.GetWarningsAndPenalties(logs);
//        Assert.AreEqual(1, results.Count);
//        Assert.AreEqual(0, results["1"].warnings);
//        Assert.AreEqual(0, results["1"].laps);
//    }

//    [TestMethod]
//    public void GetWarningsAndPenalties_Laps_Test()
//    {
//        var logs = new Dictionary<string, List<ControlLogEntry>>();
//        var log = new ControlLogEntry { Car1 = "1", PenalityAction = "10 laps" };
//        logs["1"] = [log];
//        var results = ControlLogCache.GetWarningsAndPenalties(logs);
//        Assert.AreEqual(1, results.Count);
//        Assert.AreEqual(0, results["1"].warnings);
//        Assert.AreEqual(10, results["1"].laps);
//    }

//    [TestMethod]
//    public void GetWarningsAndPenalties_Laps_Multicar_NoCarSelected_Ignore_Test()
//    {
//        var logs = new Dictionary<string, List<ControlLogEntry>>();
//        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenalityAction = "10 laps" };
//        logs["1"] = [log];
//        var results = ControlLogCache.GetWarningsAndPenalties(logs);
//        Assert.AreEqual(1, results.Count);
//        Assert.AreEqual(0, results["1"].warnings);
//        Assert.AreEqual(0, results["1"].laps);
//    }

//    [TestMethod]
//    public void GetWarningsAndPenalties_Laps_Multicar_FristCarSelected_Test()
//    {
//        var logs = new Dictionary<string, List<ControlLogEntry>>();
//        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenalityAction = "10 laps", IsCar1Highlighted = true };
//        logs["1"] = [log];
//        var results = ControlLogCache.GetWarningsAndPenalties(logs);
//        Assert.AreEqual(1, results.Count);
//        Assert.AreEqual(0, results["1"].warnings);
//        Assert.AreEqual(10, results["1"].laps);
//    }

//    [TestMethod]
//    public void GetWarningsAndPenalties_Laps_Multicar_SecondCarSelected_Test()
//    {
//        var logs = new Dictionary<string, List<ControlLogEntry>>();
//        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenalityAction = "10 laps", IsCar2Highlighted = true };
//        logs["1"] = [log];
//        var results = ControlLogCache.GetWarningsAndPenalties(logs);
//        Assert.AreEqual(1, results.Count);
//        Assert.AreEqual(0, results["1"].warnings);
//        Assert.AreEqual(0, results["1"].laps);
//    }

//    [TestMethod]
//    public void GetWarningsAndPenalties_Laps_Multicar_SecondCarSelected_UseSecond_Test()
//    {
//        var logs = new Dictionary<string, List<ControlLogEntry>>();
//        var log = new ControlLogEntry { Car1 = "1", Car2 = "2", PenalityAction = "10 laps", IsCar2Highlighted = true };
//        logs["1"] = [log];
//        logs["2"] = [log];
//        var results = ControlLogCache.GetWarningsAndPenalties(logs);
//        Assert.AreEqual(2, results.Count);
//        Assert.AreEqual(0, results["2"].warnings);
//        Assert.AreEqual(10, results["2"].laps);
//    }

//    [TestMethod]
//    public void GetWarningsAndPenalties_Laps_Invalid_Test()
//    {
//        var logs = new Dictionary<string, List<ControlLogEntry>>();
//        var log = new ControlLogEntry { Car1 = "1", PenalityAction = "xx Laps" };
//        logs["1"] = [log];
//        var results = ControlLogCache.GetWarningsAndPenalties(logs);
//        Assert.AreEqual(1, results.Count);
//        Assert.AreEqual(0, results["1"].warnings);
//        Assert.AreEqual(0, results["1"].laps);
//    }

//    [TestMethod]
//    public void GetWarningsAndPenalties_Warnings_And_Laps_Test()
//    {
//        var logs = new Dictionary<string, List<ControlLogEntry>>();
//        var log1 = new ControlLogEntry { Car1 = "1", PenalityAction = "2 Laps" };
//        var log2 = new ControlLogEntry { Car1 = "1", PenalityAction = "Warning" };
//        logs["1"] = [log1, log2];
//        var results = ControlLogCache.GetWarningsAndPenalties(logs);
//        Assert.AreEqual(1, results.Count);
//        Assert.AreEqual(1, results["1"].warnings);
//        Assert.AreEqual(2, results["1"].laps);
//    }
//}
