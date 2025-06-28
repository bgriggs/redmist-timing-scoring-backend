namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop;

[TestClass]
public class VersionTests
{
    [TestMethod]
    public void ProcessHeader_Parse_V_Test()
    {
        var command = "$V�R�1�Q1�1�5�Multiloop feed";
        var v = new TimingAndScoringService.EventStatus.Multiloop.Version();
        v.ProcessV(command);

        Assert.AreEqual(1, v.Major);
        Assert.AreEqual(5, v.Minor);
        Assert.AreEqual("Multiloop feed", v.Info);
    }
}
