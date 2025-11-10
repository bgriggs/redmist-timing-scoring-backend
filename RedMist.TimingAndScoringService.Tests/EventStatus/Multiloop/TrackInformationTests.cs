using RedMist.EventProcessor.EventStatus.Multiloop;

namespace RedMist.EventProcessor.Tests.EventStatus.Multiloop;

[TestClass]
public class TrackInformationTests
{
    [TestMethod]
    public void ProcessHeader_Parse_T_Test()
    {
        var command = "$T�R�22�Q1�Watkins Glen�WGI�3.4�4�S1�0�SF�IM1�S2�0�IM1�IM3�S3�0�IM3�SF�Spd�1438�IM1�IM2";
        var t = new TrackInformation();
        t.ProcessT(command);

        Assert.AreEqual("Watkins Glen", t.Name);
        Assert.AreEqual("WGI", t.Venue);
        Assert.AreEqual("3.4", t.LengthMi);
        Assert.AreEqual(4, t.SectionCount);
        Assert.AreEqual("S1", t.Sections[0].Name);
        Assert.AreEqual("0", t.Sections[0].LengthInches);
        Assert.AreEqual("SF", t.Sections[0].StartLabel);
        Assert.AreEqual("IM1", t.Sections[0].EndLabel);
        Assert.AreEqual("S2", t.Sections[1].Name);
        Assert.AreEqual("0", t.Sections[1].LengthInches);
        Assert.AreEqual("IM1", t.Sections[1].StartLabel);
        Assert.AreEqual("IM3", t.Sections[1].EndLabel);
        Assert.AreEqual("S3", t.Sections[2].Name);
        Assert.AreEqual("0", t.Sections[2].LengthInches);
        Assert.AreEqual("IM3", t.Sections[2].StartLabel);
        Assert.AreEqual("SF", t.Sections[2].EndLabel);
        Assert.AreEqual("Spd", t.Sections[3].Name);
        Assert.AreEqual("1438", t.Sections[3].LengthInches);
        Assert.AreEqual("IM1", t.Sections[3].StartLabel);
        Assert.AreEqual("IM2", t.Sections[3].EndLabel);
    }

}
