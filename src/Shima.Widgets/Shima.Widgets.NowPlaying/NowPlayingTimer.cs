namespace Shima.Widgets.NowPlaying;

/// <summary>
/// Periodic progress interpolation timer abstraction for Now Playing seek bar updates.
/// </summary>
public interface INowPlayingTimer : IDisposable
{
    bool IsRunning { get; }

    TimeSpan? LastInterval { get; }

    void Start(TimeSpan interval, Action onTick);

    void Stop();
}

/// <summary>
/// Production progress timer backed by <see cref="System.Threading.Timer"/> that stays completely stopped
/// unless the island is Expanded on Now Playing and actively playing.
/// </summary>
public sealed class SystemNowPlayingTimer : INowPlayingTimer
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

    public TimeSpan? LastInterval { get; private set; }

    public void Start(TimeSpan interval, Action onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        var safeInterval = interval < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : interval;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _callback = onTick;
            LastInterval = safeInterval;
            _running = true;

            if (_timer is null)
            {
                _timer = new Timer(OnTick, null, safeInterval, safeInterval);
            }
            else
            {
                _timer.Change(safeInterval, safeInterval);
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

    private void OnTick(object? state)
    {
        Action? cb;
        lock (_sync)
        {
            if (_disposed || !_running)
            {
                return;
            }

            cb = _callback;
        }

        cb?.Invoke();
    }
}
