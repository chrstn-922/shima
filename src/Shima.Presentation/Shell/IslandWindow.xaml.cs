using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Shima.Core.Contracts;

namespace Shima.Presentation.Shell;

public partial class IslandWindow : Window
{
    private const int ClickThroughDurationMs = 3000;
    private const int WmTimeChange = 0x001E;

    private readonly IIslandStateManager _stateManager;
    private readonly IOverlayPinController _pinController;
    private readonly IOverlayWindowAdapter _overlayWindowAdapter;
    private readonly IDisplayTopologyMonitor _displayTopologyMonitor;
    private readonly IFullscreenDetector _fullscreenDetector;
    private readonly IGlobalHotkeyService _globalHotkeyService;
    private readonly IPowerAndMotionMonitor _powerAndMotionMonitor;
    private readonly IEventDispatcher? _eventDispatcher;
    private readonly ILogger? _logger;

    private readonly DispatcherTimer _hoverExpandTimer;
    private readonly DispatcherTimer _autoCollapseTimer;
    private readonly DispatcherTimer _clickThroughRestoreTimer;
    private readonly DispatcherTimer _notificationPeekTimer;

    private HwndSource? _hwndSource;
    private Func<int, nint, nint, bool>? _extraMessageHook;
    private MonitorInfo _currentMonitor;
    private MonitorSelectionMode _monitorSelectionMode = MonitorSelectionMode.Primary;
    private string? _specificMonitorDeviceId;
    private bool _expandOnHover = true;
    private int _hoverExpandDelayMs = 350;
    private int _autoCollapseDelayMs = 450;
    private bool _isClickThroughActive;
    private IslandShellState _renderedState = IslandShellState.Collapsed;
    private IslandVisualSpec _currentVisualSpec = ConcaveNotchGeometryBuilder.CollapsedSpec;
    private long _transitionGeneration;
    private nint _hwnd;

    public IslandWindow(
        IIslandStateManager stateManager,
        IOverlayPinController pinController,
        IOverlayWindowAdapter overlayWindowAdapter,
        IDisplayTopologyMonitor displayTopologyMonitor,
        IFullscreenDetector fullscreenDetector,
        IGlobalHotkeyService globalHotkeyService,
        IPowerAndMotionMonitor powerAndMotionMonitor,
        ILogger? logger = null,
        IEventDispatcher? eventDispatcher = null)
    {
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        _pinController = pinController ?? throw new ArgumentNullException(nameof(pinController));
        _overlayWindowAdapter = overlayWindowAdapter ?? throw new ArgumentNullException(nameof(overlayWindowAdapter));
        _displayTopologyMonitor = displayTopologyMonitor ?? throw new ArgumentNullException(nameof(displayTopologyMonitor));
        _fullscreenDetector = fullscreenDetector ?? throw new ArgumentNullException(nameof(fullscreenDetector));
        _globalHotkeyService = globalHotkeyService ?? throw new ArgumentNullException(nameof(globalHotkeyService));
        _powerAndMotionMonitor = powerAndMotionMonitor ?? throw new ArgumentNullException(nameof(powerAndMotionMonitor));
        _logger = logger;
        _eventDispatcher = eventDispatcher;

        InitializeComponent();

        _currentMonitor = _displayTopologyMonitor.ResolveTargetMonitor(_monitorSelectionMode, _specificMonitorDeviceId);

        _hoverExpandTimer = CreateOneShotTimer(TimeSpan.FromMilliseconds(_hoverExpandDelayMs), OnHoverExpandTimerTick);
        _autoCollapseTimer = CreateOneShotTimer(TimeSpan.FromMilliseconds(_autoCollapseDelayMs), OnAutoCollapseTimerTick);
        _clickThroughRestoreTimer = CreateOneShotTimer(
            TimeSpan.FromMilliseconds(ClickThroughDurationMs),
            OnClickThroughRestoreTimerTick);
        _notificationPeekTimer = CreateOneShotTimer(TimeSpan.FromSeconds(5), OnNotificationPeekTimerTick);

        _stateManager.StateChanged += OnShellStateChanged;
        _stateManager.CompactPillUpdated += OnCompactPillUpdated;
        _displayTopologyMonitor.MonitorChanged += OnMonitorChanged;
        _displayTopologyMonitor.DpiChanged += OnDpiChanged;
        _fullscreenDetector.FullscreenStateChanged += OnFullscreenStateChanged;
        _powerAndMotionMonitor.PowerProfileChanged += OnPowerProfileChanged;
        _powerAndMotionMonitor.SessionLockChanged += OnSessionLockChanged;
        _globalHotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;
    }

    public nint WindowHandle => _hwnd;

    public WidgetHost WidgetHost => WidgetHostControl;

    public void SetWindowMessageHook(Func<int, nint, nint, bool>? messageHook) =>
        _extraMessageHook = messageHook;

    public void SetWidgetSlots(IReadOnlyList<WidgetSlotItem> slots, string? selectedWidgetId = null) =>
        WidgetHostControl.SetSlots(slots, selectedWidgetId);

    public void SetCompactActiveContent(object? content)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetCompactActiveContent(content));
            return;
        }

        ActiveCustomContentPresenter.Content = content;
        ActiveCustomContentPresenter.Visibility = content is not null ? Visibility.Visible : Visibility.Collapsed;
        ActiveDefaultBadgePanel.Visibility = content is null ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetNotificationContent(string title, string body)
    {
        NotificationTitleText.Text = string.IsNullOrWhiteSpace(title) ? "Notification" : title;
        NotificationBodyText.Text = body ?? string.Empty;
    }

    public void ApplyBehaviorPreferences(
        MonitorSelectionMode monitorSelectionMode,
        string? specificMonitorDeviceId,
        bool expandOnHover,
        int hoverExpandDelayMs,
        int autoCollapseDelayMs,
        bool fullscreenBreakthroughAlerts)
    {
        _monitorSelectionMode = monitorSelectionMode;
        _specificMonitorDeviceId = specificMonitorDeviceId;
        _expandOnHover = expandOnHover;
        _hoverExpandDelayMs = Math.Clamp(hoverExpandDelayMs, 100, 1500);
        _autoCollapseDelayMs = Math.Clamp(autoCollapseDelayMs, 200, 3000);
        _stateManager.FullscreenBreakthroughAlerts = fullscreenBreakthroughAlerts;

        _hoverExpandTimer.Interval = TimeSpan.FromMilliseconds(_hoverExpandDelayMs);
        _autoCollapseTimer.Interval = TimeSpan.FromMilliseconds(_autoCollapseDelayMs);

        ReanchorToTargetMonitor();
    }

    public void ReanchorToPrimaryMonitor()
    {
        _currentMonitor = _displayTopologyMonitor.GetPrimaryMonitor();
        ReanchorCurrentSpec();
        SyncFullscreenForCurrentMonitor();
    }

    public void ReanchorToTargetMonitor()
    {
        _currentMonitor = _displayTopologyMonitor.ResolveTargetMonitor(_monitorSelectionMode, _specificMonitorDeviceId);
        ReanchorCurrentSpec();
        SyncFullscreenForCurrentMonitor();
    }

    public void BeginInteractiveInput()
    {
        _stateManager.SetInputFocusActive(true);
        if (_hwnd != 0)
        {
            _overlayWindowAdapter.SetInteractiveFocusMode(_hwnd, allowFocus: true);
        }
    }

    public void EndInteractiveInput()
    {
        _stateManager.SetInputFocusActive(false);
        if (_hwnd != 0)
        {
            _overlayWindowAdapter.SetInteractiveFocusMode(_hwnd, allowFocus: false);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        _overlayWindowAdapter.ApplyNonActivatingStyles(_hwnd);
        _powerAndMotionMonitor.Attach(_hwnd);
        _fullscreenDetector.Attach(_hwnd);

        ReanchorToTargetMonitor();
        ApplyVisualState(_stateManager.CurrentState, immediate: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopAllTransientTimers();

        _stateManager.StateChanged -= OnShellStateChanged;
        _stateManager.CompactPillUpdated -= OnCompactPillUpdated;
        _displayTopologyMonitor.MonitorChanged -= OnMonitorChanged;
        _displayTopologyMonitor.DpiChanged -= OnDpiChanged;
        _fullscreenDetector.FullscreenStateChanged -= OnFullscreenStateChanged;
        _powerAndMotionMonitor.PowerProfileChanged -= OnPowerProfileChanged;
        _powerAndMotionMonitor.SessionLockChanged -= OnSessionLockChanged;
        _globalHotkeyService.HotkeyPressed -= OnGlobalHotkeyPressed;

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;

        base.OnClosed(e);
    }

    private static DispatcherTimer CreateOneShotTimer(TimeSpan interval, EventHandler handler)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = interval
        };
        timer.Tick += handler;
        return timer;
    }

    private void OnShellStateChanged(object? sender, IslandStateChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyVisualState(e.NewState, immediate: false);
        }
        else
        {
            Dispatcher.BeginInvoke(() => ApplyVisualState(e.NewState, immediate: false));
        }
    }

    private void OnCompactPillUpdated(object? sender, EventArgs e)
    {
        void SyncBadge()
        {
            ActiveBadgeText.Text = _stateManager.CompactPillBadgeText ?? "Active";
        }

        if (Dispatcher.CheckAccess())
        {
            SyncBadge();
        }
        else
        {
            Dispatcher.BeginInvoke(SyncBadge);
        }
    }

    private void OnMonitorChanged(object? sender, MonitorInfo monitor)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _currentMonitor = _displayTopologyMonitor.ResolveTargetMonitor(_monitorSelectionMode, _specificMonitorDeviceId);
            ReanchorCurrentSpec();
            SyncFullscreenForCurrentMonitor();
        });
    }

    private void OnDpiChanged(object? sender, double scale)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _currentMonitor = _currentMonitor with { DpiScale = scale };
            ReanchorCurrentSpec();
        });
    }

    private void OnFullscreenStateChanged(object? sender, FullscreenStateChangedEventArgs e)
    {
        if (e.MonitorHandle == _currentMonitor.Handle)
        {
            _stateManager.SetFullscreenOnHostMonitor(e.IsFullscreen);
        }
    }

    private void OnPowerProfileChanged(object? sender, PowerProfile profile) =>
        EvaluateSystemSuspension();

    private void OnSessionLockChanged(object? sender, bool isLocked) =>
        EvaluateSystemSuspension();

    private void EvaluateSystemSuspension()
    {
        var suspended = _powerAndMotionMonitor.CurrentPowerProfile == PowerProfile.DisplayOff
            || _powerAndMotionMonitor.IsSessionLocked;
        _stateManager.SetSystemSuspended(suspended);
    }

    private void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            _stateManager.ToggleHotkeySummon();
        }
        else
        {
            Dispatcher.BeginInvoke(() => _stateManager.ToggleHotkeySummon());
        }
    }

    private void ApplyVisualState(IslandShellState nextState, bool immediate)
    {
        var generation = ++_transitionGeneration;

        // Cancel any in-flight scale storyboard cleanly before switching target state.
        MorphScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        MorphScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (nextState is IslandShellState.Fullscreen or IslandShellState.Suspended)
        {
            StopAllTransientTimers();
            RestoreClickThroughImmediate();
            if (_hwnd != 0)
            {
                _overlayWindowAdapter.HideOverlay(_hwnd);
            }

            _renderedState = nextState;
            return;
        }

        if (nextState == IslandShellState.Notification)
        {
            _notificationPeekTimer.Stop();
            _notificationPeekTimer.Interval = _stateManager.CurrentNotificationTtl;
            _notificationPeekTimer.Start();
        }
        else
        {
            _notificationPeekTimer.Stop();
        }

        var previousSpec = _currentVisualSpec;
        var targetSpec = ConcaveNotchGeometryBuilder.GetSpecForState(nextState);
        var isExpanding = targetSpec.WidthDip >= previousSpec.WidthDip && targetSpec.HeightDip >= previousSpec.HeightDip;
        var reducedMotion = immediate || _powerAndMotionMonitor.IsReducedMotionEnabled;

        if (_hwnd != 0)
        {
            _overlayWindowAdapter.ShowOverlay(_hwnd);
        }

        UpdateLayerVisibility(nextState);
        ApplySurfaceGeometryAndAnchor(nextState, targetSpec);

        if (!reducedMotion && previousSpec != targetSpec)
        {
            var fromScaleX = Math.Clamp(previousSpec.WidthDip / targetSpec.WidthDip, 0.25, 1.5);
            var fromScaleY = Math.Clamp(previousSpec.HeightDip / targetSpec.HeightDip, 0.25, 1.5);
            var durationMs = isExpanding ? 220 : 180;
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var animX = new DoubleAnimation(fromScaleX, 1.0, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = easing
            };
            var animY = new DoubleAnimation(fromScaleY, 1.0, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = easing
            };

            animX.Completed += (_, _) =>
            {
                if (_transitionGeneration != generation)
                {
                    return;
                }

                MorphScaleTransform.ScaleX = 1.0;
                MorphScaleTransform.ScaleY = 1.0;
            };

            MorphScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            MorphScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }
        else
        {
            MorphScaleTransform.ScaleX = 1.0;
            MorphScaleTransform.ScaleY = 1.0;
        }

        _renderedState = nextState;
        _logger?.Debug($"Island rendered state {nextState} ({targetSpec.WidthDip}x{targetSpec.HeightDip} DIP).");
    }

    private void UpdateLayerVisibility(IslandShellState state)
    {
        ActiveBadgeText.Text = _stateManager.CompactPillBadgeText ?? "Active";

        ActiveLayer.Visibility = state == IslandShellState.Active ? Visibility.Visible : Visibility.Collapsed;
        NotificationLayer.Visibility = state == IslandShellState.Notification ? Visibility.Visible : Visibility.Collapsed;
        ExpandedLayer.Visibility = state == IslandShellState.Expanded ? Visibility.Visible : Visibility.Collapsed;

        var surfaceBrushKey = state is IslandShellState.Expanded or IslandShellState.Notification
            ? "SurfaceExpanded"
            : "SurfaceCollapsed";
        NotchSurfacePath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, surfaceBrushKey);
    }

    private void ApplySurfaceGeometryAndAnchor(IslandShellState state, IslandVisualSpec targetSpec)
    {
        _currentVisualSpec = targetSpec;

        Width = targetSpec.WidthDip;
        Height = targetSpec.HeightDip;
        NotchSurfacePath.Data = ConcaveNotchGeometryBuilder.Build(targetSpec);

        if (_hwnd != 0)
        {
            _overlayWindowAdapter.AnchorToMonitor(
                _hwnd,
                _currentMonitor.Bounds,
                _currentMonitor.DpiScale,
                targetSpec.ToDipSize());
        }
    }

    private void ReanchorCurrentSpec()
    {
        if (_renderedState is IslandShellState.Fullscreen or IslandShellState.Suspended)
        {
            return;
        }

        if (_hwnd != 0)
        {
            _overlayWindowAdapter.AnchorToMonitor(
                _hwnd,
                _currentMonitor.Bounds,
                _currentMonitor.DpiScale,
                _currentVisualSpec.ToDipSize());
        }
    }

    private void SyncFullscreenForCurrentMonitor() =>
        _stateManager.SetFullscreenOnHostMonitor(_fullscreenDetector.IsMonitorFullscreen(_currentMonitor.Handle));

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        _autoCollapseTimer.Stop();

        if (TryActivateAltClickThrough())
        {
            return;
        }

        if (_expandOnHover && _stateManager.CurrentState is IslandShellState.Collapsed or IslandShellState.Active)
        {
            _hoverExpandTimer.Stop();
            _hoverExpandTimer.Interval = TimeSpan.FromMilliseconds(_hoverExpandDelayMs);
            _hoverExpandTimer.Start();
        }
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (TryActivateAltClickThrough())
        {
            _hoverExpandTimer.Stop();
        }
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        _hoverExpandTimer.Stop();

        if (_stateManager.CurrentState is IslandShellState.Expanded or IslandShellState.Notification
            && !_pinController.IsPinned)
        {
            _autoCollapseTimer.Stop();
            _autoCollapseTimer.Interval = TimeSpan.FromMilliseconds(_autoCollapseDelayMs);
            _autoCollapseTimer.Start();
        }
    }

    private void OnWindowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isClickThroughActive)
        {
            return;
        }

        if (_stateManager.CurrentState is IslandShellState.Collapsed or IslandShellState.Active)
        {
            _hoverExpandTimer.Stop();
            _stateManager.RequestUserExpand();
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EndInteractiveInput();
            _stateManager.RequestUserCollapse(force: true);
            e.Handled = true;
        }
    }

    private void OnDismissNotificationClicked(object sender, RoutedEventArgs e)
    {
        _notificationPeekTimer.Stop();
        _stateManager.ClearNotificationPeek();
        e.Handled = true;
    }

    private bool TryActivateAltClickThrough()
    {
        if (_hwnd == 0
            || _stateManager.CurrentState is not (IslandShellState.Collapsed or IslandShellState.Active)
            || (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            return false;
        }

        if (!_isClickThroughActive)
        {
            _isClickThroughActive = true;
            RootContainer.Opacity = 0.25;
            _overlayWindowAdapter.SetClickThrough(_hwnd, transparent: true);
        }

        _clickThroughRestoreTimer.Stop();
        _clickThroughRestoreTimer.Start();
        return true;
    }

    private void RestoreClickThroughImmediate()
    {
        _clickThroughRestoreTimer.Stop();
        if (_isClickThroughActive)
        {
            _isClickThroughActive = false;
            RootContainer.Opacity = 1.0;
            if (_hwnd != 0)
            {
                _overlayWindowAdapter.SetClickThrough(_hwnd, transparent: false);
            }
        }
    }

    private void OnHoverExpandTimerTick(object? sender, EventArgs e)
    {
        _hoverExpandTimer.Stop();
        if (_isClickThroughActive || (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            return;
        }

        _stateManager.RequestUserExpand();
    }

    private void OnAutoCollapseTimerTick(object? sender, EventArgs e)
    {
        _autoCollapseTimer.Stop();
        _stateManager.RequestUserCollapse();
    }

    private void OnClickThroughRestoreTimerTick(object? sender, EventArgs e) =>
        RestoreClickThroughImmediate();

    private void OnNotificationPeekTimerTick(object? sender, EventArgs e)
    {
        _notificationPeekTimer.Stop();
        _stateManager.ClearNotificationPeek();
    }

    private void StopAllTransientTimers()
    {
        _hoverExpandTimer.Stop();
        _autoCollapseTimer.Stop();
        _clickThroughRestoreTimer.Stop();
        _notificationPeekTimer.Stop();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_extraMessageHook is not null && _extraMessageHook(msg, wParam, lParam))
        {
            handled = true;
            return 0;
        }

        _displayTopologyMonitor.HandleWindowMessage(msg, wParam, lParam, hwnd);
        _powerAndMotionMonitor.HandleWindowMessage(msg, wParam, lParam);

        if (msg == WmTimeChange)
        {
            _eventDispatcher?.Publish(new SystemTimeChangedEvent(DateTimeOffset.UtcNow));
        }

        if (_globalHotkeyService.HandleWindowMessage(msg, wParam))
        {
            handled = true;
        }

        return 0;
    }
}
