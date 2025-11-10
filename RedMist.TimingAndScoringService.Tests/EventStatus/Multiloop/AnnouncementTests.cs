using RedMist.EventProcessor.EventStatus.Multiloop;

namespace RedMist.EventProcessor.Tests.EventStatus.Multiloop;

[TestClass]
public class AnnouncementTests
{
    [TestMethod]
    public void ProcessHeader_Parse_A_Test()
    {
        var command = "$A�N�F3170000�Q1�2F�A�U�BC6AD080�Some Message";
        var a = new Announcement();
        a.ProcessA(command);

        Assert.AreEqual(47, a.MessageNumber);
        Assert.AreEqual("A", a.ActionStr);
        Assert.AreEqual("U", a.PriorityStr);
        Assert.AreEqual(3161116800, a.TimestampSecs);
        Assert.AreEqual("Some Message", a.Text);
    }
}
