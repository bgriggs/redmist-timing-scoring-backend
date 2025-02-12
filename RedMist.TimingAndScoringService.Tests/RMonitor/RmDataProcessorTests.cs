using BigMission.TestHelpers.Testing;
using MediatR;
using Moq;
using RedMist.TimingAndScoringService.EventStatus.RMonitor;

namespace RedMist.TimingAndScoringService.Tests.RMonitor;

[TestClass]
public class RmDataProcessorTests
{
    private readonly DebugLoggerFactory lf = new();

    #region Competitor Tests

    [TestMethod]
    public async Task ProcessA_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$A,\"1234BE\",\"12X\",52474,\"John\",\"Johnson\",\"USA\",5");
        await processor.ProcessUpdate("$C,5,\"Formula 300\"");
        var entry = processor.GetEventEntries();

        Assert.AreEqual("12X", entry[0].Number);
        Assert.AreEqual("John Johnson", entry[0].Name);
        Assert.AreEqual("", entry[0].Team);
        Assert.AreEqual("Formula 300", entry[0].Class);
    }

    [TestMethod]
    public async Task ProcessA_NoClassMapping_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$A,\"1234BE\",\"12X\",52474,\"John\",\"Johnson\",\"USA\",5");
        var entry = processor.GetEventEntries();

        Assert.AreEqual("5", entry[0].Class);
    }

    [TestMethod]
    public async Task ProcessComp_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$COMP,\"1234BE\",\"12X\",5,\"John\",\"Johnson\",\"USA\",\"CAMEL\"");
        await processor.ProcessUpdate("$C,5,\"Formula 300\"");
        var entry = processor.GetEventEntries();

        Assert.AreEqual("12X", entry[0].Number);
        Assert.AreEqual("John Johnson", entry[0].Name);
        Assert.AreEqual("CAMEL", entry[0].Team);
        Assert.AreEqual("Formula 300", entry[0].Class);
    }

    [TestMethod]
    public async Task ProcessComp_NoClassMapping_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$COMP,\"1234BE\",\"12X\",5,\"John\",\"Johnson\",\"USA\",\"CAMEL\"");
        var entry = processor.GetEventEntries();

        Assert.AreEqual("5", entry[0].Class);
    }

    #endregion

    #region Event Tests

    [TestMethod]
    public async Task ProcessEvent_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$B,5,\"Friday free practice\"");
        var @event = processor.GetEvent();

        Assert.AreEqual("Friday free practice", @event.EventName);
    }

    [TestMethod]
    public async Task ProcessEvent_BadRef_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$B,asdf,\"Friday free practice\"");
        var @event = processor.GetEvent();

        Assert.AreEqual("", @event.EventName);
    }

    [TestMethod]
    public async Task ProcessEvent_NoName_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$B,2");
        var @event = processor.GetEvent();

        Assert.AreEqual("", @event.EventName);
    }

    #endregion

    #region Class Tests

    [TestMethod]
    public async Task ProcessClass_Single_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$C,5,\"Formula 300\"");
        var classes = processor.GetClasses();

        Assert.AreEqual(1, classes.Count);
        Assert.AreEqual("Formula 300", classes[5]);
    }

    [TestMethod]
    public async Task ProcessClass_Bulk_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$C,1,\"GTU\"\n$C,2,\"GTO\"\n$C,3,\"GP1\"\n$C,4,\"GP2\"");
        var classes = processor.GetClasses();

        Assert.AreEqual(4, classes.Count);
        Assert.AreEqual("GTU", classes[1]);
        Assert.AreEqual("GTO", classes[2]);
        Assert.AreEqual("GP1", classes[3]);
        Assert.AreEqual("GP2", classes[4]);
    }

    [TestMethod]
    public async Task ProcessClass_Multiple_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$C,1,\"GTU\"");
        await processor.ProcessUpdate("$C,2,\"GTO\"");
        await processor.ProcessUpdate("$C,3,\"GP1\"");
        await processor.ProcessUpdate("$C,4,\"GP2\"");
        var classes = processor.GetClasses();

        Assert.AreEqual(4, classes.Count);
        Assert.AreEqual("GTU", classes[1]);
        Assert.AreEqual("GTO", classes[2]);
        Assert.AreEqual("GP1", classes[3]);
        Assert.AreEqual("GP2", classes[4]);
    }

    [TestMethod]
    public async Task ProcessClass_Malformed_MissingData_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$C,1");
        var classes = processor.GetClasses();
        Assert.AreEqual(0, classes.Count);
    }

    [TestMethod]
    public async Task ProcessClass_Malformed_MissingInt_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$C,a");
        var classes = processor.GetClasses();
        Assert.AreEqual(0, classes.Count);
    }

    [TestMethod]
    public async Task ProcessClass_Malformed_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);
        await processor.ProcessUpdate("$Casdkjnalmngka");
        var classes = processor.GetClasses();
        Assert.AreEqual(0, classes.Count);
    }

    #endregion
}
