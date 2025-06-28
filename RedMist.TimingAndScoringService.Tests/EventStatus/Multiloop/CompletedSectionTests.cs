using RedMist.TimingAndScoringService.EventStatus.Multiloop;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop;

[TestClass]
public class CompletedSectionTests
{
    [TestMethod]
    public void ProcessHeader_Parse_S_Test()
    {
        var command = "$S�N�F3170000�Q1�99�EF317�S1�2DF3C0E�7C07�5";
        var s = new CompletedSection();
        s.ProcessS(command);

        Assert.AreEqual("99", s.Number);
        Assert.AreEqual<uint>(979735, s.UniqueIdentifier);
        Assert.AreEqual("S1", s.SectionIdentifier);
        Assert.AreEqual<uint>(48184334, s.ElaspedTimeMs);
        Assert.AreEqual<uint>(31751, s.LastSectionTimeMs);
        Assert.AreEqual(5, s.LastLap);
    }
}
