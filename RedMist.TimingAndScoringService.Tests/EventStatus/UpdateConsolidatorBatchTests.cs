using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Hubs;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.EventStatus.PipelineBlocks;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using System.Collections.Immutable;

namespace RedMist.EventProcessor.Tests.EventStatus;

/// <summary>
/// Covers how a pipeline pass reaches clients. A pass produces a patch set per stage, and the
/// consolidator waits out a debounce interval on every call it is given, so the cost of publishing
/// has to depend on how many passes there were rather than on how many stages each one touched.
/// </summary>
[TestClass]
public class UpdateConsolidatorBatchTests
{
    private readonly UpdateConsolidator consolidator;
    private readonly Mock<IClientProxy> clientProxy = new();
    private int sendCalls;

    public UpdateConsolidatorBatchTests()
    {
        var mockLogger = new Mock<ILogger>();
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);

        var hubContext = new Mock<IHubContext<StatusHub>>();
        var clients = new Mock<IHubClients>();
        hubContext.Setup(x => x.Clients).Returns(clients.Object);
        clients.Setup(x => x.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        clientProxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => Interlocked.Increment(ref sendCalls));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "123" } })
            .Build();
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");

        var sessionContext = new SessionContext(configuration, new TestDbContextFactory(optionsBuilder.Options),
            mockLoggerFactory.Object, new Mock<ICarLapHistoryService>().Object);

        consolidator = new UpdateConsolidator(sessionContext, mockLoggerFactory.Object,
            new StatusAggregator(hubContext.Object, mockLoggerFactory.Object, sessionContext));
    }

    private static PatchUpdates CarPatch(string number, int position) =>
        new([], [new CarPositionPatch { Number = number, OverallPosition = position }]);

    [TestMethod]
    public async Task Process_APassWithManyStages_SendsThemAsOneUpdate()
    {
        var stages = Enumerable.Range(1, 10).Select(i => CarPatch(i.ToString(), i)).ToList();

        await consolidator.Process(stages);

        // One send carrying every car, rather than one send per stage.
        Assert.AreEqual(1, sendCalls);
    }

    [TestMethod]
    public async Task Process_APassWithManyStages_KeepsEveryStagesPatches()
    {
        IReadOnlyList<CarPositionPatch>? sent = null;
        clientProxy.Setup(x => x.SendCoreAsync("ReceiveCarPatches", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<string, object[], CancellationToken>((_, args, _) =>
                sent = (IReadOnlyList<CarPositionPatch>)args[0]);

        await consolidator.Process(Enumerable.Range(1, 10).Select(i => CarPatch(i.ToString(), i)).ToList());

        Assert.IsNotNull(sent);
        CollectionAssert.AreEquivalent(
            Enumerable.Range(1, 10).Select(i => i.ToString()).ToList(),
            sent!.Select(p => p.Number).ToList());
    }

    [TestMethod]
    public async Task Process_EmptyStages_PublishNothing()
    {
        await consolidator.Process(new List<PatchUpdates>
        {
            new(ImmutableList<SessionStatePatch>.Empty, ImmutableList<CarPositionPatch>.Empty),
            new(ImmutableList<SessionStatePatch>.Empty, ImmutableList<CarPositionPatch>.Empty),
        });

        Assert.AreEqual(0, sendCalls);
    }
}
