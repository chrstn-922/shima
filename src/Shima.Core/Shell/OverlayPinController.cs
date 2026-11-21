using Shima.Core.Contracts;

namespace Shima.Core.Shell;

/// <summary>
/// Thread-safe reference-counted pin controller ensuring Color Picker live preview
/// and critical modal workflows keep the island visible without permanently altering auto-collapse.
/// </summary>
public sealed class OverlayPinController : IOverlayPinController
{
    private readonly object _sync = new();
    private readonly List<PinEntry> _pins = [];

    public bool IsPinned
    {
        get
        {
            lock (_sync)
            {
                return _pins.Count > 0;
            }
        }
    }

    public PreviewTargetState? ActiveTargetState
    {
        get
        {
            lock (_sync)
            {
                return _pins.Count > 0 ? _pins[^1].TargetState : null;
            }
        }
    }

    public event EventHandler? PinStateChanged;

    public PinToken AcquirePin(PinReason reason, PreviewTargetState targetState)
    {
        var token = new PinToken(Guid.NewGuid(), reason, targetState);

        lock (_sync)
        {
            _pins.Add(new PinEntry(token.Id, reason, targetState));
        }

        PinStateChanged?.Invoke(this, EventArgs.Empty);
        return token;
    }

    public void UpdatePinTargetState(PinToken token, PreviewTargetState targetState)
    {
        ArgumentNullException.ThrowIfNull(token);

        var changed = false;
        lock (_sync)
        {
            for (var i = 0; i < _pins.Count; i++)
            {
                if (_pins[i].Id == token.Id)
                {
                    if (_pins[i].TargetState != targetState)
                    {
                        _pins[i] = _pins[i] with { TargetState = targetState };
                        changed = true;
                    }

                    break;
                }
            }
        }

        if (changed)
        {
            PinStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ReleasePin(PinToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var removed = false;
        lock (_sync)
        {
            for (var i = _pins.Count - 1; i >= 0; i--)
            {
                if (_pins[i].Id == token.Id)
                {
                    _pins.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }

        if (removed)
        {
            PinStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed record PinEntry(Guid Id, PinReason Reason, PreviewTargetState TargetState);
}
