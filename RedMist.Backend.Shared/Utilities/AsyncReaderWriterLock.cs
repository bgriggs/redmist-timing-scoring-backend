namespace RedMist.Backend.Shared.Utilities;

/// <summary>
/// Async reader/writer lock. Readers run concurrently with each other; a writer runs alone, excluding
/// both readers and other writers.
///
/// Writer preferring: a reader arriving while a writer is queued waits behind it. The writer here is
/// the pipeline applying timing data, and readers - the status endpoint, the consistency check, the
/// session monitor's finish check - arrive on their own schedules, so first-come ordering would let a
/// steady trickle of readers hold the pipeline off.
///
/// Not reentrant. Taking either lock while already holding one deadlocks; a caller that is already
/// inside the lock has to work against the state directly instead of locking again.
///
/// Await the returned task, do not abandon it - a WaitAsync or WhenAny that walks away from a grant
/// still in flight leaves the lock held by a holder nobody will ever dispose. Give up by cancelling
/// the token instead.
/// </summary>
public sealed class AsyncReaderWriterLock
{
    private readonly Lock gate = new();

    /// <summary>-1 while a writer holds the lock, otherwise the number of readers holding it.</summary>
    private int state;

    private readonly Queue<Waiter> waitingWriters = [];
    private readonly List<Waiter> waitingReaders = [];


    public Task<IDisposable> AcquireReadLockAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<IDisposable>(cancellationToken);

        lock (gate)
        {
            // Queuing behind a waiting writer rather than joining the readers already running is what
            // keeps writers from starving. Queuing behind waiting readers as well stops a reader that
            // arrives mid hand-off from overtaking the ones already in line.
            if (state >= 0 && waitingWriters.Count == 0 && waitingReaders.Count == 0)
            {
                state++;
                return Task.FromResult<IDisposable>(new Releaser(this, isWriter: false));
            }

            var waiter = new Waiter(this, isWriter: false, cancellationToken);
            waitingReaders.Add(waiter);
            return waiter.Task;
        }
    }

    public Task<IDisposable> AcquireWriteLockAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<IDisposable>(cancellationToken);

        lock (gate)
        {
            if (state == 0)
            {
                state = -1;
                return Task.FromResult<IDisposable>(new Releaser(this, isWriter: true));
            }

            var waiter = new Waiter(this, isWriter: true, cancellationToken);
            waitingWriters.Enqueue(waiter);
            return waiter.Task;
        }
    }

    private void Release(bool isWriter)
    {
        lock (gate)
        {
            if (isWriter)
                state = 0;
            else
                state--;
        }

        GrantWaiters();
    }

    /// <summary>
    /// Hands the lock to whoever is next in line, if it is free. Loops because a waiter can be
    /// cancelled between being chosen and being handed the lock, which would otherwise leave the
    /// lock recorded as held by someone who never took it.
    /// </summary>
    private void GrantWaiters()
    {
        while (true)
        {
            Waiter? writer = null;
            List<Waiter>? readers = null;

            lock (gate)
            {
                if (state != 0)
                    return;

                // Drop waiters that gave up while queued rather than handing the lock to one.
                while (waitingWriters.Count > 0 && waitingWriters.Peek().HasGivenUp)
                    waitingWriters.Dequeue();
                waitingReaders.RemoveAll(w => w.HasGivenUp);

                if (waitingWriters.Count > 0)
                {
                    writer = waitingWriters.Dequeue();
                    state = -1;
                }
                else if (waitingReaders.Count > 0)
                {
                    readers = [.. waitingReaders];
                    waitingReaders.Clear();
                    state = readers.Count;
                }
                else
                {
                    return;
                }
            }

            if (writer != null)
            {
                if (writer.TryGrant())
                    return;

                // It gave up in the moment between being chosen and being handed the lock, so the
                // lock is held by nobody. Take it back and offer it to the next in line.
                lock (gate)
                {
                    state = 0;
                }
                continue;
            }

            // Readers are granted as a group. Any that gave up never took their share of the count,
            // so hand it back - and if that leaves the lock free, go round again for the next waiter.
            int notTaken = 0;
            foreach (var reader in readers!)
            {
                if (!reader.TryGrant())
                    notTaken++;
            }

            if (notTaken == 0)
                return;

            bool free;
            lock (gate)
            {
                state -= notTaken;
                free = state == 0;
            }

            if (!free)
                return;
        }
    }

    private sealed class Waiter
    {
        private readonly TaskCompletionSource<IDisposable> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AsyncReaderWriterLock owner;
        private readonly bool isWriter;
        private readonly CancellationToken cancellationToken;
        private readonly CancellationTokenRegistration registration;

        /// <summary>
        /// Constructed while the owner holds its gate, so the cancellation callback - which fires
        /// inline here when the token has already been cancelled - must not take the gate itself.
        /// Completing the waiter is enough: it is skipped the next time the queues are drained.
        /// </summary>
        public Waiter(AsyncReaderWriterLock owner, bool isWriter, CancellationToken cancellationToken)
        {
            this.owner = owner;
            this.isWriter = isWriter;
            if (cancellationToken.CanBeCanceled)
            {
                // Carries the token onto the exception so a caller can tell whose cancellation it was.
                this.cancellationToken = cancellationToken;
                registration = cancellationToken.Register(
                    static s =>
                    {
                        var w = (Waiter)s!;
                        w.completion.TrySetCanceled(w.cancellationToken);
                    }, this);
            }
        }

        public Task<IDisposable> Task => completion.Task;

        /// <summary>Whether this waiter gave up, so the lock must not be handed to it.</summary>
        public bool HasGivenUp => completion.Task.IsCompleted;

        /// <summary>Hands the lock over. False when the waiter had already given up.</summary>
        public bool TryGrant()
        {
            if (!completion.TrySetResult(new Releaser(owner, isWriter)))
                return false;

            registration.Dispose();
            return true;
        }
    }

    private sealed class Releaser(AsyncReaderWriterLock owner, bool isWriter) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.Release(isWriter);
        }
    }
}
