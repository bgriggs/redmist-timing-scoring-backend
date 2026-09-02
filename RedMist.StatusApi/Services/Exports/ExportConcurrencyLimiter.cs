namespace RedMist.StatusApi.Services.Exports;

/// <summary>
/// Caps how many export files this replica will build at once.
/// </summary>
/// <remarks>
/// <para>
/// The rate limiter in front of the controller bounds how often a single caller may ask, but it
/// says nothing about how many callers are asking at the same time. Building an export walks a
/// whole session's lap rows and, for PDF, lays out a document - both of which are far heavier than
/// anything else this process does. This pod shares its memory limit with the SignalR hub that
/// feeds every live viewer, so a handful of simultaneous exports is the difference between a slow
/// download and an OOM kill that drops every connected client.
/// </para>
/// <para>
/// Waiting is bounded on purpose. A queue of exports that all eventually run is the same problem
/// deferred; telling the third caller to come back is the only answer that keeps the pod's working
/// set flat.
/// </para>
/// </remarks>
public sealed class ExportConcurrencyLimiter : IDisposable
{
    /// <summary>How many exports may be generated at once on this replica.</summary>
    public const int DefaultMaxConcurrentExports = 2;

    /// <summary>How long a request will wait for a slot before it is turned away.</summary>
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How many PDF renders may run at once, regardless of how many general slots are free.
    /// </summary>
    /// <remarks>
    /// Memory is not what binds here, CPU is. This pod is limited to 300m - under a third of one
    /// core - and a 2,000 row report measures at roughly 740ms of unthrottled CPU plus ~55MB of
    /// native Skia arenas outside the managed heap. At 300m that 740ms is several seconds of the
    /// pod's entire quota, and the thing being starved is the SignalR hub the pod exists to run. Two
    /// at once would double it, so PDF gets a lane of one while JSON and CSV - which are I/O bound
    /// and barely touch the CPU - keep the wider limit.
    /// </remarks>
    public const int DefaultMaxConcurrentPdfRenders = 1;

    /// <summary>The <c>Retry-After</c> value, in seconds, sent with the 503 when no slot came free.</summary>
    public const int RetryAfterSeconds = 5;

    private readonly SemaphoreSlim gate;
    private readonly SemaphoreSlim pdfGate;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportConcurrencyLimiter"/> class.
    /// </summary>
    /// <param name="maxConcurrentExports">Number of concurrent exports to allow. Tests use this to force saturation.</param>
    /// <param name="waitTimeout">How long a request waits for a slot. Tests shorten this so saturation is not a three second pause.</param>
    /// <param name="maxConcurrentPdfRenders">PDF render slots, taken in addition to a general slot.</param>
    public ExportConcurrencyLimiter(int maxConcurrentExports = DefaultMaxConcurrentExports, TimeSpan? waitTimeout = null,
        int maxConcurrentPdfRenders = DefaultMaxConcurrentPdfRenders)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentExports, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentPdfRenders, 1);
        MaxConcurrentExports = maxConcurrentExports;
        MaxConcurrentPdfRenders = maxConcurrentPdfRenders;
        WaitTimeout = waitTimeout ?? DefaultWaitTimeout;
        gate = new SemaphoreSlim(maxConcurrentExports, maxConcurrentExports);
        pdfGate = new SemaphoreSlim(maxConcurrentPdfRenders, maxConcurrentPdfRenders);
    }

    /// <summary>How many exports this instance allows at once.</summary>
    public int MaxConcurrentExports { get; }

    /// <summary>How many PDF renders this instance allows at once.</summary>
    public int MaxConcurrentPdfRenders { get; }

    /// <summary>How long <see cref="TryAcquireAsync(CancellationToken)"/> waits before giving up.</summary>
    public TimeSpan WaitTimeout { get; }

    /// <summary>Slots currently free. Exposed for diagnostics and tests.</summary>
    public int AvailableSlots => gate.CurrentCount;

    /// <summary>
    /// Tries to take a slot, waiting no longer than <see cref="WaitTimeout"/>.
    /// </summary>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>
    /// A lease that must be disposed to return the slot, or null when no slot came free in time.
    /// </returns>
    public Task<IDisposable?> TryAcquireAsync(CancellationToken cancellationToken = default) =>
        TryAcquireAsync(WaitTimeout, cancellationToken);

    /// <summary>
    /// Tries to take the slots an export of this format needs: a general slot always, plus a PDF
    /// render slot for a PDF.
    /// </summary>
    /// <remarks>
    /// The general slot is always taken first. A consistent order is what stops two exports of
    /// different formats from each holding the slot the other is waiting for.
    /// </remarks>
    /// <param name="format">The format about to be generated.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>
    /// A lease that returns every slot it took, or null when the wait ran out - in which case
    /// nothing is held.
    /// </returns>
    public async Task<IDisposable?> TryAcquireAsync(ExportFormat format, CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(WaitTimeout, cancellationToken))
            return null;

        var lease = new Lease(gate);
        if (format != ExportFormat.Pdf)
            return lease;

        try
        {
            if (!await pdfGate.WaitAsync(WaitTimeout, cancellationToken))
            {
                lease.Dispose();
                return null;
            }
        }
        catch
        {
            // A cancellation between the two waits must not strand the general slot.
            lease.Dispose();
            throw;
        }

        return new Lease(gate, pdfGate);
    }

    /// <summary>
    /// Tries to take a slot, waiting no longer than <paramref name="timeout"/>.
    /// </summary>
    /// <param name="timeout">How long to wait for a slot.</param>
    /// <param name="cancellationToken">Canceled when the client disconnects.</param>
    /// <returns>
    /// A lease that must be disposed to return the slot, or null when no slot came free in time.
    /// </returns>
    public async Task<IDisposable?> TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(timeout, cancellationToken))
            return null;
        return new Lease(gate);
    }

    /// <summary>PDF render slots currently free. Exposed for diagnostics and tests.</summary>
    public int AvailablePdfSlots => pdfGate.CurrentCount;

    /// <inheritdoc />
    public void Dispose()
    {
        gate.Dispose();
        pdfGate.Dispose();
    }

    /// <summary>
    /// Returns the slot exactly once, however the export ended. A double release would raise the
    /// semaphore's ceiling for the life of the process, which is a leak that only shows up later
    /// as unexplained memory pressure, so the flag is not optional bookkeeping.
    /// </summary>
    private sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim gate;
        private readonly SemaphoreSlim? pdfGate;
        private int released;

        public Lease(SemaphoreSlim gate, SemaphoreSlim? pdfGate = null)
        {
            this.gate = gate;
            this.pdfGate = pdfGate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
                return;

            // Released in the opposite order to acquisition, and the general slot last, so a waiter
            // that wakes on it finds the PDF lane already free.
            pdfGate?.Release();
            gate.Release();
        }
    }
}
