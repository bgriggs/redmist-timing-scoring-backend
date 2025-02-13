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

    #region Setting information

    [TestMethod]
    public async Task ProcessSeries_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);

        await processor.ProcessUpdate("$E,\"TRACKNAME\",\"Indianapolis Motor Speedway\"");
        Assert.AreEqual("Indianapolis Motor Speedway", processor.TrackName);
        
        await processor.ProcessUpdate("$E,\"TRACKLENGTH\",\"2.500\"");
        Assert.AreEqual(2.500, processor.TrackLength, 0.001);
    }

    [TestMethod]
    public async Task ProcessSeries_InvalidDesc_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);

        await processor.ProcessUpdate("$E,\"wefahbt\",\"Indianapolis Motor Speedway\"");
        Assert.AreEqual("", processor.TrackName);
        Assert.AreEqual(0, processor.TrackLength, 0.0001);
    }

    #endregion

    #region Race Information

    [TestMethod]
    public async Task ProcessRaceInfo_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);

        await processor.ProcessUpdate("$G,3,\"1234BE\",14,\"01:12:47.872\"");
        var raceInfo = processor.GetRaceInformation();
        Assert.AreEqual(1, raceInfo.Count);
        Assert.AreEqual(3, raceInfo["1234BE"].Position);
        Assert.AreEqual(14, raceInfo["1234BE"].Laps);
        Assert.AreEqual("01:12:47.872", raceInfo["1234BE"].RaceTime);
        Assert.AreEqual(new DateTime(1, 1, 1, 1, 12, 47, 872).TimeOfDay, raceInfo["1234BE"].Timestamp.TimeOfDay);
    }

    [TestMethod]
    public async Task ProcessRaceInfo_EmptyTime_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);

        await processor.ProcessUpdate("$G,3,\"1234BE\",14,");
        var raceInfo = processor.GetRaceInformation();
        Assert.AreEqual(default, raceInfo["1234BE"].Timestamp);
    }

    [TestMethod]
    public async Task ProcessRaceInfo_InvalidTime_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);

        await processor.ProcessUpdate("$G,3,\"1234BE\",14,\"01asdf:we12we:47.872\"");
        var raceInfo = processor.GetRaceInformation();
        Assert.AreEqual(default, raceInfo["1234BE"].Timestamp);
    }

    #endregion

    #region Practice/qualifying information

    [TestMethod]
    public async Task ProcessPracticingQualifying_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);

        await processor.ProcessUpdate("$H,2,\"1234BE\",3,\"00:02:17.872\"");
        var raceInfo = processor.GetPracticeQualifying();
        Assert.AreEqual(1, raceInfo.Count);
        Assert.AreEqual(2, raceInfo["1234BE"].Position);
        Assert.AreEqual(3, raceInfo["1234BE"].BestLap);
        Assert.AreEqual("00:02:17.872", raceInfo["1234BE"].BestLapTime);
        Assert.AreEqual(new DateTime(1, 1, 1, 0, 2, 17, 872).TimeOfDay, raceInfo["1234BE"].BestTimeTimestamp.TimeOfDay);
    }

    #endregion

    #region Passing information

    [TestMethod]
    public async Task ProcessPassingInformation_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var processor = new RmDataProcessor(0, mediatorMock.Object, lf);

        await processor.ProcessUpdate("$J,\"1234BE\",\"00:02:03.826\",\"01:42:17.672\"");
        var raceInfo = processor.GetPassingInformation();
        Assert.AreEqual(1, raceInfo.Count);
        Assert.AreEqual("00:02:03.826", raceInfo["1234BE"].LapTime);
        Assert.AreEqual(new DateTime(1, 1, 1, 0, 2, 3, 826).TimeOfDay, raceInfo["1234BE"].LapTimestamp.TimeOfDay);
        Assert.AreEqual("01:42:17.672", raceInfo["1234BE"].RaceTime);
        Assert.AreEqual(new DateTime(1, 1, 1, 1, 42, 17, 672).TimeOfDay, raceInfo["1234BE"].RaceTimestamp.TimeOfDay);
    }

    #endregion
}
