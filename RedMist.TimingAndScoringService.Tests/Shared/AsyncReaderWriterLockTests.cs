using RedMist.Backend.Shared.Utilities;

namespace RedMist.EventProcessor.Tests.Shared;

/// <summary>
/// The lock guards the session state the whole event processor is built on, so these cover the
/// guarantees callers rely on rather than the implementation: that a writer runs alone, that readers
/// do not, that giving up on a wait leaves the lock usable, and that neither side starves.
/// </summary>
[TestClass]
public class AsyncReaderWriterLockTests
{
    /// <summary>How long a test waits for something that should happen almost immediately.</summary>
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    /// <summary>How long a test waits to satisfy itself that something is not going to happen.</summary>
    private static readonly TimeSpan NotHappening = TimeSpan.FromMilliseconds(200);

    [TestMethod]
    public async Task Reader_RunsConcurrentlyWithAnotherReader()
    {
        var rwLock = new AsyncReaderWriterLock();

        using var first = await rwLock.AcquireReadLockAsync(TestContext.CancellationToken);
        var second = rwLock.AcquireReadLockAsync(TestContext.CancellationToken);

        Assert.IsTrue(second.IsCompleted, "A second reader must not wait on the first");
        (await second).Dispose();
    }

    [TestMethod]
    public async Task Writer_WaitsForAnOutstandingReader()
    {
        var rwLock = new AsyncReaderWriterLock();

        var reader = await rwLock.AcquireReadLockAsync(TestContext.CancellationToken);
        var writer = rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);

        Assert.IsFalse(await CompletesAsync(writer, NotHappening), "The writer ran while a reader held the lock");

        reader.Dispose();
        Assert.IsTrue(await CompletesAsync(writer, Soon));
        (await writer).Dispose();
    }

    /// <summary>
    /// The guarantee the old implementation did not provide: it let a reader straight through while a
    /// writer was mid-update, so readers could see half-applied state.
    /// </summary>
    [TestMethod]
    public async Task Reader_WaitsForAnOutstandingWriter()
    {
        var rwLock = new AsyncReaderWriterLock();

        var writer = await rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
        var reader = rwLock.AcquireReadLockAsync(TestContext.CancellationToken);

        Assert.IsFalse(await CompletesAsync(reader, NotHappening), "The reader ran while a writer held the lock");

        writer.Dispose();
        Assert.IsTrue(await CompletesAsync(reader, Soon));
        (await reader).Dispose();
    }

    [TestMethod]
    public async Task Writer_WaitsForAnotherWriter()
    {
        var rwLock = new AsyncReaderWriterLock();

        var first = await rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
        var second = rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);

        Assert.IsFalse(await CompletesAsync(second, NotHappening));

        first.Dispose();
        Assert.IsTrue(await CompletesAsync(second, Soon));
        (await second).Dispose();
    }

    /// <summary>
    /// A reader arriving while a writer is queued waits behind it. Without this the pipeline, which
    /// is the writer, can be held off indefinitely by readers that keep arriving.
    /// </summary>
    [TestMethod]
    public async Task Reader_ArrivingWhileAWriterIsQueued_WaitsBehindIt()
    {
        var rwLock = new AsyncReaderWriterLock();

        var holdingReader = await rwLock.AcquireReadLockAsync(TestContext.CancellationToken);
        var queuedWriter = rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
        var laterReader = rwLock.AcquireReadLockAsync(TestContext.CancellationToken);

        holdingReader.Dispose();

        Assert.IsTrue(await CompletesAsync(queuedWriter, Soon), "The queued writer should go first");
        Assert.IsFalse(await CompletesAsync(laterReader, NotHappening), "The later reader overtook the writer");

        (await queuedWriter).Dispose();
        Assert.IsTrue(await CompletesAsync(laterReader, Soon));
        (await laterReader).Dispose();
    }

    [TestMethod]
    public async Task QueuedReaders_AreAllGrantedTogetherWhenTheWriterFinishes()
    {
        var rwLock = new AsyncReaderWriterLock();

        var writer = await rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
        var readers = Enumerable.Range(0, 5)
            .Select(_ => rwLock.AcquireReadLockAsync(TestContext.CancellationToken))
            .ToList();

        writer.Dispose();

        foreach (var reader in readers)
            Assert.IsTrue(await CompletesAsync(reader, Soon));
        foreach (var reader in readers)
            (await reader).Dispose();

        // The lock is free again, so a writer takes it without waiting.
        var next = rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
        Assert.IsTrue(await CompletesAsync(next, Soon));
        (await next).Dispose();
    }

    [TestMethod]
    public async Task CancellingAQueuedWriter_LeavesTheLockUsable()
    {
        var rwLock = new AsyncReaderWriterLock();
        using var cts = new CancellationTokenSource();

        var holder = await rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
        var abandoned = rwLock.AcquireWriteLockAsync(cts.Token);
        var next = rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);

        await cts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => abandoned);

        holder.Dispose();

        // The abandoned waiter was ahead in the queue; the lock must pass over it rather than be
        // handed to a waiter that is no longer there.
        Assert.IsTrue(await CompletesAsync(next, Soon));
        (await next).Dispose();
    }

    [TestMethod]
    public async Task CancellingAQueuedReader_LeavesTheLockUsable()
    {
        var rwLock = new AsyncReaderWriterLock();
        using var cts = new CancellationTokenSource();

        var holder = await rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
        var abandoned = rwLock.AcquireReadLockAsync(cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => abandoned);

        holder.Dispose();

        var writer = rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
        Assert.IsTrue(await CompletesAsync(writer, Soon), "The lock was left held on behalf of the cancelled reader");
        (await writer).Dispose();
    }

    [TestMethod]
    public async Task AlreadyCancelledToken_DoesNotTakeTheLock()
    {
        var rwLock = new AsyncReaderWriterLock();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => rwLock.AcquireWriteLockAsync(cts.Token));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => rwLock.AcquireReadLockAsync(cts.Token));

        var writer = rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
        Assert.IsTrue(await CompletesAsync(writer, Soon));
        (await writer).Dispose();
    }

    [TestMethod]
    public async Task DisposingTheSameHolderTwice_DoesNotReleaseTheLockTwice()
    {
        var rwLock = new AsyncReaderWriterLock();

        var firstReader = await rwLock.AcquireReadLockAsync(TestContext.CancellationToken);
        using var secondReader = await rwLock.AcquireReadLockAsync(TestContext.CancellationToken);

        firstReader.Dispose();
        firstReader.Dispose();

        // A double release would have dropped the count to zero and let the writer in while the
        // second reader was still holding the lock.
        var writer = rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
        Assert.IsFalse(await CompletesAsync(writer, NotHappening));

        secondReader.Dispose();
        Assert.IsTrue(await CompletesAsync(writer, Soon));
        (await writer).Dispose();
    }

    /// <summary>
    /// Readers and writers hammering the lock together. The pair of counters stands in for the
    /// session state: a writer that is not alone, or a reader that sees a write half-applied, shows
    /// up as the two disagreeing.
    ///
    /// Bounded by a number of writes rather than by elapsed time. A wall clock started before the
    /// tasks are on the thread pool can expire before they are scheduled at all, which on a loaded
    /// build agent leaves every loop exiting on its first check and the test asserting against work
    /// that never ran.
    /// </summary>
    [TestMethod]
    public async Task UnderContention_WritesAreAtomicAndNothingDeadlocks()
    {
        const int WritesPerWriter = 250;
        var rwLock = new AsyncReaderWriterLock();
        var pair = new int[2];
        int writes = 0, reads = 0, tornReads = 0;
        var stopReading = false;

        var writers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < WritesPerWriter; i++)
            {
                using (await rwLock.AcquireWriteLockAsync())
                {
                    // Deliberately not atomic: the lock is what makes the pair agree.
                    var next = pair[0] + 1;
                    pair[0] = next;
                    await Task.Yield();
                    pair[1] = next;
                    Interlocked.Increment(ref writes);
                }

                // Off the lock between writes, so readers are not permanently behind a queued writer.
                await Task.Yield();
            }
        })).ToList();

        var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            while (!Volatile.Read(ref stopReading))
            {
                using (await rwLock.AcquireReadLockAsync())
                {
                    var first = pair[0];
                    await Task.Yield();
                    if (pair[1] != first)
                        Interlocked.Increment(ref tornReads);
                    Interlocked.Increment(ref reads);
                }
            }
        })).ToList();

        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(60), TestContext.CancellationToken);
        // The lock is writer preferring, so a reader is only guaranteed a turn once no writer wants
        // it. Leaving the readers running alone for a moment makes their progress a property of the
        // lock rather than of who won a race.
        await Task.Delay(50, TestContext.CancellationToken);
        Volatile.Write(ref stopReading, true);
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(60), TestContext.CancellationToken);

        Assert.AreEqual(0, tornReads, "A reader saw a half-applied write");
        Assert.AreEqual(3 * WritesPerWriter, writes, "Writers did not all finish");
        Assert.AreEqual(writes, pair[1], "Writers were not exclusive of one another");
        Assert.IsGreaterThan(0, reads, "No reader ever got the lock");

        // Nothing was left holding the lock.
        var final = rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
        Assert.IsTrue(await CompletesAsync(final, Soon));
        (await final).Dispose();
    }

    /// <summary>
    /// Cancellation racing the hand-off, which is the case the two tests above cannot reach: they
    /// wait for the cancellation to land before releasing, so the waiter is already gone by the time
    /// the queues are drained. Here the waiter can instead be chosen and then give up before it is
    /// handed the lock, which has to leave the lock free rather than held on behalf of nobody.
    /// </summary>
    [TestMethod]
    public async Task CancellingWhileTheLockIsHandedOver_LeavesTheLockUsable()
    {
        for (int i = 0; i < 500; i++)
        {
            var rwLock = new AsyncReaderWriterLock();
            using var cts = new CancellationTokenSource();

            var holder = await rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
            var racingWriter = rwLock.AcquireWriteLockAsync(cts.Token);
            var racingReader = rwLock.AcquireReadLockAsync(cts.Token);

            var cancelling = Task.Run(cts.Cancel, TestContext.CancellationToken);
            holder.Dispose();
            await cancelling;

            // Either outcome is fine for any given waiter; what matters is where the lock ends up.
            foreach (var racer in new[] { racingWriter, racingReader })
            {
                try
                {
                    (await racer).Dispose();
                }
                catch (OperationCanceledException)
                {
                }
            }

            var probe = rwLock.AcquireWriteLockAsync(TestContext.CancellationToken);
            Assert.IsTrue(await CompletesAsync(probe, Soon), $"The lock was left held by nobody on iteration {i}");
            (await probe).Dispose();
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<bool> CompletesAsync(Task task, TimeSpan within)
    {
        return await Task.WhenAny(task, Task.Delay(within)) == task;
    }
}
