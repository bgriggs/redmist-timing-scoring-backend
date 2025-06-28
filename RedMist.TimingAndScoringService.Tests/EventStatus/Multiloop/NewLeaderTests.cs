using RedMist.TimingAndScoringService.EventStatus.Multiloop;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop;

[TestClass]
public class NewLeaderTests
{
    [TestMethod]
    public void ProcessHeader_Parse_N_Test()
    {
        var command = "$N�U�80004�Q1�01�469D�45�4CAD63�20";
        var n = new NewLeader();
        n.ProcessC(command);

        Assert.AreEqual("01", n.Number);
        Assert.AreEqual<uint>(18077, n.UniqueIdentifier);
        Assert.AreEqual(69, n.LapNumber);
        Assert.AreEqual<uint>(5025123, n.ElaspedTimeMs);
        Assert.AreEqual(32, n.LeadChangeIndex);
    }
}
