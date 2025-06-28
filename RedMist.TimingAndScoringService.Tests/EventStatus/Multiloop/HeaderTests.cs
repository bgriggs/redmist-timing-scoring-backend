using RedMist.TimingAndScoringService.EventStatus.Multiloop;

namespace RedMist.TimingAndScoringService.Tests.EventStatus.Multiloop;

[TestClass]
public class HeaderTests
{
    [TestMethod]
    public void ProcessHeader_Parse_N_Test()
    {
        var command = "$H�N�139B�Q1�C�685E980C�13ABDF�0�0\r\n";
        var m = new Message();
        var parts = m.ProcessHeader(command);

        Assert.IsNotNull(parts);
        Assert.AreEqual(RecordType.New, m.RecordType);
        Assert.AreEqual<uint>(5019, m.Sequence);
        Assert.AreEqual("Q1", m.Preamble);
    }

    [TestMethod]
    public void ProcessHeader_Parse_R_Test()
    {
        var command = "$H�R�139B�Q1�C�685E980C�13ABDF�0�0\r\n";
        var m = new Message();
        var parts = m.ProcessHeader(command);

        Assert.IsNotNull(parts);
        Assert.AreEqual(RecordType.Repeated, m.RecordType);
        Assert.AreEqual<uint>(5019, m.Sequence);
        Assert.AreEqual("Q1", m.Preamble);
    }

    [TestMethod]
    public void ProcessHeader_Parse_U_Test()
    {
        var command = "$H�U�139B�Q1�C�685E980C�13ABDF�0�0\r\n";
        var m = new Message();
        var parts = m.ProcessHeader(command);

        Assert.IsNotNull(parts);
        Assert.AreEqual(RecordType.Updated, m.RecordType);
        Assert.AreEqual<uint>(5019, m.Sequence);
        Assert.AreEqual("Q1", m.Preamble);
    }
}
