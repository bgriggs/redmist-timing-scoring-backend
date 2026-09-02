namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Bounds how many bytes of finished-but-undelivered export files this replica has on disk at once.
/// </summary>
/// <remarks>
/// <para>
/// The concurrency limiter releases its slot when a file has been generated, not when it has been
/// delivered - deliberately, so the cap measures work rather than the client's download speed. The
/// consequence is that finished files accumulate for as long as their readers take, and how many
/// there are is governed only by the rate limiter. That limiter partitions on the client IP taken
/// from <c>CF-Connecting-IP</c> / <c>X-Forwarded-For</c>, which are request headers: a caller who
/// varies them gets a fresh bucket every time. Add a reader that opens the download and then stalls,
/// and 100MB files pile up on the node's disk.
/// </para>
/// <para>
/// So admission is checked against what is already staged. This is deliberately not a reservation:
/// a file's size is not known until it has been written, so the check is "is there already too much
/// outstanding" rather than "will this fit". It can overshoot by one export per concurrent
/// generation, which the concurrency limiter already bounds, and that is enough to turn unbounded
/// accumulation into a ceiling.
/// </para>
/// </remarks>
public sealed class StagedExportTracker
{
    /// <summary>
    /// Bytes of undelivered exports past which new exports are refused. Worst case on disk is this
    /// plus one maximum-size file per concurrent generation slot.
    /// </summary>
    public const long DefaultMaxStagedBytes = 512L * 1024 * 1024;

    private long stagedBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="StagedExportTracker"/> class.
    /// </summary>
    /// <param name="maxStagedBytes">The ceiling. Tests lower it to force the refusal path.</param>
    public StagedExportTracker(long maxStagedBytes = DefaultMaxStagedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStagedBytes, 1);
        MaxStagedBytes = maxStagedBytes;
    }

    /// <summary>The ceiling this instance enforces.</summary>
    public long MaxStagedBytes { get; }

    /// <summary>Bytes of generated exports not yet released.</summary>
    public long StagedBytes => Interlocked.Read(ref stagedBytes);

    /// <summary>
    /// Whether another export may be generated. Checked before the work starts, so a caller that is
    /// refused has not paid for a file that then has nowhere to live.
    /// </summary>
    public bool HasCapacity => StagedBytes < MaxStagedBytes;

    /// <summary>
    /// Takes ownership of a finished export, counting its bytes until the returned stream is
    /// disposed.
    /// </summary>
    /// <param name="file">The delete-on-close handle over the generated file.</param>
    /// <returns>A stream to hand to the response; disposing it releases both the bytes and the file.</returns>
    public Stream Track(FileStream file) => new TrackedExportStream(file, this);

    private void Add(long bytes) => Interlocked.Add(ref stagedBytes, bytes);

    private void Remove(long bytes) => Interlocked.Add(ref stagedBytes, -bytes);

    /// <summary>
    /// A read-only pass-through over the staged file that decrements the tracker exactly once, when
    /// the response is done with it.
    /// </summary>
    /// <remarks>
    /// MVC's file result seeks, reads the length, and copies; nothing else is forwarded because
    /// nothing else is used. The count is taken at construction rather than read from the file on
    /// dispose, so a decrement can never disagree with its increment.
    /// </remarks>
    private sealed class TrackedExportStream : Stream
    {
        private readonly FileStream inner;
        private readonly StagedExportTracker tracker;
        private readonly long bytes;
        private int released;

        public TrackedExportStream(FileStream inner, StagedExportTracker tracker)
        {
            this.inner = inner;
            this.tracker = tracker;
            bytes = inner.Length;
            tracker.Add(bytes);
        }

        /// <summary>The staged file's path, for tests and diagnostics.</summary>
        public string Path => inner.Name;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            inner.CopyToAsync(destination, bufferSize, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseOnce();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            ReleaseOnce();
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        private void ReleaseOnce()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                tracker.Remove(bytes);
        }
    }
}
