namespace RedMist.EventProcessor.Tests.Utilities;

/// <summary>
/// A <see cref="TimeProvider"/> with a frozen clock whose timers fire as soon as they are armed.
/// </summary>
/// <remarks>
/// Lets a test drive code that awaits a fixed settle delay without depending on wall-clock time
/// and without the start-up race that <c>FakeTimeProvider.Advance</c> has when the timer under
/// test has not been registered yet. <c>Task.Delay(delay, timeProvider, token)</c> creates its timer
/// already armed and fires immediately here; a timer created with an infinite due time instead stays
/// idle until a later <c>Change</c> arms it.
///
/// Each timer fires at most once, whatever it is armed with afterwards. Firing on every
/// <c>Change</c> would turn a self-rescheduling timer - the usual shape of a polling loop - into an
/// unbounded thread-pool storm that starves the rest of the suite rather than failing.
///
/// Repeating timers are not supported. A non-infinite <c>period</c> is refused rather than silently
/// discarded, so code under test that wants a tick every interval fails instead of quietly running
/// once - see <see cref="MisuseException"/> for why it is recorded rather than thrown.
/// </remarks>
public sealed class ImmediateTimerTimeProvider(DateTimeOffset now) : TimeProvider
{
    private Exception? callbackException;
    private Exception? misuseException;

    /// <summary>
    /// The first exception thrown by a timer callback, if any. Callbacks run on the thread pool,
    /// where letting one escape would tear down the whole test host, so the exception is recorded
    /// here instead of being lost. A test whose timed work never completes should assert this is
    /// null - that is usually the reason.
    /// </summary>
    public Exception? CallbackException => Volatile.Read(ref callbackException);

    /// <summary>
    /// Set when this provider is asked for something it cannot model - currently only a repeating
    /// timer. Recorded rather than thrown because <see cref="CreateTimer"/> is called from
    /// constructors that are not exception-safe: throwing out of <c>PeriodicTimer</c>'s constructor,
    /// for instance, leaves a finalizable object with a null timer field, and the resulting
    /// NullReferenceException on the finalizer thread takes the whole test host down at some
    /// unrelated later GC. A timer created this way never fires, so the test that wanted the ticks
    /// fails on its own; this says why.
    /// </summary>
    public Exception? MisuseException => Volatile.Read(ref misuseException);

    public override DateTimeOffset GetUtcNow() => now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => new ImmediateTimer(this, callback, state, dueTime, period);

    private void Record(ref Exception? field, Exception ex, string label)
    {
        Interlocked.CompareExchange(ref field, ex, null);
        Console.Error.WriteLine($"{nameof(ImmediateTimerTimeProvider)} {label}: {ex}");
    }

    private void RecordCallbackFailure(Exception ex) => Record(ref callbackException, ex, "callback threw");

    private void RecordMisuse(TimeSpan period) => Record(ref misuseException, new NotSupportedException(
        $"{nameof(ImmediateTimerTimeProvider)} fires a timer once and cannot model a repeating period ({period}). "
        + "The code under test needs a clock that can tick, such as FakeTimeProvider. This timer will never fire."),
        "misused");

    private sealed class ImmediateTimer : ITimer
    {
        private readonly ImmediateTimerTimeProvider owner;
        private readonly TimerCallback callback;
        private readonly object? state;
        private int fired;

        public ImmediateTimer(ImmediateTimerTimeProvider owner, TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            this.owner = owner;
            this.callback = callback;
            this.state = state;
            Arm(dueTime, period);
        }

        /// <summary>
        /// Queues the callback rather than invoking it inline so the caller finishes wiring up first.
        /// Does nothing once the timer has already fired, or if a repeating period was asked for.
        /// </summary>
        private bool Arm(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
            {
                owner.RecordMisuse(period);
                return false;
            }

            // The infinite check comes first so disarming does not consume the one firing.
            if (dueTime == Timeout.InfiniteTimeSpan || Interlocked.Exchange(ref fired, 1) != 0)
            {
                return true;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    owner.RecordCallbackFailure(ex);
                }
            });
            return true;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => Arm(dueTime, period);

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
