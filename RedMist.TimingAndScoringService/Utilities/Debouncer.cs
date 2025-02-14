namespace RedMist.TimingAndScoringService.Utilities;

public class Debouncer(TimeSpan delay)
{
    private DateTime _lastCallTime;
    private bool _isWaiting;
    private readonly Lock _lock = new();
    public bool IsDisabled { get; set; }

    public async Task ExecuteAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        if (IsDisabled) return;
        lock (_lock)
        {
            if (_isWaiting) return;
            _isWaiting = true;
            _lastCallTime = DateTime.UtcNow;
        }

        await Task.Delay(delay, cancellationToken);

        lock (_lock)
        {
            _isWaiting = false;
        }

        await action();
    }
}
