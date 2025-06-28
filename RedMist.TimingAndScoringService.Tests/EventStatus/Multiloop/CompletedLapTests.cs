using RedMist.TimingAndScoringService.EventStatus.Multiloop;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop;

[TestClass]
public class CompletedLapTests
{
    [TestMethod]
    public void ProcessHeader_Parse_C_Test()
    {
        var command = "$C�U�80004�Q1�C�0�8�4�83DDF�1CB83�T�1CB83�4�2E6A�0�649�0�C�1CB83�Unknown�G�1�0�9�0\r\n";
        var c = new CompletedLap();
        c.ProcessC(command);

        Assert.AreEqual(12, c.Rank);
        Assert.AreEqual("0", c.Number);
        Assert.AreEqual<uint>(8, c.UniqueIdentifier);
        Assert.AreEqual(4, c.CompletedLaps);
        Assert.AreEqual<uint>(540127, c.ElaspedTimeMs);
        Assert.AreEqual<uint>(117635, c.LastLapTimeMs);
        Assert.AreEqual("T", c.LapStatus);
        Assert.AreEqual<uint>(117635, c.FastestLapTimeMs);
        Assert.AreEqual<ushort>(4, c.FastestLap);
        Assert.AreEqual<uint>(11882, c.TimeBehindLeaderMs);
        Assert.AreEqual<ushort>(0, c.LapsBehindLeader);
        Assert.AreEqual<uint>(1609, c.TimeBehindPrecedingCarMs);
        Assert.AreEqual<ushort>(0, c.LapsBehindPrecedingCar);
        Assert.AreEqual<ushort>(12, c.OverallRank);
        Assert.AreEqual<uint>(117635, c.OverallBestLapTimeMs);
        Assert.AreEqual("Unknown", c.CurrentStatus);
        Assert.AreEqual("G", c.TrackStatus);
        Assert.AreEqual<uint>(1, c.PitStopCount);
        Assert.AreEqual<uint>(0, c.LastLapPitted);
        Assert.AreEqual<uint>(9, c.StartPosition);
        Assert.AreEqual<uint>(0, c.LapsLed);
    }
}
