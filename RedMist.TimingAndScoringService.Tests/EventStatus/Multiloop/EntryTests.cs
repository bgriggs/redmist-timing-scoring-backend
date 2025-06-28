using RedMist.TimingAndScoringService.EventStatus.Multiloop;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop;

[TestClass]
public class EntryTests
{

    [TestMethod]
    public void ProcessHeader_Parse_E_Test()
    {
        var command = "$E�R�17�Q1�12�17�Steve Introne�18�B�B-Spec�Honda Fit�Windham NH�NER�180337�White�Sripath/PurposeEnergy/BlackHog Beer/BostonMobileTire/Hyperco/G-Loc Brakes/Introne Comm�����17�";
        var e = new Entry();
        e.ProcessE(command);

        Assert.AreEqual("12", e.Number);
        Assert.AreEqual<uint>(23, e.UniqueIdentifier);
        Assert.AreEqual("Steve Introne", e.DriverName);
        Assert.AreEqual(24, e.StartPosition);
        Assert.AreEqual(11, e.FieldCount);
        Assert.AreEqual("B-Spec", e.Fields[0]);
        Assert.AreEqual("Honda Fit", e.Fields[1]);
        Assert.AreEqual("Windham NH", e.Fields[2]);
        Assert.AreEqual("NER", e.Fields[3]);
        Assert.AreEqual("180337", e.Fields[4]);
        Assert.AreEqual("White", e.Fields[5]);
        Assert.AreEqual("Sripath/PurposeEnergy/BlackHog Beer/BostonMobileTire/Hyperco/G-Loc Brakes/Introne Comm", e.Fields[6]);
        Assert.AreEqual("", e.Fields[7]);
        Assert.AreEqual("", e.Fields[8]);
        Assert.AreEqual("", e.Fields[9]);
        Assert.AreEqual("", e.Fields[10]);
        Assert.AreEqual<uint>(23, e.CompetitorIdentifier);
    }
}
