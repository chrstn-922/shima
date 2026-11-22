namespace Shima.Widgets.Notes;

public interface INotesDebounceTimer : IDisposable
{
    bool IsPending { get; }

    void Schedule(TimeSpan delay, Action callback);

    void Cancel();
}

/// <summary>
/// Single-shot debounce timer backed by System.Threading.Timer with zero idle polling ticks.
/// </summary>
internal sealed class SystemNotesDebounceTimer : INotesDebounceTimer
{
    private readonly object _sync = new();
    private Timer? _timer;
    private Action? _callback;
    private long _generation;
    private bool _isPending;
    private bool _disposed;

    public bool IsPending
    {
        get
        {
            lock (_sync)
            {
                return _isPending;
            }
        }
    }

    public void Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var clampedDelay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        long gen;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            gen = ++_generation;
            _callback = callback;
            _isPending = true;

            _timer ??= new Timer(OnTimerCallback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(clampedDelay, Timeout.InfiniteTimeSpan);
        }

        void OnTimerCallback(object? _)
        {
            Action? toInvoke = null;
            lock (_sync)
            {
                if (_disposed || !_isPending || _generation != gen)
                {
                    return;
                }

                _isPending = false;
                toInvoke = _callback;
                _callback = null;
            }

            toInvoke?.Invoke();
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _generation++;
            _isPending = false;
            _callback = null;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        Timer? toDispose;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            _isPending = false;
            _callback = null;
            toDispose = _timer;
            _timer = null;
        }

        toDispose?.Dispose();
    }
}
