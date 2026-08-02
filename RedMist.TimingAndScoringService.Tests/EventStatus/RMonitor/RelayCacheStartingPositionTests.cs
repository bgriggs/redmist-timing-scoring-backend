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

namespace RedMist.EventProcessor.Tests.EventStatus.RMonitor;

/// <summary>
/// Positions gained and lost are the difference between a car's grid position and where it is now, so
/// they depend on the starting positions surviving a restart. The relay does cache the grid - it keeps
/// each car's lap 0 $G record and replays it ahead of the current ones - but the batch lists its $C
/// class records after the $A records that create the cars, so the cars are still class-less when
/// those grid records arrive. These cover rebuilding both the overall and the in-class grid from that
/// replay.
/// </summary>
[TestClass]
public class RelayCacheStartingPositionTests
{
    private RMonitorDataProcessor _processor = null!;
    private SessionContext _sessionContext = null!;

    /// <summary>
    /// What the relay replays from its cache, in its order: reset, competitors, run information,
    /// classes, then the cached lap 0 $G grid records ahead of the current $G race information.
    /// Cars 12 and 13 are in GT starting 3rd and 1st overall; car 14 is in TT starting 2nd.
    /// </summary>
    private const string CachedRelayData =
        "$I, \"00:00:00\", \"0/0/0000\"\n" +
        "$A,\"12\",\"12\",52474,\"John\",\"Doe\",\"USA\",1\n" +
        "$A,\"13\",\"13\",52475,\"Jane\",\"Roe\",\"USA\",1\n" +
        "$A,\"14\",\"14\",52476,\"Jim\",\"Poe\",\"USA\",2\n" +
        "$B,5,\"Race\"\n" +
        "$C,1,\"GT\"\n" +
        "$C,2,\"TT\"\n" +
        "$G,3,\"12\",,\"00:00:00.000\"\n" +
        "$G,1,\"13\",,\"00:00:00.000\"\n" +
        "$G,2,\"14\",,\"00:00:00.000\"\n" +
        "$G,1,\"12\",14,\"01:12:47.872\"\n" +
        "$G,2,\"13\",13,\"01:13:01.100\"\n" +
        "$G,3,\"14\",13,\"01:13:11.100\"\n";

    [TestInitialize]
    public void Setup()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "1" } })
            .Build();
        var dbContextFactory = CreateDbContextFactory();

        var lapHistory = new Mock<ICarLapHistoryService>();
        lapHistory.Setup(x => x.GetLapsAsync(It.IsAny<string>())).ReturnsAsync([]);

        _sessionContext = new SessionContext(config, dbContextFactory, loggerFactory.Object, lapHistory.Object);

        var resetProcessor = new ResetProcessor(_sessionContext, new Mock<IHubContext<StatusHub>>().Object, loggerFactory.Object);
        var startingPositionProcessor = new StartingPositionProcessor(_sessionContext, loggerFactory.Object, dbContextFactory);
        _processor = new RMonitorDataProcessor(loggerFactory.Object, _sessionContext, resetProcessor,
            startingPositionProcessor, new Mock<IMediator>().Object);
    }

    [TestMethod]
    public async Task CachedRelayData_OnRestart_RestoresOverallStartingPositions()
    {
        await _processor.ProcessAsync(Message(CachedRelayData), _sessionContext);

        Assert.AreEqual(3, _sessionContext.GetStartingPosition("12"));
        Assert.AreEqual(1, _sessionContext.GetStartingPosition("13"));
        Assert.AreEqual(2, _sessionContext.GetStartingPosition("14"));
    }

    /// <summary>
    /// The cars are still class-less when the grid records arrive, so the in-class ranks have to be
    /// rebuilt once the batch's $C records have been applied.
    /// </summary>
    [TestMethod]
    public async Task CachedRelayData_OnRestart_RestoresInClassStartingPositions()
    {
        await _processor.ProcessAsync(Message(CachedRelayData), _sessionContext);

        // GT: car 13 starts 1st overall, car 12 starts 3rd, so in class they are 1st and 2nd.
        Assert.AreEqual(1, _sessionContext.GetInClassStartingPosition("13"));
        Assert.AreEqual(2, _sessionContext.GetInClassStartingPosition("12"));
        // TT: car 14 is the only entry.
        Assert.AreEqual(1, _sessionContext.GetInClassStartingPosition("14"));
    }

    /// <summary>
    /// In-class ranks come from the grid order, not from where the cars are running now - the replay
    /// carries both, and the current order is the opposite of the grid here on purpose.
    /// </summary>
    [TestMethod]
    public async Task CachedRelayData_OnRestart_RanksInClassByGridNotCurrentPosition()
    {
        await _processor.ProcessAsync(Message(CachedRelayData), _sessionContext);

        // Car 12 leads GT on track (overall 1st vs 13's 2nd) but started behind it, so it must still
        // be 2nd on the in-class grid.
        Assert.AreEqual(1, _sessionContext.GetCarByNumber("12")?.OverallPosition);
        Assert.AreEqual(2, _sessionContext.GetInClassStartingPosition("12"));
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
