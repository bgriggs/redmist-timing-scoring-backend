using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using RedMist.Database;
using RedMist.EventProcessor.Tests.Utilities;
using RedMist.StatusApi.Services;

namespace RedMist.TimingAndScoringService.Tests.StatusApi;

/// <summary>
/// Covers the sponsor telemetry buffer. Every impression, click and dwell measurement the UIs report
/// passes through this bounded channel, so what it drops under pressure and what it flushes on
/// shutdown determines whether sponsor billing numbers are right.
/// </summary>
[TestClass]
public class SponsorTelemetryQueueTests
{
    /// <summary>Mirrors SponsorTelemetryQueue.MAX_CHANNEL_SIZE, which is private.</summary>
    private const int MaxChannelSize = 5000;

    private IDbContextFactory<TsContext> _dbFactory = null!;
    private Mock<ILoggerFactory> _loggerFactory = null!;
    private Mock<ILogger> _logger = null!;

    [TestInitialize]
    public void Setup()
    {
        _logger = new Mock<ILogger>();
        _loggerFactory = new Mock<ILoggerFactory>();
        _loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(_logger.Object);

        var options = new DbContextOptionsBuilder<TsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbFactory = new TestDbContextFactory(options);
    }

    private SponsorTelemetryQueue CreateQueue(IDbContextFactory<TsContext>? factory = null) =>
        new(_loggerFactory.Object, factory ?? _dbFactory);

    private static SponsorTelemetryEntry Entry(string imageId, string eventType = "Impression", int? durationMs = null) =>
        new("landing-page", "297", imageId, eventType, durationMs);

    /// <summary>
    /// Waits for the flusher to reach <paramref name="expected"/> persisted rows. Polls a condition
    /// rather than sleeping a fixed interval, so the test is not tied to the 15 second flush timer.
    /// </summary>
    /// <remarks>
    /// The deadline is deliberately shorter than the tests' [Timeout(30_000)]: a flusher that never
    /// writes should fail on the row-count assertion, not be abandoned mid-run by the MSTest timeout.
    /// The reader wakes on the channel rather than the flush timer, so a healthy flush lands in
    /// milliseconds and this budget is never approached.
    /// </remarks>
    private async Task<int> WaitForRowsAsync(int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            using var db = _dbFactory.CreateDbContext();
            var count = await db.SponsorTelemetryLogs.CountAsync();
            if (count >= expected)
                return count;
            await Task.Delay(10);
        }

        using var final = _dbFactory.CreateDbContext();
        return await final.SponsorTelemetryLogs.CountAsync();
    }

    /// <summary>
    /// Entries buffered before the service starts must survive: the API accepts telemetry the moment
    /// it can bind a request, which is before the hosted service is guaranteed to be running.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task EntriesQueuedBeforeStart_AreFlushedToTheDatabase()
    {
        var queue = CreateQueue();
        Assert.IsTrue(queue.TryEnqueue(Entry("img-1")));
        Assert.IsTrue(queue.TryEnqueue(Entry("img-2")));

        await queue.StartAsync(CancellationToken.None);
        var count = await WaitForRowsAsync(2);
        await queue.StopAsync(CancellationToken.None);

        Assert.AreEqual(2, count);
    }

    /// <summary>
    /// Entries arriving once the flusher is already idle are picked up too. Deliberately does not
    /// assert how fast: the poll deadline is longer than the 15 second flush interval, so timing the
    /// wake-up here would only make the test flaky on a loaded agent.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task EntriesQueuedAfterStart_AreAlsoFlushed()
    {
        var queue = CreateQueue();
        await queue.StartAsync(CancellationToken.None);

        Assert.IsTrue(queue.TryEnqueue(Entry("img-late")));
        var count = await WaitForRowsAsync(1);
        await queue.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, count);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task FlushedRow_CarriesEverySubmittedField()
    {
        var queue = CreateQueue();
        queue.TryEnqueue(new SponsorTelemetryEntry("in-car-app", "297", "sponsor-42", "EngagementDuration", 4500));

        await queue.StartAsync(CancellationToken.None);
        await WaitForRowsAsync(1);
        await queue.StopAsync(CancellationToken.None);

        using var db = _dbFactory.CreateDbContext();
        var row = await db.SponsorTelemetryLogs.SingleAsync();
        Assert.AreEqual("in-car-app", row.Source);
        Assert.AreEqual("297", row.EventId);
        Assert.AreEqual("sponsor-42", row.ImageId);
        Assert.AreEqual("EngagementDuration", row.EventType);
        Assert.AreEqual(4500, row.DurationMs);
        Assert.AreNotEqual(default(DateTime), row.Timestamp);
    }

    /// <summary>A null duration is the normal case for impressions and clicks and must round-trip as null.</summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task FlushedRow_KeepsNullDurationForNonTimedEvents()
    {
        var queue = CreateQueue();
        queue.TryEnqueue(Entry("sponsor-1", "ClickThrough"));

        await queue.StartAsync(CancellationToken.None);
        await WaitForRowsAsync(1);
        await queue.StopAsync(CancellationToken.None);

        using var db = _dbFactory.CreateDbContext();
        Assert.IsNull((await db.SponsorTelemetryLogs.SingleAsync()).DurationMs);
    }

    /// <summary>
    /// The reader drains at most 50 entries per pass, so a backlog larger than one batch has to survive
    /// several passes without losing anything.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task BacklogLargerThanOneBatch_IsFlushedInFull()
    {
        var queue = CreateQueue();
        for (int i = 0; i < 120; i++)
            queue.TryEnqueue(Entry($"img-{i}"));

        await queue.StartAsync(CancellationToken.None);
        var count = await WaitForRowsAsync(120);
        await queue.StopAsync(CancellationToken.None);

        Assert.AreEqual(120, count);
        using var db = _dbFactory.CreateDbContext();
        Assert.AreEqual(120, await db.SponsorTelemetryLogs.Select(l => l.ImageId).Distinct().CountAsync());
    }

    /// <summary>
    /// The channel is DropOldest, so TryEnqueue never reports failure however far behind the flusher
    /// is - contrary to what its summary claims. Callers cannot use the return value to detect loss;
    /// the oldest telemetry is silently discarded instead.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task QueueingBeyondCapacity_ReturnsTrueButSilentlyDropsTheOldestEntries()
    {
        var queue = CreateQueue();
        const int overflow = 10;
        for (int i = 0; i < MaxChannelSize + overflow; i++)
            Assert.IsTrue(queue.TryEnqueue(Entry($"img-{i}")), $"TryEnqueue reported failure at entry {i}");

        await queue.StartAsync(CancellationToken.None);
        var count = await WaitForRowsAsync(MaxChannelSize);
        await queue.StopAsync(CancellationToken.None);

        Assert.AreEqual(MaxChannelSize, count);
        using var db = _dbFactory.CreateDbContext();
        var retained = await db.SponsorTelemetryLogs.Select(l => l.ImageId).ToListAsync();
        Assert.DoesNotContain("img-0", retained, "the oldest entries should have been dropped");
        Assert.DoesNotContain($"img-{overflow - 1}", retained);
        Assert.Contains($"img-{overflow}", retained, "the first surviving entry should be the one after the dropped run");
        Assert.Contains($"img-{MaxChannelSize + overflow - 1}", retained, "the newest entry should always survive");
    }

    /// <summary>
    /// A database outage must not take the API down with it. The failed batch is lost - the flusher
    /// clears it rather than retrying - but the service stays up and telemetry that arrives after the
    /// database recovers is still written.
    /// </summary>
    [TestMethod]
    [Timeout(120_000)]
    public async Task DatabaseFailureDuringFlush_LosesThatBatchButTheServiceKeepsRunning()
    {
        var factory = new FailOnceDbContextFactory(_dbFactory);
        var queue = CreateQueue(factory);
        queue.TryEnqueue(Entry("img-lost"));

        await queue.StartAsync(CancellationToken.None);
        await factory.FirstCallFailed.WaitAsync(TimeSpan.FromSeconds(30));

        // The database is healthy again from here on.
        queue.TryEnqueue(Entry("img-after-recovery"));
        await WaitForRowsAsync(1);
        await queue.StopAsync(CancellationToken.None);

        using var db = _dbFactory.CreateDbContext();
        var retained = await db.SponsorTelemetryLogs.Select(l => l.ImageId).ToListAsync();
        CollectionAssert.AreEqual(new[] { "img-after-recovery" }, retained);
        Assert.IsTrue(queue.ExecuteTask!.IsCompletedSuccessfully, "the flusher must not have faulted out of its loop");
        _logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Error flushing")),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.AtLeastOnce);
    }

    /// <summary>
    /// Shutdown arriving mid-flush: the batch already handed to the database is lost, but whatever is
    /// still buffered behind it is drained and written before the service exits. The gate makes the
    /// interleaving exact rather than relying on timing.
    /// </summary>
    [TestMethod]
    [Timeout(120_000)]
    public async Task ShutdownDuringAFlush_LosesTheInFlightBatchButStillDrainsWhatIsBuffered()
    {
        // Mirrors SponsorTelemetryQueue.BATCH_SIZE, which is private.
        const int batchSize = 50;
        const int remainder = 10;
        var gate = new GatedDbContextFactory(_dbFactory);
        var queue = CreateQueue(gate);
        for (int i = 0; i < batchSize + remainder; i++)
            queue.TryEnqueue(Entry($"img-{i}"));

        await queue.StartAsync(CancellationToken.None);
        // The flusher is now holding the first 50 entries and blocked inside the factory.
        await gate.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(30));

        // StopAsync cancels its token synchronously before it starts awaiting, so releasing the gate
        // afterwards guarantees the flusher observes shutdown on its next pass.
        var stop = queue.StopAsync(CancellationToken.None);
        gate.Release();
        await stop;

        using var db = _dbFactory.CreateDbContext();
        var retained = await db.SponsorTelemetryLogs.Select(l => l.ImageId).ToListAsync();
        Assert.AreEqual(remainder, retained.Count);
        CollectionAssert.AreEquivalent(
            Enumerable.Range(batchSize, remainder).Select(i => $"img-{i}").ToArray(),
            retained);
    }

    /// <summary>Stopping a queue that never received anything must not write a row or throw.</summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task EmptyQueue_StopsCleanlyWithoutWritingAnything()
    {
        var queue = CreateQueue();

        await queue.StartAsync(CancellationToken.None);
        await queue.StopAsync(CancellationToken.None);

        using var db = _dbFactory.CreateDbContext();
        Assert.AreEqual(0, await db.SponsorTelemetryLogs.CountAsync());
    }

    /// <summary>
    /// Fails the first context creation, then behaves normally. Note that only <see cref="CreateDbContext"/>
    /// needs to fail: <see cref="IDbContextFactory{TContext}.CreateDbContextAsync"/> is a default interface
    /// method that forwards to it.
    /// </summary>
    private sealed class FailOnceDbContextFactory(IDbContextFactory<TsContext> inner) : IDbContextFactory<TsContext>
    {
        // Asynchronous continuations: completing this inline would resume the test on the hosted
        // service's own thread, which then could not finish for StopAsync to await.
        private readonly TaskCompletionSource firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public Task FirstCallFailed => firstFailure.Task;

        public TsContext CreateDbContext()
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstFailure.TrySetResult();
                throw new InvalidOperationException("database unavailable");
            }
            return inner.CreateDbContext();
        }

        public ValueTask<TsContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }

    /// <summary>
    /// Holds the first flush open until the test releases it, then fails it. Later flushes go through
    /// to <paramref name="inner"/>. Lets a test pin down exactly where shutdown lands.
    /// </summary>
    /// <remarks>
    /// Blocking here is only safe because .NET 10's <c>BackgroundService.StartAsync</c> schedules
    /// <c>ExecuteAsync</c> on the thread pool. On a runtime that ran it inline, this would block the
    /// test's own thread inside StartAsync.
    /// </remarks>
    private sealed class GatedDbContextFactory(IDbContextFactory<TsContext> inner) : IDbContextFactory<TsContext>
    {
        private readonly TaskCompletionSource firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim released = new(false);
        private int calls;

        public Task FirstCallStarted => firstCall.Task;

        public void Release() => released.Set();

        public TsContext CreateDbContext()
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstCall.TrySetResult();
                // Bounded so a test that never reaches Release() fails instead of hanging the run.
                released.Wait(TimeSpan.FromSeconds(60));
                throw new InvalidOperationException("flush interrupted by shutdown");
            }
            return inner.CreateDbContext();
        }

        public ValueTask<TsContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}
