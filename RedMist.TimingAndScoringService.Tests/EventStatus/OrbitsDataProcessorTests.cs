using BigMission.TestHelpers.Testing;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using RedMist.Database;
using RedMist.TimingAndScoringService.EventStatus;
using RedMist.TimingAndScoringService.EventStatus.X2;

namespace RedMist.TimingAndScoringService.Tests.EventStatus;

[TestClass]
public class OrbitsDataProcessorTests
{
    private readonly DebugLoggerFactory lf = new();

    #region Heartbeat

    [TestMethod]
    public async Task ProcessF_Parse_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf, new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"", 0);

        Assert.AreEqual(14, processor.Heartbeat.LapsToGo);
        Assert.AreEqual("00:12:45", processor.Heartbeat.TimeToGo);
        Assert.AreEqual("13:34:23", processor.Heartbeat.TimeOfDay);
        Assert.AreEqual("00:09:47", processor.Heartbeat.RaceTime);
        Assert.AreEqual("Green", processor.Heartbeat.FlagStatus);
    }

    [TestMethod]
    public async Task ProcessF_ChangeNotDirty_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"", 0);
        Assert.IsTrue(processor.Heartbeat.IsDirty);

        // Reset the dirty flag manually
        processor.Heartbeat.IsDirty = false;

        await processor.ProcessUpdate("rmonitor", "$F,14,\"10:12:45\",\"14:34:23\",\"00:03:47\",\"Green \"", 0);
        Assert.IsFalse(processor.Heartbeat.IsDirty);
    }

    [TestMethod]
    public async Task ProcessF_ChangeDirty_Laps_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"", 0);
        Assert.IsTrue(processor.Heartbeat.IsDirty);

        // Reset the dirty flag manually
        processor.Heartbeat.IsDirty = false;

        await processor.ProcessUpdate("rmonitor", "$F,15,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"", 0);
        Assert.IsTrue(processor.Heartbeat.IsDirty);
    }

    [TestMethod]
    public async Task ProcessF_ChangeDirty_Flag_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"", 0);
        Assert.IsTrue(processor.Heartbeat.IsDirty);

        // Reset the dirty flag manually
        processor.Heartbeat.IsDirty = false;

        await processor.ProcessUpdate("rmonitor", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Red   \"", 0);
        Assert.IsTrue(processor.Heartbeat.IsDirty);
    }

    [TestMethod]
    public async Task ProcessF_Invalid_EmptyLapsToGo_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$F,,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"", 0);

        Assert.AreEqual(0, processor.Heartbeat.LapsToGo);
    }

    [TestMethod]
    public async Task ProcessF_Invalid_CharsLapsToGo_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$F,asdf,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"", 0);

        Assert.AreEqual(0, processor.Heartbeat.LapsToGo);
    }

    [TestMethod]
    public async Task ProcessF_Invalid_MissingFlag_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$F,9999,\"07:50:29\",\"08:09:30\",", 0);

        Assert.AreEqual(0, processor.Heartbeat.LapsToGo);
    }

    #endregion

    #region Competitor Tests

    [TestMethod]
    public async Task ProcessA_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$A,\"1234BE\",\"12X\",52474,\"John\",\"Johnson\",\"USA\",5", 0);
        await processor.ProcessUpdate("rmonitor", "$C,5,\"Formula 300\"", 0);
        var entry = processor.GetEventEntries();

        Assert.AreEqual("12X", entry[0].Number);
        Assert.AreEqual("John Johnson", entry[0].Name);
        Assert.AreEqual("", entry[0].Team);
        Assert.AreEqual("Formula 300", entry[0].Class);
    }

    [TestMethod]
    public async Task ProcessA_GetWhenDirty_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);

        // Disable the debouncer to bypass premature dirty reset
        processor.Debouncer.IsDisabled = true;

        await processor.ProcessUpdate("rmonitor", "$A,\"1234BE\",\"12X\",52474,\"John\",\"Johnson\",\"USA\",5", 0);
        await processor.ProcessUpdate("rmonitor", "$A,\"1234BE1\",\"122\",52474,\"Fred\",\"Johnson\",\"USA\",5", 0);
        await processor.ProcessUpdate("rmonitor", "$C,5,\"Formula 300\"", 0);
        var entries = processor.GetChangedEventEntries();
        Assert.AreEqual(2, entries.Length);

        await processor.ProcessUpdate("rmonitor", "$A,\"1234BE1\",\"122\",52474,\"Bob\",\"Johnson\",\"USA\",5", 0);
        entries = processor.GetChangedEventEntries();
        Assert.AreEqual(1, entries.Length);
        Assert.AreEqual("Bob Johnson", entries[0].Name);
    }

    [TestMethod]
    public async Task ProcessA_NoClassMapping_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$A,\"1234BE\",\"12X\",52474,\"John\",\"Johnson\",\"USA\",5", 0);
        var entry = processor.GetEventEntries();

        Assert.AreEqual("5", entry[0].Class);
    }

    [TestMethod]
    public async Task ProcessComp_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$COMP,\"1234BE\",\"12X\",5,\"John\",\"Johnson\",\"USA\",\"CAMEL\"", 0);
        await processor.ProcessUpdate("rmonitor", "$C,5,\"Formula 300\"", 0);
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
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$COMP,\"1234BE\",\"12X\",5,\"John\",\"Johnson\",\"USA\",\"CAMEL\"", 0);
        var entry = processor.GetEventEntries();

        Assert.AreEqual("5", entry[0].Class);
    }

    #endregion

    #region Event Tests

    [TestMethod]
    public async Task ProcessEvent_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$B,5,\"Friday free practice\"", 0);
        var @event = processor.GetEvent();

        Assert.AreEqual("Friday free practice", @event.EventName);
    }

    [TestMethod]
    public async Task ProcessEvent_BadRef_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$B,asdf,\"Friday free practice\"", 0);
        var @event = processor.GetEvent();

        Assert.AreEqual("", @event.EventName);
    }

    [TestMethod]
    public async Task ProcessEvent_NoName_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$B,2", 0);
        var @event = processor.GetEvent();

        Assert.AreEqual("", @event.EventName);
    }

    #endregion

    #region Class Tests

    [TestMethod]
    public async Task ProcessClass_Single_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$C,5,\"Formula 300\"", 0);
        var classes = processor.GetClasses();

        Assert.AreEqual(1, classes.Count);
        Assert.AreEqual("Formula 300", classes[5]);
    }

    [TestMethod]
    public async Task ProcessClass_Bulk_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$C,1,\"GTU\"\n$C,2,\"GTO\"\n$C,3,\"GP1\"\n$C,4,\"GP2\"", 0);
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
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$C,1,\"GTU\"", 0);
        await processor.ProcessUpdate("rmonitor", "$C,2,\"GTO\"", 0);
        await processor.ProcessUpdate("rmonitor", "$C,3,\"GP1\"", 0);
        await processor.ProcessUpdate("rmonitor", "$C,4,\"GP2\"", 0);
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
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$C,1", 0);
        var classes = processor.GetClasses();
        Assert.AreEqual(0, classes.Count);
    }

    [TestMethod]
    public async Task ProcessClass_Malformed_MissingInt_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$C,a", 0);
        var classes = processor.GetClasses();
        Assert.AreEqual(0, classes.Count);
    }

    [TestMethod]
    public async Task ProcessClass_Malformed_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$Casdkjnalmngka", 0);
        var classes = processor.GetClasses();
        Assert.AreEqual(0, classes.Count);
    }

    #endregion

    #region Setting information

    [TestMethod]
    public async Task ProcessSeries_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);

        await processor.ProcessUpdate("rmonitor", "$E,\"TRACKNAME\",\"Indianapolis Motor Speedway\"", 0);
        Assert.AreEqual("Indianapolis Motor Speedway", processor.TrackName);

        await processor.ProcessUpdate("rmonitor", "$E,\"TRACKLENGTH\",\"2.500\"", 0);
        Assert.AreEqual(2.500, processor.TrackLength, 0.001);
    }

    [TestMethod]
    public async Task ProcessSeries_InvalidDesc_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);

        await processor.ProcessUpdate("rmonitor", "$E,\"wefahbt\",\"Indianapolis Motor Speedway\"", 0);
        Assert.AreEqual("", processor.TrackName);
        Assert.AreEqual(0, processor.TrackLength, 0.0001);
    }

    #endregion

    #region Race Information

    [TestMethod]
    public async Task ProcessRaceInfo_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);

        await processor.ProcessUpdate("rmonitor", "$G,3,\"1234BE\",14,\"01:12:47.872\"", 0);
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
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);

        await processor.ProcessUpdate("rmonitor", "$G,3,\"1234BE\",14,", 0);
        var raceInfo = processor.GetRaceInformation();
        Assert.AreEqual(default, raceInfo["1234BE"].Timestamp);
    }

    [TestMethod]
    public async Task ProcessRaceInfo_InvalidTime_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);

        await processor.ProcessUpdate("rmonitor", "$G,3,\"1234BE\",14,\"01asdf:we12we:47.872\"", 0);
        var raceInfo = processor.GetRaceInformation();
        Assert.AreEqual(default, raceInfo["1234BE"].Timestamp);
    }

    [TestMethod]
    public async Task ProcessRaceInfo_Invalid_EmptyLaps_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);

        await processor.ProcessUpdate("rmonitor", "$G,3,\"1234BE\",,\"01:12:47.872\"", 0);
        var raceInfo = processor.GetRaceInformation();
        Assert.AreEqual(0, raceInfo["1234BE"].Laps);
    }

    [TestMethod]
    public async Task ProcessRaceInfo_Invalid_NonIntLaps_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);

        await processor.ProcessUpdate("rmonitor", "$G,3,\"1234BE\",asdf,\"01:12:47.872\"", 0);
        var raceInfo = processor.GetRaceInformation();
        Assert.AreEqual(0, raceInfo["1234BE"].Laps);
    }

    [TestMethod]
    public async Task ProcessRaceInfo_StartingPosition_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);

        await processor.ProcessUpdate("rmonitor", "$G,10,\"89\",,\"00:00:00.000\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,11,\"188\",,\"00:00:00.000\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,12,\"68\",,\"00:00:00.000\"", 0);
        var raceInfo = processor.GetOverallStartingPositions();
        Assert.AreEqual(3, raceInfo.Count);
    }

    [TestMethod]
    public async Task ProcessRaceInfo_StartingPosition_NonStarting_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);

        await processor.ProcessUpdate("rmonitor", "$G,10,\"89\",,\"00:00:00.000\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,3,\"1234BE\",,\"01:12:47.872\"", 0); // Now using laps and flag rather than time

        // Invalid
        await processor.ProcessUpdate("rmonitor", "$G,12,\"68\",27,\"00:00:00.000\"", 0);

        var raceInfo = processor.GetOverallStartingPositions();
        Assert.AreEqual(2, raceInfo.Count);
    }

    [TestMethod]
    public async Task ProcessRaceInfo_InClassStartingPosition_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        await processor.ProcessUpdate("rmonitor", "$COMP,\"89\",\"89\",1,\"John\",\"Johnson\",\"USA\",\"CAMEL\"", 0);
        await processor.ProcessUpdate("rmonitor", "$COMP,\"188\",\"89\",1,\"John\",\"Johnson\",\"USA\",\"CAMEL\"", 0);
        await processor.ProcessUpdate("rmonitor", "$COMP,\"68\",\"89\",1,\"John\",\"Johnson\",\"USA\",\"CAMEL\"", 0);
        await processor.ProcessUpdate("rmonitor", "$COMP,\"99\",\"99\",2,\"John\",\"Johnson\",\"USA\",\"CAMEL\"", 0);
        await processor.ProcessUpdate("rmonitor", "$COMP,\"100\",\"100\",2,\"John\",\"Johnson\",\"USA\",\"CAMEL\"", 0);

        await processor.ProcessUpdate("rmonitor", "$G,10,\"89\",,\"00:00:00.000\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,11,\"188\",,\"00:00:00.000\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,12,\"68\",,\"00:00:00.000\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,13,\"99\",,\"00:00:00.000\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,14,\"100\",,\"00:00:00.000\"", 0);

        var raceInfo = processor.GetInClassStartingPositions();
        Assert.AreEqual(5, raceInfo.Count);
        Assert.AreEqual(1, raceInfo["89"]);
        Assert.AreEqual(2, raceInfo["188"]);
        Assert.AreEqual(3, raceInfo["68"]);
        Assert.AreEqual(1, raceInfo["99"]);
        Assert.AreEqual(2, raceInfo["100"]);
    }

    [TestMethod]
    public async Task ProcessRaceInfo_InClassStartingPosition_MissingClassInfo_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        //await processor.ProcessUpdate("rmonitor", "$COMP,\"89\",\"89\",1,\"John\",\"Johnson\",\"USA\",\"CAMEL\"");
        await processor.ProcessUpdate("rmonitor", "$COMP,\"188\",\"89\",1,\"John\",\"Johnson\",\"USA\",\"CAMEL\"", 0);
        await processor.ProcessUpdate("rmonitor", "$COMP,\"68\",\"89\",1,\"John\",\"Johnson\",\"USA\",\"CAMEL\"", 0);
        await processor.ProcessUpdate("rmonitor", "$COMP,\"99\",\"99\",2,\"John\",\"Johnson\",\"USA\",\"CAMEL\"", 0);
        await processor.ProcessUpdate("rmonitor", "$COMP,\"100\",\"100\",2,\"John\",\"Johnson\",\"USA\",\"CAMEL\"", 0);

        await processor.ProcessUpdate("rmonitor", "$G,10,\"89\",,\"00:00:00.000\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,11,\"188\",,\"00:00:00.000\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,12,\"68\",,\"00:00:00.000\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,13,\"99\",,\"00:00:00.000\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,14,\"100\",,\"00:00:00.000\"", 0);

        var raceInfo = processor.GetInClassStartingPositions();
        Assert.AreEqual(4, raceInfo.Count);
        //Assert.AreEqual(1, raceInfo["89"]);
        Assert.AreEqual(1, raceInfo["188"]);
        Assert.AreEqual(2, raceInfo["68"]);
        Assert.AreEqual(1, raceInfo["99"]);
        Assert.AreEqual(2, raceInfo["100"]);
    }

    #endregion

    #region Practice/qualifying information

    [TestMethod]
    public async Task ProcessPracticingQualifying_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);

        await processor.ProcessUpdate("rmonitor", "$H,2,\"1234BE\",3,\"00:02:17.872\"", 0);
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
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(0, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);

        await processor.ProcessUpdate("rmonitor", "$J,\"1234BE\",\"00:02:03.826\",\"01:42:17.672\"", 0);
        var raceInfo = processor.GetPassingInformation();
        Assert.AreEqual(1, raceInfo.Count);
        Assert.AreEqual("00:02:03.826", raceInfo["1234BE"].LapTime);
        Assert.AreEqual(new DateTime(1, 1, 1, 0, 2, 3, 826).TimeOfDay, raceInfo["1234BE"].LapTimestamp.TimeOfDay);
        Assert.AreEqual("01:42:17.672", raceInfo["1234BE"].RaceTime);
        Assert.AreEqual(new DateTime(1, 1, 1, 1, 42, 17, 672).TimeOfDay, raceInfo["1234BE"].RaceTimestamp.TimeOfDay);
    }

    #endregion

    #region Car Positions

    [TestMethod]
    public async Task CarPositions_Dirty_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(1, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        processor.Debouncer.IsDisabled = true;

        await processor.ProcessUpdate("rmonitor", "$B,5,\"Friday free practice\"", 0);
        await processor.ProcessUpdate("rmonitor", "$A,\"1234BE\",\"12X\",52474,\"John\",\"Johnson\",\"USA\",5", 0);
        await processor.ProcessUpdate("rmonitor", "$C,5,\"Formula 300\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,3,\"1234BE\",14,\"01:12:47.872\"", 0);
        await processor.ProcessUpdate("rmonitor", "$J,\"1234BE\",\"00:02:03.826\",\"01:42:17.672\"", 0);
        await processor.ProcessUpdate("rmonitor", "$H,2,\"1234BE\",3,\"00:02:17.872\"", 0);

        var car = await processor.GetCarPositions(includeChangedOnly: true);
        Assert.AreEqual(1, car.Length);
        Assert.AreEqual("1234BE", car[0].Number);
        Assert.AreEqual(14, car[0].LastLap);
        Assert.AreEqual(3, car[0].BestLap);
        Assert.AreEqual("00:02:17.872", car[0].BestTime);
        Assert.AreEqual("01:12:47.872", car[0].TotalTime);

        car = await processor.GetCarPositions(includeChangedOnly: true);
        Assert.AreEqual(0, car.Length);
    }

    [TestMethod]
    public async Task CarPositions_All_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(1, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        processor.Debouncer.IsDisabled = true;

        await processor.ProcessUpdate("rmonitor", "$B,5,\"Friday free practice\"", 0);
        await processor.ProcessUpdate("rmonitor", "$A,\"1234BE\",\"12X\",52474,\"John\",\"Johnson\",\"USA\",5", 0);
        await processor.ProcessUpdate("rmonitor", "$C,5,\"Formula 300\"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,3,\"1234BE\",14,\"01:12:47.872\"", 0);
        await processor.ProcessUpdate("rmonitor", "$J,\"1234BE\",\"00:02:03.826\",\"01:42:17.672\"", 0);
        await processor.ProcessUpdate("rmonitor", "$H,2,\"1234BE\",3,\"00:02:17.872\"", 0);

        var car = await processor.GetCarPositions(includeChangedOnly: false);
        Assert.AreEqual(1, car.Length);

        car = await processor.GetCarPositions(includeChangedOnly: false);
        Assert.AreEqual(1, car.Length);

        await processor.ProcessUpdate("rmonitor", "$G,3,\"1234BE\",15,\"01:12:47.872\"", 0);

        car = await processor.GetCarPositions(includeChangedOnly: false);
        Assert.AreEqual(1, car.Length);
        Assert.AreEqual(15, car[0].LastLap);
    }

    #endregion

    #region Payload

    [TestMethod]
    public async Task Payload_All_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(1, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        processor.Debouncer.IsDisabled = true;

        await processor.ProcessUpdate("rmonitor", "$B,5,\"Friday free practice\"", 0);
        await processor.ProcessUpdate("rmonitor", "$A,\"1234BE\",\"12X\",52474,\"John\",\"Johnson\",\"USA\",5", 0);
        await processor.ProcessUpdate("rmonitor", "$C,5,\"Formula 300\"", 0);
        await processor.ProcessUpdate("rmonitor", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \"", 0);
        await processor.ProcessUpdate("rmonitor", "$G,3,\"1234BE\",14,\"01:12:47.872\"", 0);
        await processor.ProcessUpdate("rmonitor", "$J,\"1234BE\",\"00:02:03.826\",\"01:42:17.672\"", 0);
        await processor.ProcessUpdate("rmonitor", "$H,2,\"1234BE\",3,\"00:02:17.872\"", 0);

        var payload = await processor.GetPayload();
        Assert.AreEqual("Friday free practice", payload.EventName);
        Assert.AreEqual(1, payload.CarPositions.Count);
        Assert.AreEqual("1234BE", payload.CarPositions[0].Number);
        Assert.AreEqual(1, payload.EventEntries.Count);
        Assert.AreEqual("12X", payload.EventEntries[0].Number);
        Assert.AreEqual(TimingCommon.Models.Flags.Green, payload.EventStatus?.Flag);
        Assert.AreEqual(14, payload.EventStatus?.LapsToGo);
        Assert.AreEqual("00:12:45", payload.EventStatus?.TimeToGo);
        Assert.AreEqual("13:34:23", payload.EventStatus?.LocalTimeOfDay);
        Assert.AreEqual("00:09:47", payload.EventStatus?.RunningRaceTime);
    }

    [TestMethod]
    public async Task Payload_Flag_Red_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(1, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        processor.Debouncer.IsDisabled = true;

        await processor.ProcessUpdate("rmonitor", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Red \"", 0);

        var payload = await processor.GetPayload();
        Assert.AreEqual(TimingCommon.Models.Flags.Red, payload.EventStatus?.Flag);
    }

    [TestMethod]
    public async Task Payload_Flag_None_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(1, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        processor.Debouncer.IsDisabled = true;

        await processor.ProcessUpdate("rmonitor", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"    \"", 0);

        var payload = await processor.GetPayload();
        Assert.AreEqual(TimingCommon.Models.Flags.Unknown, payload.EventStatus?.Flag);
    }

    [TestMethod]
    public async Task Payload_Flag_Malformed_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(1, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        processor.Debouncer.IsDisabled = true;

        await processor.ProcessUpdate("rmonitor", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"  asdfas  \"", 0);

        var payload = await processor.GetPayload();
        Assert.AreEqual(TimingCommon.Models.Flags.Unknown, payload.EventStatus?.Flag);
    }

    [TestMethod]
    public async Task Payload_Flag_Change_Test()
    {
        var mediatorMock = new Mock<IMediator>();
        var dbMock = new Mock<IDbContextFactory<TsContext>>();
        var pitProcessor = new PitProcessor(0, dbMock.Object, lf);
        var flagProcessor = new FlagProcessor(0, dbMock.Object, lf);
        var competitorMetadataProcessor = new CompetitorMetadataProcessor(0, dbMock.Object, lf);
        var processor = new OrbitsDataProcessor(1, mediatorMock.Object, lf,  new DebugSessionMonitor(0, dbMock.Object), pitProcessor, null, flagProcessor, competitorMetadataProcessor);
        processor.Debouncer.IsDisabled = true;

        await processor.ProcessUpdate("rmonitor", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Red  \"", 0);

        var payload = await processor.GetPayload();
        Assert.AreEqual(TimingCommon.Models.Flags.Red, payload.EventStatus?.Flag);

        await processor.ProcessUpdate("rmonitor", "$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Yellow\"", 0);

        payload = await processor.GetPayload();
        Assert.AreEqual(TimingCommon.Models.Flags.Yellow, payload.EventStatus?.Flag);
    }

    #endregion
}
