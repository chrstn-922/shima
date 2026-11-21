using Shima.Core.Contracts;

namespace Shima.Core.Shell;

/// <summary>
/// Deterministic shell state arbiter enforcing:
/// SystemSuspended &gt; ExplicitPin &gt; FullscreenHardHide &gt; HotkeySummon &gt; UserExpanded &gt; NotificationPeek &gt; CompactActive &gt; CollapsedIdle.
/// </summary>
public sealed class IslandStateManager : IIslandStateManager, IDisposable
{
    private static readonly TimeSpan DefaultNotificationTtl = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly IOverlayPinController _pinController;
    private readonly ILogger? _logger;

    private IslandShellState _currentState = IslandShellState.Collapsed;
    private VisibilityPriority _currentPriority = VisibilityPriority.CollapsedIdle;
    private bool _fullscreenBreakthroughAlerts;
    private bool _isNotificationsPaused;
    private bool _isSystemSuspended;
    private bool _isFullscreenOnHostMonitor;
    private bool _isHotkeySummoned;
    private bool _isUserExpanded;
    private bool _isNotificationPeekActive;
    private bool _isCriticalBreakthroughPeek;
    private bool _isCompactActive;
    private bool _isInputFocusActive;
    private string? _compactPillBadgeText;
    private string? _compactActiveWidgetId;
    private TimeSpan _currentNotificationTtl = DefaultNotificationTtl;
    private bool _disposed;

    public IslandStateManager(IOverlayPinController pinController, ILogger? logger = null)
    {
        _pinController = pinController ?? throw new ArgumentNullException(nameof(pinController));
        _logger = logger;
        _pinController.PinStateChanged += OnPinStateChanged;
        ReevaluateCore(out _);
    }

    public IslandShellState CurrentState
    {
        get
        {
            lock (_sync)
            {
                return _currentState;
            }
        }
    }

    public VisibilityPriority CurrentPriority
    {
        get
        {
            lock (_sync)
            {
                return _currentPriority;
            }
        }
    }

    public bool FullscreenBreakthroughAlerts
    {
        get
        {
            lock (_sync)
            {
                return _fullscreenBreakthroughAlerts;
            }
        }
        set
        {
            IslandStateChangedEventArgs? change;
            lock (_sync)
            {
                if (_fullscreenBreakthroughAlerts == value)
                {
                    return;
                }

                _fullscreenBreakthroughAlerts = value;
                ReevaluateCore(out change);
            }

            RaiseStateChanged(change);
        }
    }

    public bool IsNotificationsPaused
    {
        get
        {
            lock (_sync)
            {
                return _isNotificationsPaused;
            }
        }
        set
        {
            IslandStateChangedEventArgs? change;
            lock (_sync)
            {
                if (_isNotificationsPaused == value)
                {
                    return;
                }

                _isNotificationsPaused = value;
                if (value)
                {
                    _isNotificationPeekActive = false;
                }

                ReevaluateCore(out change);
            }

            RaiseStateChanged(change);
        }
    }

    public string? CompactPillBadgeText
    {
        get
        {
            lock (_sync)
            {
                return _compactPillBadgeText;
            }
        }
    }

    public string? CompactActiveWidgetId
    {
        get
        {
            lock (_sync)
            {
                return _compactActiveWidgetId;
            }
        }
    }

    public TimeSpan CurrentNotificationTtl
    {
        get
        {
            lock (_sync)
            {
                return _currentNotificationTtl;
            }
        }
    }

    public event EventHandler<IslandStateChangedEventArgs>? StateChanged;

    public event EventHandler? CompactPillUpdated;

    public Task TransitionToAsync(IslandShellState newState, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        switch (newState)
        {
            case IslandShellState.Suspended:
                SetSystemSuspended(true);
                break;
            case IslandShellState.Fullscreen:
                SetSystemSuspended(false);
                SetFullscreenOnHostMonitor(true);
                break;
            case IslandShellState.Expanded:
                RequestUserExpand();
                break;
            case IslandShellState.Notification:
                RequestNotificationPeek(NotificationPriority.Normal, DefaultNotificationTtl);
                break;
            case IslandShellState.Active:
                SetCompactActive(true, CompactPillBadgeText);
                break;
            case IslandShellState.Collapsed:
                RequestUserCollapse(force: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(newState), newState, "Unsupported shell state.");
        }

        return Task.CompletedTask;
    }

    public void SetSystemSuspended(bool suspended)
    {
        IslandStateChangedEventArgs? change;
        lock (_sync)
        {
            if (_isSystemSuspended == suspended)
            {
                return;
            }

            _isSystemSuspended = suspended;
            ReevaluateCore(out change);
        }

        RaiseStateChanged(change);
    }

    public void SetFullscreenOnHostMonitor(bool isFullscreen)
    {
        IslandStateChangedEventArgs? change;
        lock (_sync)
        {
            if (_isFullscreenOnHostMonitor == isFullscreen)
            {
                return;
            }

            _isFullscreenOnHostMonitor = isFullscreen;
            if (isFullscreen)
            {
                // Entering fullscreen clears transient expansion/peeks so zero hover hit-box lingers.
                _isUserExpanded = false;
                _isHotkeySummoned = false;
                _isNotificationPeekActive = false;
                _isCriticalBreakthroughPeek = false;
            }
            else
            {
                _isCriticalBreakthroughPeek = false;
            }

            ReevaluateCore(out change);
        }

        RaiseStateChanged(change);
    }

    public void SetCompactActive(bool isActive, string? badgeText = null) =>
        SetCompactActive(null, isActive, badgeText);

    public void SetCompactActive(string? widgetId, bool isActive, string? badgeText = null)
    {
        IslandStateChangedEventArgs? change;
        bool pillChanged;
        lock (_sync)
        {
            if (!isActive
                && !string.IsNullOrWhiteSpace(widgetId)
                && !string.IsNullOrWhiteSpace(_compactActiveWidgetId)
                && !string.Equals(_compactActiveWidgetId, widgetId, StringComparison.Ordinal))
            {
                return;
            }

            var prevActive = _isCompactActive;
            var prevBadge = _compactPillBadgeText;
            var prevWidgetId = _compactActiveWidgetId;

            _isCompactActive = isActive;
            _compactPillBadgeText = isActive ? badgeText : null;
            _compactActiveWidgetId = isActive ? widgetId : null;

            pillChanged = prevActive != _isCompactActive
                || !string.Equals(prevBadge, _compactPillBadgeText, StringComparison.Ordinal)
                || !string.Equals(prevWidgetId, _compactActiveWidgetId, StringComparison.Ordinal);

            ReevaluateCore(out change);
        }

        RaiseStateChanged(change);
        if (pillChanged)
        {
            CompactPillUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RequestNotificationPeek(NotificationPriority priority, TimeSpan ttl)
    {
        var effectiveTtl = ttl > TimeSpan.Zero ? ttl : DefaultNotificationTtl;
        IslandStateChangedEventArgs? change;
        lock (_sync)
        {
            if (_isNotificationsPaused && priority != NotificationPriority.Critical)
            {
                return;
            }

            _currentNotificationTtl = effectiveTtl;
            if (_isFullscreenOnHostMonitor)
            {
                if (priority == NotificationPriority.Critical)
                {
                    _isCriticalBreakthroughPeek = true;
                }
                else
                {
                    // Standard notifications remain suppressed during fullscreen; only Inbox queues silently.
                    _isNotificationPeekActive = true;
                }
            }
            else
            {
                _isNotificationPeekActive = true;
            }

            ReevaluateCore(out change);
        }

        RaiseStateChanged(change);
    }

    public void ClearNotificationPeek()
    {
        IslandStateChangedEventArgs? change;
        lock (_sync)
        {
            _isNotificationPeekActive = false;
            _isCriticalBreakthroughPeek = false;
            ReevaluateCore(out change);
        }

        RaiseStateChanged(change);
    }

    public void RequestUserExpand()
    {
        IslandStateChangedEventArgs? change;
        lock (_sync)
        {
            if (_isFullscreenOnHostMonitor && !_pinController.IsPinned && !_isHotkeySummoned)
            {
                return;
            }

            _isUserExpanded = true;
            ReevaluateCore(out change);
        }

        RaiseStateChanged(change);
    }

    public void RequestUserCollapse(bool force = false)
    {
        IslandStateChangedEventArgs? change;
        lock (_sync)
        {
            if (!force && (_pinController.IsPinned || _isInputFocusActive))
            {
                return;
            }

            _isUserExpanded = false;
            _isHotkeySummoned = false;
            _isNotificationPeekActive = false;
            _isCriticalBreakthroughPeek = false;
            ReevaluateCore(out change);
        }

        RaiseStateChanged(change);
    }

    public void ToggleHotkeySummon()
    {
        IslandStateChangedEventArgs? change;
        lock (_sync)
        {
            if (_isHotkeySummoned || (!_isFullscreenOnHostMonitor && _isUserExpanded))
            {
                _isHotkeySummoned = false;
                _isUserExpanded = false;
                _isCriticalBreakthroughPeek = false;
            }
            else
            {
                _isHotkeySummoned = true;
            }

            ReevaluateCore(out change);
        }

        RaiseStateChanged(change);
    }

    public void DismissHotkeySummon()
    {
        IslandStateChangedEventArgs? change;
        lock (_sync)
        {
            _isHotkeySummoned = false;
            _isCriticalBreakthroughPeek = false;
            ReevaluateCore(out change);
        }

        RaiseStateChanged(change);
    }

    public void SetInputFocusActive(bool isFocused)
    {
        lock (_sync)
        {
            _isInputFocusActive = isFocused;
        }
    }

    public void Reevaluate()
    {
        IslandStateChangedEventArgs? change;
        lock (_sync)
        {
            ReevaluateCore(out change);
        }

        RaiseStateChanged(change);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pinController.PinStateChanged -= OnPinStateChanged;
    }

    private void OnPinStateChanged(object? sender, EventArgs e) => Reevaluate();

    private void ReevaluateCore(out IslandStateChangedEventArgs? change)
    {
        var previous = _currentState;
        VisibilityPriority nextPriority;
        IslandShellState nextState;

        if (_isSystemSuspended)
        {
            nextPriority = VisibilityPriority.SystemSuspended;
            nextState = IslandShellState.Suspended;
        }
        else if (_pinController.IsPinned)
        {
            nextPriority = VisibilityPriority.ExplicitPin;
            nextState = _pinController.ActiveTargetState == PreviewTargetState.Expanded
                ? IslandShellState.Expanded
                : IslandShellState.Collapsed;
        }
        else if (_isFullscreenOnHostMonitor)
        {
            if (_isHotkeySummoned)
            {
                nextPriority = VisibilityPriority.HotkeySummon;
                nextState = IslandShellState.Expanded;
            }
            else if (_isCriticalBreakthroughPeek && _fullscreenBreakthroughAlerts)
            {
                nextPriority = VisibilityPriority.HotkeySummon;
                nextState = IslandShellState.Notification;
            }
            else
            {
                nextPriority = VisibilityPriority.FullscreenHardHide;
                nextState = IslandShellState.Fullscreen;
            }
        }
        else if (_isHotkeySummoned)
        {
            nextPriority = VisibilityPriority.HotkeySummon;
            nextState = IslandShellState.Expanded;
        }
        else if (_isUserExpanded)
        {
            nextPriority = VisibilityPriority.UserExpanded;
            nextState = IslandShellState.Expanded;
        }
        else if (_isNotificationPeekActive && !_isNotificationsPaused)
        {
            nextPriority = VisibilityPriority.NotificationPeek;
            nextState = IslandShellState.Notification;
        }
        else if (_isCompactActive)
        {
            nextPriority = VisibilityPriority.CompactActive;
            nextState = IslandShellState.Active;
        }
        else
        {
            nextPriority = VisibilityPriority.CollapsedIdle;
            nextState = IslandShellState.Collapsed;
        }

        _currentPriority = nextPriority;
        _currentState = nextState;

        if (nextState != previous)
        {
            change = new IslandStateChangedEventArgs(previous, nextState);
            _logger?.Debug($"Island state transitioned {previous} -> {nextState} (Priority: {nextPriority}).");
        }
        else
        {
            change = null;
        }
    }

    private void RaiseStateChanged(IslandStateChangedEventArgs? change)
    {
        if (change is not null)
        {
            StateChanged?.Invoke(this, change);
        }
    }
}
