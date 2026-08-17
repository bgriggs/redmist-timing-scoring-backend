using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.EventStatus;
using RedMist.EventProcessor.EventStatus.LapData;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.TimingCommon.Models;

namespace RedMist.EventProcessor.Tests.EventStatus;

/// <summary>
/// Covers the serialized snapshot the status endpoint is served from. The point of the snapshot is
/// that a large audience polling the endpoint interrupts the processing pipeline at a fixed rate
/// rather than once per poller, so these assert that repeat callers reuse one serialization.
/// </summary>
[TestClass]
public class SessionContextSnapshotTests
{
    private readonly SessionContext sessionContext;
    private readonly FakeTimeProvider fakeTimeProvider;

    public SessionContextSnapshotTests()
    {
        var mockLogger = new Mock<ILogger>();
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);

        fakeTimeProvider = new FakeTimeProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "event_id", "123" } })
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<TsContext>();
        optionsBuilder.UseInMemoryDatabase($"TestDatabase_{Guid.NewGuid()}");

        sessionContext = new SessionContext(configuration, new TestDbContextFactory(optionsBuilder.Options),
            mockLoggerFactory.Object, new Mock<ICarLapHistoryService>().Object, fakeTimeProvider);
    }

    [TestMethod]
    public async Task GetSerializedStateAsync_SerializesTheCurrentState()
    {
        sessionContext.SessionState.SessionName = "Race 1";
        sessionContext.SessionState.CarPositions.Add(new CarPosition { Number = "42", OverallPosition = 3 });

        var state = MessagePackSerializer.Deserialize<SessionState>(await sessionContext.GetSerializedStateAsync());

        Assert.AreEqual("Race 1", state.SessionName);
        Assert.AreEqual(1, state.CarPositions.Count);
        Assert.AreEqual("42", state.CarPositions[0].Number);
    }

    [TestMethod]
    public async Task GetSerializedStateAsync_WithinMaxAge_ReusesTheSameSnapshot()
    {
        var first = await sessionContext.GetSerializedStateAsync();

        // A change the second caller must not see: it proves the bytes were reused rather than
        // re-serialized, which is what keeps the pipeline off the per-request path.
        sessionContext.SessionState.SessionName = "Changed";
        fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(50));

        var second = await sessionContext.GetSerializedStateAsync();

        Assert.AreSame(first, second);
    }

    [TestMethod]
    public async Task GetSerializedStateAsync_PastMaxAge_TakesAFreshSnapshot()
    {
        var first = await sessionContext.GetSerializedStateAsync();

        sessionContext.SessionState.SessionName = "Changed";
        fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(150));

        var second = await sessionContext.GetSerializedStateAsync();

        Assert.AreNotSame(first, second);
        Assert.AreEqual("Changed", MessagePackSerializer.Deserialize<SessionState>(second).SessionName);
    }

    [TestMethod]
    public async Task GetSerializedStateAsync_ConcurrentCallers_ShareOneSerialization()
    {
        // Held so every caller below queues behind it, which is the window the coalescing covers.
        var writer = await sessionContext.SessionStateLock.AcquireWriteLockAsync(TestContext.CancellationToken);

        var callers = Enumerable.Range(0, 25)
            .Select(_ => sessionContext.GetSerializedStateAsync())
            .ToList();

        writer.Dispose();
        var results = await Task.WhenAll(callers);

        foreach (var result in results)
            Assert.AreSame(results[0], result);
    }

    [TestMethod]
    public async Task GetSerializedStateAsync_AfterAReset_DoesNotServeThePreviousFieldFromCache()
    {
        sessionContext.SessionState.CarPositions.Add(new CarPosition { Number = "42" });
        await sessionContext.GetSerializedStateAsync();

        // Well inside the freshness window, so only the reset itself can drop the cached copy.
        fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(10));
        sessionContext.ResetCommand();

        var state = MessagePackSerializer.Deserialize<SessionState>(await sessionContext.GetSerializedStateAsync());
        Assert.AreEqual(0, state.CarPositions.Count);
    }

    [TestMethod]
    public async Task GetSerializedStateAsync_SnapshotTakenBeforeAReset_ServesItsCallerButNotTheNextOne()
    {
        sessionContext.SessionState.CarPositions.Add(new CarPosition { Number = "42" });

        // Parked behind the writer, as a snapshot competing with the pipeline is.
        var writer = await sessionContext.SessionStateLock.AcquireWriteLockAsync(TestContext.CancellationToken);
        var inFlight = sessionContext.GetSerializedStateAsync();
        writer.Dispose();

        // Its own caller asked before the reset and gets the field it read, which is what the
        // per-request serialization this replaced would also have returned.
        var served = MessagePackSerializer.Deserialize<SessionState>(await inFlight);
        Assert.AreEqual(1, served.CarPositions.Count);

        sessionContext.ResetCommand();
        fakeTimeProvider.Advance(TimeSpan.FromMilliseconds(10));

        var next = MessagePackSerializer.Deserialize<SessionState>(await sessionContext.GetSerializedStateAsync());
        Assert.AreEqual(0, next.CarPositions.Count);
    }

    [TestMethod]
    public async Task GetSerializedStateAsync_AfterAFailedAttempt_RetriesRatherThanCachingTheFailure()
    {
        // A cancelled context makes the read lock acquisition fail, standing in for any fault
        // during the snapshot.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        sessionContext.CancellationToken = cts.Token;

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => sessionContext.GetSerializedStateAsync());

        sessionContext.CancellationToken = CancellationToken.None;
        sessionContext.SessionState.SessionName = "Recovered";

        var state = MessagePackSerializer.Deserialize<SessionState>(await sessionContext.GetSerializedStateAsync());
        Assert.AreEqual("Recovered", state.SessionName);
    }

    public TestContext TestContext { get; set; } = null!;
}
