using RedMist.TimingAndScoringService.EventStatus.Multiloop;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop;

[TestClass]
public partial class RunInformationTests
{
    [TestMethod]
    public void ProcessHeader_Parse_R_Test()
    {
        var command = "$R�R�400004C7�Q1�Watkins Glen Hoosier Super Tour��Grp 2  FA FC FE2 P P2 Qual 1�Q�685ECBB8";
        var r = new RunInformation();
        r.ProcessR(command);

        Assert.AreEqual("Watkins Glen Hoosier Super Tour", r.EventName);
        Assert.AreEqual("", r.EventShortName);
        Assert.AreEqual("Grp 2  FA FC FE2 P P2 Qual 1", r.RunName);
        Assert.AreEqual("Q", r.RunTypeStr);
        Assert.AreEqual<uint>(1751043000, r.StartTimeDateSec);
    }
}
