namespace RedMist.Backend.Shared.Utilities;

public class AsyncReaderWriterLock(int maxReaders = int.MaxValue)
{
    private readonly SemaphoreSlim _readSemaphore = new(maxReaders, maxReaders);
    private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
    private long _readerCount = 0;


    public async Task<IDisposable> AcquireReadLockAsync(CancellationToken cancellationToken = default)
    {
        await _readSemaphore.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _readerCount);
        return new ReaderLockReleaser(this);
    }


    public async Task<IDisposable> AcquireWriteLockAsync(CancellationToken cancellationToken = default)
    {
        await _writeSemaphore.WaitAsync(cancellationToken);

        // Wait for all readers to finish
        while (Interlocked.Read(ref _readerCount) > 0)
        {
            await Task.Delay(1, cancellationToken);
        }

        return new WriterLockReleaser(this);
    }

    private class ReaderLockReleaser(AsyncReaderWriterLock lockObj) : IDisposable
    {
        private bool _disposed = false;

        public void Dispose()
        {
            if (!_disposed)
            {
                Interlocked.Decrement(ref lockObj._readerCount);
                lockObj._readSemaphore.Release();
                _disposed = true;
            }
        }
    }

    private class WriterLockReleaser(AsyncReaderWriterLock lockObj) : IDisposable
    {
        private bool _disposed = false;

        public void Dispose()
        {
            if (!_disposed)
            {
                lockObj._writeSemaphore.Release();
                _disposed = true;
            }
        }
    }
}
