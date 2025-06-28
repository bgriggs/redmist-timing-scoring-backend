using RedMist.TimingAndScoringService.EventStatus.Multiloop;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop;

[TestClass]
public class HeartbeatTests
{
    [TestMethod]
    public void ProcessHeader_Parse_Test()
    {
        var command = "$H�N�2F4�Q1�G�685E9B2F�9A0AB�0�D42B4\r\n";
        var h = new Heartbeat();
        h.ProcessH(command);

        Assert.AreEqual("G", h.TrackStatus);
        Assert.AreEqual<uint>(1751030575, h.TimeDateSec);
        Assert.AreEqual<uint>(630955, h.ElaspedTimeMs);
        Assert.AreEqual(0, h.LapsToGo);
        Assert.AreEqual<uint>(869044, h.TimeToGoMs);
    }
}
