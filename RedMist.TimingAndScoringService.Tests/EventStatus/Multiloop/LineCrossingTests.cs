using RedMist.TimingAndScoringService.EventStatus.Multiloop;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop;

[TestClass]
public class LineCrossingTests
{
    [TestMethod]
    public void ProcessHeader_Parse_L_Test()
    {
        var command = "$L�N�EF325�Q1�89�5�SF�A�9B82E�G�T\r\n";
        var l = new LineCrossing();
        l.ProcessL(command);

        Assert.AreEqual("89", l.Number);
        Assert.AreEqual<uint>(5, l.UniqueIdentifier);
        Assert.AreEqual("SF", l.TimeLine);
        Assert.AreEqual("A", l.SourceStr);
        Assert.AreEqual<uint>(636974, l.ElaspedTimeMs);
        Assert.AreEqual("G", l.TrackStatus);
        Assert.AreEqual("T", l.CrossingStatusStr);
    }
}