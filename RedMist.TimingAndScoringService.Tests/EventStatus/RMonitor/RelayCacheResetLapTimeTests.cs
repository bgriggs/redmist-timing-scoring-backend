using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Hubs;
using RedMist.Backend.Shared.Services;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.PipelineBlocks;
using RedMist.EventProcessor.EventStatus.RMonitor;
using RedMist.EventProcessor.Models;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus.RMonitor;

/// <summary>
/// When the event processor restarts mid-session it asks the relay to resend its cached data set. That
/// cache holds no $J passing records - the only carrier of a car's last lap time - and no $F heartbeat,
/// so the processor has to restore last lap times from its own lap history or every car reads blank
/// until it next crosses start/finish.
/// </summary>
[TestClass]
public class RelayCacheResetLapTimeTests
{
    private RMonitorDataProcessor _processor = null!;
    private SessionContext _sessionContext = null!;
    private Mock<ICarLapHistoryService> _lapHistory = null!;

    /// <summary>
    /// What the relay replays from its cache: a leading reset, competitors, run/class information and
    /// the current $G race information. Deliberately has no $F and no $J, matching EventDataCache.
    /// </summary>
    private static string CachedRelayData(int car12Laps, int car13Laps) =>
        "$I, \"00:00:00\", \"0/0/0000\"\n" +
        "$A,\"12\",\"12\",52474,\"John\",\"Doe\",\"USA\",1\n" +
        "$A,\"13\",\"13\",52475,\"Jane\",\"Roe\",\"USA\",1\n" +
        "$B,5,\"Race\"\n" +
        "$C,1,\"GT\"\n" +
        $"$G,1,\"12\",{car12Laps},\"01:12:47.872\"\n" +
        $"$G,2,\"13\",{car13Laps},\"01:13:01.100\"\n";

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();
        var dbContextFactory = CreateDbContextFactory();

        _lapHistory = new Mock<ICarLapHistoryService>();
        _lapHistory.Setup(x => x.GetLapsAsync(It.IsAny<string>())).ReturnsAsync([]);
        _lapHistory.Setup(x => x.GetLapsAsync("12"))
            .ReturnsAsync([new CarPosition { Number = "12", LastLapTime = "00:02:03.826" }]);
        _lapHistory.Setup(x => x.GetLapsAsync("13"))
            .ReturnsAsync([new CarPosition { Number = "13", LastLapTime = "00:02:11.402" }]);

        _sessionContext = new SessionContext(config, dbContextFactory, loggerFactory.Object, _lapHistory.Object);

        var resetProcessor = new ResetProcessor(_sessionContext, new Mock<IHubContext<StatusHub>>().Object, loggerFactory.Object);
        var startingPositionProcessor = new StartingPositionProcessor(_sessionContext, loggerFactory.Object, dbContextFactory);
        _processor = new RMonitorDataProcessor(loggerFactory.Object, _sessionContext, resetProcessor,
            startingPositionProcessor, new Mock<IMediator>().Object);
    }

    /// <summary>
    /// The restart case: no heartbeat has ever been seen, so the laps carried by $G are the only
    /// evidence the session is already under way.
    /// </summary>
    [TestMethod]
    public async Task CachedRelayData_OnRestartMidSession_RestoresLastLapTimesFromHistory()
    {
        await _processor.ProcessAsync(Message(CachedRelayData(14, 13)), _sessionContext);

        Assert.AreEqual("00:02:03.826", _sessionContext.GetCarByNumber("12")?.LastLapTime);
        Assert.AreEqual("00:02:11.402", _sessionContext.GetCarByNumber("13")?.LastLapTime);
    }

    /// <summary>
    /// Clients are only sent what a patch tells them, so a restore applied to server state alone would
    /// still leave the car blank on screen.
    /// </summary>
    [TestMethod]
    public async Task CachedRelayData_OnRestartMidSession_SendsRestoredLapTimesAsPatches()
    {
        var updates = await _processor.ProcessAsync(Message(CachedRelayData(14, 13)), _sessionContext);

        Assert.IsNotNull(updates);
        var patch = updates.CarPatches.SingleOrDefault(p => p.Number == "12" && p.LastLapTime != null);
        Assert.IsNotNull(patch, "Expected a patch carrying car 12's restored last lap time");
        Assert.AreEqual("00:02:03.826", patch.LastLapTime);
    }

    /// <summary>
    /// A session that has not started has nothing to restore, and restoring anyway would drag the
    /// previous session's lap times onto the new one - the lap history is keyed by event, not session.
    /// </summary>
    [TestMethod]
    public async Task CachedRelayData_AtSessionStart_DoesNotRestoreLapTimes()
    {
        await _processor.ProcessAsync(Message(CachedRelayData(0, 0)), _sessionContext);

        Assert.IsNull(_sessionContext.GetCarByNumber("12")?.LastLapTime);
        _lapHistory.Verify(x => x.GetLapsAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// The restore belongs to a reset carrying a full data set. Ordinary traffic must not trigger it -
    /// it would overwrite the live lap time every car update.
    /// </summary>
    [TestMethod]
    public async Task NonResetData_DoesNotRestoreLapTimes()
    {
        // Establish the cars first, then send race information on its own with no reset.
        await _processor.ProcessAsync(Message(CachedRelayData(14, 13)), _sessionContext);
        _lapHistory.Invocations.Clear();

        await _processor.ProcessAsync(Message("$G,1,\"12\",15,\"01:15:47.872\""), _sessionContext);

        _lapHistory.Verify(x => x.GetLapsAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// The pre-existing mid-race reset case, where a heartbeat has already been seen, keeps working.
    /// </summary>
    [TestMethod]
    public async Task MidRaceReset_AfterHeartbeat_RestoresLastLapTimes()
    {
        await _processor.ProcessAsync(Message("$F,14,\"00:12:45\",\"13:34:23\",\"00:09:47\",\"Green \""), _sessionContext);

        await _processor.ProcessAsync(Message(CachedRelayData(14, 13)), _sessionContext);

        Assert.AreEqual("00:02:03.826", _sessionContext.GetCarByNumber("12")?.LastLapTime);
    }

    private static TimingMessage Message(string data) =>
        new(Backend.Shared.Consts.RMONITOR_TYPE, data, 5, DateTime.UtcNow);

    private static IDbContextFactory<TsContext> CreateDbContextFactory()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        return new TestDbContextFactory(optionsBuilder.Options);
    }
}
