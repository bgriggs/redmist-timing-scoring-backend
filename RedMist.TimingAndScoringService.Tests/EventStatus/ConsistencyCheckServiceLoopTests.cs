using BigMission.TestHelpers.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Backend.Shared.Services;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;
using System.Collections.Immutable;

namespace RedMist.EventProcessor.Tests.EventStatus;

/// <summary>
/// The consistency check is the last line of defense against the field on screen drifting from the
/// timing system: it is the only thing that asks the relay to resend. ConsistencyCheckServiceTests
/// covers the check itself - these cover the loop that has to keep running it, and the snapshot the
/// check runs against.
/// </summary>
[TestClass]
public class ConsistencyCheckServiceLoopTests
{
    /// <summary>
    /// The check compares car numbers, transponders and positions across the whole field and takes
    /// its time doing it. It runs on a copy so the pipeline is not held off for the duration, and
    /// the copy has to be deep - a shallow one would be re-read as the pipeline mutates the cars.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task GetCars_TakesADeepCopySoTheCheckCannotSeeTheFieldChangeUnderIt()
    {
        var (service, sessionContext) = CreateService();
        sessionContext.UpdateCars([new CarPosition { Number = "1", TransponderId = 1001, OverallPosition = 1 }]);

        var snapshot = await service.CallGetCars(TestContext.CancellationToken);

        Assert.HasCount(1, snapshot);
        snapshot[0].OverallPosition = 99;
        Assert.AreEqual(1, sessionContext.GetCarByNumber("1")!.OverallPosition,
            "The check must not be able to write back into the live session state.");

        sessionContext.GetCarByNumber("1")!.OverallPosition = 42;
        Assert.AreEqual(99, snapshot[0].OverallPosition, "The snapshot must not follow the live state.");
    }

    /// <summary>
    /// A check that throws must not end the loop - the service runs for the life of the event and
    /// nothing restarts it.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Loop_WhenACheckThrows_KeepsChecking()
    {
        var (service, _) = CreateService();
        service.FailOnPass = 1;
        service.StopAfterPasses = 3;

        await RunLoopAsync(service);

        Assert.AreEqual(3, service.Passes, "The loop should have carried on past the failed check.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Loop_KeepsCheckingUntilItIsStopped()
    {
        var (service, _) = CreateService();
        service.StopAfterPasses = 4;

        await RunLoopAsync(service);

        Assert.AreEqual(4, service.Passes);
    }

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Shutting the loop down always lands inside one of its waits, so the cancellation surfacing
    /// as a canceled task is the expected way for it to end.
    /// </summary>
    private static async Task RunLoopAsync(ProbeConsistencyCheckService service)
    {
        await service.StartAsync(service.Stopping.Token);
        try
        {
            await service.ExecuteTask!;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Counts the loop's passes and stops it from inside the check, so the loop is driven by its own
    /// progress rather than by elapsed time.
    /// </summary>
    private sealed class ProbeConsistencyCheckService : ConsistencyCheckService
    {
        public ProbeConsistencyCheckService(ILoggerFactory loggerFactory, SessionContext sessionContext,
            IMediator mediator, ConsistencyCheckOptions options)
            : base(loggerFactory, sessionContext, mediator, TimeProvider.System, null, options) { }

        public CancellationTokenSource Stopping { get; } = new();
        public int Passes { get; private set; }
        public int StopAfterPasses { get; set; } = int.MaxValue;
        public int? FailOnPass { get; set; }

        public Task<ImmutableList<CarPosition>> CallGetCars(CancellationToken token) => base.GetCarsAsync(token);

        protected override async Task<ImmutableList<CarPosition>> GetCarsAsync(CancellationToken stoppingToken)
        {
            Passes++;
            if (Passes >= StopAfterPasses)
                await Stopping.CancelAsync();
            if (Passes == FailOnPass)
                throw new InvalidOperationException("the session state could not be read");
            return await base.GetCarsAsync(stoppingToken);
        }
    }

    private static (ProbeConsistencyCheckService Service, SessionContext Context) CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "123" } })
            .Build();
        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");
        var loggerFactory = new DebugLoggerFactory();
        var sessionContext = new SessionContext(configuration, new TestDbContextFactory(optionsBuilder.Options),
            loggerFactory, new Mock<ICarLapHistoryService>().Object);

        var options = new ConsistencyCheckOptions
        {
            MainLoopInterval = TimeSpan.Zero,
            ErrorThrottleDelay = TimeSpan.Zero,
            ConsistencyErrorRecheckInterval = TimeSpan.Zero,
            MaxConsistencyErrorsBeforeRelayReset = 1,
        };
        return (new ProbeConsistencyCheckService(loggerFactory, sessionContext, new Mock<IMediator>().Object, options), sessionContext);
    }
}
