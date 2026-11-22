using Shima.Core.Contracts;

namespace Shima.Core.Shell;

/// <summary>
/// <see cref="System.Threading.Timer"/>-backed one-shot timer. Re-scheduling replaces the pending shot;
/// after firing (or cancelling) the underlying timer is disarmed so there is zero idle CPU.
/// </summary>
public sealed class OneShotTimer : IOneShotTimer
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
        var clamped = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var gen = ++_generation;
            _callback = callback;
            _isPending = true;

            _timer?.Dispose();
            _timer = new Timer(_ => OnElapsed(gen), null, clamped, Timeout.InfiniteTimeSpan);
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _generation++;
            _isPending = false;
            _callback = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        Cancel();
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private void OnElapsed(long gen)
    {
        Action? toInvoke;
        lock (_sync)
        {
            if (_disposed || !_isPending || _generation != gen)
            {
                return;
            }

            _isPending = false;
            toInvoke = _callback;
            _callback = null;
            _timer?.Dispose();
            _timer = null;
        }

        toInvoke?.Invoke();
    }
}
