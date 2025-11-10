using RedMist.EventProcessor.EventStatus.Multiloop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RedMist.EventProcessor.Tests.EventStatus.Multiloop;

[TestClass]
public class InvalidatedLapTests
{
    [TestMethod]
    public void ProcessHeader_Parse_I_Test()
    {
        var command = "$I�R�F005A�Q1�31�F005A�84E�\r\n";
        var i = new InvalidatedLap();
        i.ProcessI(command);

        Assert.AreEqual("31", i.Number);
        Assert.AreEqual<uint>(983130, i.UniqueIdentifier);
        Assert.AreEqual<uint>(2126, i.ElapsedTimeMs);
    }

    [TestMethod]
    public void ProcessHeader_Parse_I_Partial_Test()
    {
        var command = "$I�N�F005A�Q1�??�F005A�0�\r\n";
        var i = new InvalidatedLap();
        i.ProcessI(command);

        Assert.AreEqual("??", i.Number);
        Assert.AreEqual<uint>(983130, i.UniqueIdentifier);
        Assert.AreEqual<uint>(0, i.ElapsedTimeMs);
    }
}
