namespace RedMist.EventProcessor.Tests.Utilities;

/// <summary>
/// A <see cref="TimeProvider"/> with a frozen clock whose timers fire as soon as they are created.
/// </summary>
/// <remarks>
/// Lets a test drive code that awaits a fixed settle delay without depending on wall-clock time
/// and without the start-up race that <c>FakeTimeProvider.Advance</c> has when the timer under
/// test has not been registered yet. A timer created with an infinite due time stays idle until
/// it is armed by <c>Change</c>; <c>period</c> is ignored, so a periodic timer fires only once.
/// </remarks>
public sealed class ImmediateTimerTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => new ImmediateTimer(callback, state, dueTime);

    private sealed class ImmediateTimer : ITimer
    {
        private readonly TimerCallback callback;
        private readonly object? state;

        public ImmediateTimer(TimerCallback callback, object? state, TimeSpan dueTime)
        {
            this.callback = callback;
            this.state = state;
            Arm(dueTime);
        }

        /// <summary>Queued rather than invoked inline so the caller finishes wiring up first.</summary>
        private void Arm(TimeSpan dueTime)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    // A throwing timer callback on a pool thread would tear down the whole test host.
                    Console.Error.WriteLine($"ImmediateTimer callback threw: {ex}");
                }
            });
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Arm(dueTime);
            return true;
        }

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
