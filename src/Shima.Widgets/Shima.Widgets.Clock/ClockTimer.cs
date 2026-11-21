namespace Shima.Widgets.Clock;

/// <summary>
/// Boundary-aligned one-shot timer abstraction enabling deterministic Clock timer suspension tests.
/// </summary>
public interface IClockTimer : IDisposable
{
    bool IsRunning { get; }

    TimeSpan? LastScheduledDelay { get; }

    void Schedule(TimeSpan delay, Action onTick);

    void Stop();
}

/// <summary>
/// Production timer backed by <see cref="System.Threading.Timer"/> using Infinite period between explicit reschedules.
/// </summary>
public sealed class SystemClockTimer : IClockTimer
{
    private readonly object _sync = new();
    private Timer? _timer;
    private Action? _callback;
    private bool _running;
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _running && !_disposed;
            }
        }
    }

    public TimeSpan? LastScheduledDelay { get; private set; }

    public void Schedule(TimeSpan delay, Action onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        var safeDelay = delay < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : delay;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _callback = onTick;
            LastScheduledDelay = safeDelay;
            _running = true;

            if (_timer is null)
            {
                _timer = new Timer(OnTimerElapsed, null, safeDelay, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _timer.Change(safeDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _running = false;
            _callback = null;
            if (!_disposed && _timer is not null)
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnTimerElapsed(object? state)
    {
        Action? callback;
        lock (_sync)
        {
            if (_disposed || !_running)
            {
                return;
            }

            _running = false;
            callback = _callback;
        }

        callback?.Invoke();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
            _callback = null;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
