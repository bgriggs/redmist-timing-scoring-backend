using RedMist.EventProcessor.EventStatus.Multiloop;

namespace RedMist.EventProcessor.Tests.EventStatus.Multiloop;

[TestClass]
public class FlagInformationTests
{
    [TestMethod]
    public void ProcessHeader_Parse_F_Test()
    {
        var command = "$F�R�5�Q1�K�0�D7108�6�6088A�1�0�1�00�1�81.63";
        var f = new FlagInformation();
        f.ProcessF(command);

        Assert.AreEqual("K", f.TrackStatus);
        Assert.AreEqual(0, f.LapNumber);
        Assert.AreEqual<uint>(880904, f.GreenTimeMs);
        Assert.AreEqual(6, f.GreenLaps);
        Assert.AreEqual<uint>(395402, f.YellowTimeMs);
        Assert.AreEqual(1, f.YellowLaps);
        Assert.AreEqual<uint>(0, f.RedTimeMs);
        Assert.AreEqual(1, f.NumberOfYellows);
        Assert.AreEqual("00", f.CurrentLeader);
        Assert.AreEqual(1, f.LeadChanges);
        Assert.AreEqual("81.63", f.AverageRaceSpeedMph);
    }
}
