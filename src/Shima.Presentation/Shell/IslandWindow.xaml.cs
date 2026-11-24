using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Shima.Core.Contracts;
using Shima.Core.Shell;

namespace Shima.Presentation.Shell;

public sealed record IslandPositionCommittedEventArgs(
    double OffsetXDip,
    double? FloatingTopOffsetDip);

public partial class IslandWindow : Window
{
    private const int ClickThroughDurationMs = 3000;
    private const int WmTimeChange = 0x001E;
    private const int WmNcHitTest = 0x0084;
    private const nint HtTransparent = -1;
    private const nint HtClient = 1;

    private const double FloatingPadSideDip = 14.0;
    private const double FloatingPadTopDip = 10.0;
    private const double FloatingPadBottomDip = 16.0;
    private const double FloatingHoverLiftScale = 1.02;
    private const double DragLiftScale = 1.03;
    private const double FloatingRestingShadowOpacity = 0.34;
    private const double FloatingHoverShadowOpacity = 0.44;
    private const double DragShadowOpacity = 0.52;
    private const double DragOverscrollCapDip = 18.0;
    private const double DragOverscrollDamping = 0.25;

    private const double StyleMorphDurationMs = 220.0;
    private const double LiftDurationMs = 150.0;
    private const double PositionGlideDurationMs = 190.0;
    private const double CompanionEnterDurationMs = 180.0;
    private const double CompanionExitDurationMs = 160.0;

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
    private readonly List<Func<int, nint, nint, bool>> _extraMessageHooks = [];
    private readonly Pen _hitTestStrokePen = new(Brushes.Transparent, 2.0);
    private readonly CubicEase _cubicEaseOut = new() { EasingMode = EasingMode.EaseOut };

    private DropShadowEffect? _surfaceDropShadow;

    private HwndSource? _hwndSource;
    private IDisposable? _inputFocusSubscription;
    private IDisposable? _notificationPeekContentSubscription;
    private IDisposable? _notificationPeekDismissSubscription;
    private string? _activeNotificationWidgetId;
    private string? _activeNotificationItemId;
    private NotificationPeekAction? _primaryPeekAction;
    private NotificationPeekAction? _secondaryPeekAction;
    private MonitorInfo _currentMonitor;
    private MonitorSelectionMode _monitorSelectionMode = MonitorSelectionMode.Primary;
    private string? _specificMonitorDeviceId;
    private IslandStyle _islandStyle = IslandStyle.DockedNotch;
    private bool _lockIslandPosition;
    private double _persistedOffsetXDip;
    private double? _persistedFloatingTopOffsetDip;
    private bool _expandOnHover = true;
    private int _hoverExpandDelayMs = 350;
    private int _autoCollapseDelayMs = 450;
    private bool _isClickThroughActive;
    private bool _interactiveInputActive;
    private bool _isPointerHoveringSurface;
    private IslandShellState _renderedState = IslandShellState.Collapsed;
    private IslandVisualSpec _currentVisualSpec = ConcaveNotchGeometryBuilder.CollapsedSpec;

    // Companion bottom-edge slim bar state
    private object? _targetCompanionContent;
    private bool _isCompanionRenderedVisible;
    private bool _isCompanionAnimatingExit;
    private long _companionTransitionGeneration;

    // Unified VSync motion tracks (Stopwatch-backed; 0% idle CPU when detached)
    private bool _motionHookAttached;

    private double _floatingProgress;
    private bool _styleMorphActive;
    private double _styleMorphStart;
    private double _styleMorphTarget;
    private long _styleMorphStartTimestamp;

    private bool _stateMorphActive;
    private DipRect _stateStartVisualRect;
    private DipRect _stateTargetVisualRect;
    private double _stateMorphDurationMs = 200.0;
    private long _stateMorphStartTimestamp;

    private bool _positionGlideActive;
    private double _liveOffsetXDip;
    private double _liveTopOffsetDip;
    private double _glideStartOffsetXDip;
    private double _glideStartTopOffsetDip;
    private double _glideTargetOffsetXDip;
    private double _glideTargetTopOffsetDip;
    private double _glideDurationMs = PositionGlideDurationMs;
    private long _glideStartTimestamp;

    private double _currentLiftScale = 1.0;
    private double _currentShadowOpacity;
    private double _currentShadowBlur = 12.0;
    private double _currentShadowDepth = 3.0;
    private bool _liftAnimActive;
    private double _liftStartScale = 1.0;
    private double _liftTargetScale = 1.0;
    private double _liftStartShadowOpacity;
    private double _liftTargetShadowOpacity;
    private double _liftStartShadowBlur = 12.0;
    private double _liftTargetShadowBlur = 12.0;
    private double _liftStartShadowDepth = 3.0;
    private double _liftTargetShadowDepth = 3.0;
    private double _liftDurationMs = LiftDurationMs;
    private long _liftStartTimestamp;

    // Interactive drag tracking
    private bool _dragArmed;
    private bool _isDragging;
    private bool _dragHandledClickSuppression;
    private Point _dragStartCursorScreenPx;
    private double _dragStartOffsetXDip;
    private double _dragStartTopOffsetDip;

    private DipRect _currentVisualBoundsDip;
    private DipRect _currentWindowBoundsDip;
    private MonitorBounds _currentWindowPixelRect;
    private double _currentPadLeftDip;
    private double _currentPadTopDip;
    private double _currentPadRightDip;
    private double _currentPadBottomDip;
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

        _hitTestStrokePen.Freeze();
        _cubicEaseOut.Freeze();

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
        _powerAndMotionMonitor.ReducedMotionChanged += OnReducedMotionChanged;
        _powerAndMotionMonitor.SessionLockChanged += OnSessionLockChanged;
        _globalHotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;

        ExpandedLayer.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnExpandedLayerPreviewMouseDown), handledEventsToo: true);
        ExpandedLayer.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnExpandedLayerGotKeyboardFocus), handledEventsToo: true);
        ExpandedLayer.AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnExpandedLayerLostKeyboardFocus), handledEventsToo: true);

        if (_eventDispatcher is not null)
        {
            _inputFocusSubscription = _eventDispatcher.Subscribe<IslandInputFocusRequestedEvent>(
                evt => Dispatcher.BeginInvoke(() =>
                {
                    if (evt.IsActive)
                    {
                        BeginInteractiveInput();
                    }
                    else
                    {
                        EndInteractiveInput();
                    }
                }));

            _notificationPeekContentSubscription = _eventDispatcher.Subscribe<NotificationPeekContentEvent>(
                evt => Dispatcher.BeginInvoke(() =>
                {
                    _activeNotificationWidgetId = evt.WidgetId;
                    _activeNotificationItemId = evt.ItemId;
                    SetNotificationContent(evt.Title, evt.Body, evt.PrimaryAction, evt.SecondaryAction);
                }));

            _notificationPeekDismissSubscription = _eventDispatcher.Subscribe<NotificationPeekDismissRequestedEvent>(
                evt => Dispatcher.BeginInvoke(() =>
                {
                    if (string.Equals(_activeNotificationWidgetId, evt.WidgetId, StringComparison.Ordinal)
                        && (evt.ItemId is null || string.Equals(_activeNotificationItemId, evt.ItemId, StringComparison.Ordinal)))
                    {
                        _notificationPeekTimer.Stop();
                        _stateManager.ClearNotificationPeek();
                    }
                }));
        }
    }

    public nint WindowHandle => _hwnd;

    public WidgetHost WidgetHost => WidgetHostControl;

    public IslandStyle CurrentIslandStyle => _islandStyle;

    public bool IsPositionLocked => _lockIslandPosition;

    public double CurrentOffsetXDip => _persistedOffsetXDip;

    public double? CurrentFloatingTopOffsetDip => _persistedFloatingTopOffsetDip;

    public double CurrentFloatingProgress => _floatingProgress;

    public double CurrentShadowOpacity => _currentShadowOpacity;

    public double CurrentLiftScale => _currentLiftScale;

    public DipRect CurrentVisualBoundsDip => _currentVisualBoundsDip;

    public bool IsDraggingIsland => _isDragging;

    public bool IsMotionAnimationActive => _motionHookAttached;

    public event EventHandler<IslandPositionCommittedEventArgs>? PositionCommitted;

    public void SetWindowMessageHook(Func<int, nint, nint, bool>? messageHook)
    {
        _extraMessageHooks.Clear();
        if (messageHook is not null)
        {
            _extraMessageHooks.Add(messageHook);
        }
    }

    public void AddWindowMessageHook(Func<int, nint, nint, bool> messageHook)
    {
        ArgumentNullException.ThrowIfNull(messageHook);
        if (!_extraMessageHooks.Contains(messageHook))
        {
            _extraMessageHooks.Add(messageHook);
        }
    }

    public void RemoveWindowMessageHook(Func<int, nint, nint, bool> messageHook)
    {
        ArgumentNullException.ThrowIfNull(messageHook);
        _extraMessageHooks.Remove(messageHook);
    }

    public void SetWidgetSlots(IReadOnlyList<WidgetSlotItem> slots, string? selectedWidgetId = null) =>
        WidgetHostControl.SetSlots(slots, selectedWidgetId);

    public void SetCompactActiveContent(object? content)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetCompactActiveContent(content));
            return;
        }

        if (content is FrameworkElement { Parent: ContentPresenter existingParent } && !ReferenceEquals(existingParent, ActiveCustomContentPresenter))
        {
            existingParent.Content = null;
        }

        ActiveCustomContentPresenter.Content = content;
        ActiveCustomContentPresenter.Visibility = content is not null ? Visibility.Visible : Visibility.Collapsed;
        ActiveCustomContentPresenter.IsHitTestVisible = content is not null;
        ActiveDefaultBadgePanel.Visibility = content is null ? Visibility.Visible : Visibility.Collapsed;
        NotifyPrimaryCompactCompanionChanged(isActive: false);
    }

    public void SetCompactCompanionContent(object? content)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetCompactCompanionContent(content));
            return;
        }

        var hadContent = _targetCompanionContent is not null;
        _targetCompanionContent = content;

        if (content is IReducedMotionAware motionAware)
        {
            motionAware.SetReducedMotion(_powerAndMotionMonitor.IsReducedMotionEnabled);
        }

        if (content is not null && hadContent && ReferenceEquals(ActiveCompanionContentPresenter.Content, content))
        {
            if (_stateManager.CurrentState == IslandShellState.Active
                && (_renderedState == IslandShellState.Active || _isCompanionAnimatingExit))
            {
                AnimateCompanionEnter(immediate: _powerAndMotionMonitor.IsReducedMotionEnabled);
            }

            return;
        }

        if (content is FrameworkElement { Parent: ContentPresenter oldParent }
            && !ReferenceEquals(oldParent, ActiveCompanionContentPresenter))
        {
            oldParent.Content = null;
        }

        if (content is not null)
        {
            ActiveCompanionContentPresenter.Content = content;
            if (_stateManager.CurrentState == IslandShellState.Active && _renderedState == IslandShellState.Active)
            {
                AnimateCompanionEnter(immediate: _powerAndMotionMonitor.IsReducedMotionEnabled);
            }
            else if (ActiveCompanionHost.Visibility == Visibility.Visible)
            {
                AnimateCompanionExit(clearContentOnComplete: false, immediate: _powerAndMotionMonitor.IsReducedMotionEnabled);
            }

            return;
        }

        if (ActiveCompanionHost.Visibility == Visibility.Visible || _isCompanionRenderedVisible || _isCompanionAnimatingExit)
        {
            AnimateCompanionExit(
                clearContentOnComplete: true,
                immediate: _powerAndMotionMonitor.IsReducedMotionEnabled || _renderedState != IslandShellState.Active);
        }
        else
        {
            ActiveCompanionContentPresenter.Content = null;
        }
    }

    private void NotifyPrimaryCompactCompanionChanged(bool isActive)
    {
        if (ActiveCustomContentPresenter.Content is ICompactCompanionAware aware)
        {
            aware.SetCompanionActive(isActive);
        }
    }

    public void SetCollapsedStatusContent(object? content)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetCollapsedStatusContent(content));
            return;
        }

        CollapsedOverlayContentPresenter.Content = content;
        UpdateCollapsedOverlayVisibility(_renderedState);
    }

    public void SetNotificationContent(
        string title,
        string body,
        NotificationPeekAction? primaryAction = null,
        NotificationPeekAction? secondaryAction = null)
    {
        NotificationTitleText.Text = string.IsNullOrWhiteSpace(title) ? "Notification" : title;
        NotificationBodyText.Text = body ?? string.Empty;

        _primaryPeekAction = primaryAction;
        _secondaryPeekAction = secondaryAction;

        ConfigurePeekActionButton(NotificationPrimaryActionButton, primaryAction);
        ConfigurePeekActionButton(NotificationSecondaryActionButton, secondaryAction);
    }

    public void ApplyBehaviorPreferences(
        MonitorSelectionMode monitorSelectionMode,
        string? specificMonitorDeviceId,
        bool expandOnHover,
        int hoverExpandDelayMs,
        int autoCollapseDelayMs,
        bool fullscreenBreakthroughAlerts) =>
        ApplyBehaviorPreferences(
            monitorSelectionMode,
            specificMonitorDeviceId,
            expandOnHover,
            hoverExpandDelayMs,
            autoCollapseDelayMs,
            fullscreenBreakthroughAlerts,
            _islandStyle,
            _lockIslandPosition,
            _persistedOffsetXDip,
            _persistedFloatingTopOffsetDip);

    public void ApplyBehaviorPreferences(
        MonitorSelectionMode monitorSelectionMode,
        string? specificMonitorDeviceId,
        bool expandOnHover,
        int hoverExpandDelayMs,
        int autoCollapseDelayMs,
        bool fullscreenBreakthroughAlerts,
        IslandStyle islandStyle,
        bool lockIslandPosition,
        double islandOffsetXDip,
        double? islandFloatingTopOffsetDip)
    {
        var previousMonitorMode = _monitorSelectionMode;
        var previousDeviceId = _specificMonitorDeviceId;
        var previousStyle = _islandStyle;
        var previousOffsetX = _persistedOffsetXDip;
        var previousFloatingTop = _persistedFloatingTopOffsetDip;

        _monitorSelectionMode = monitorSelectionMode;
        _specificMonitorDeviceId = specificMonitorDeviceId;
        _expandOnHover = expandOnHover;
        _hoverExpandDelayMs = Math.Clamp(hoverExpandDelayMs, 100, 1500);
        _autoCollapseDelayMs = Math.Clamp(autoCollapseDelayMs, 200, 3000);
        _stateManager.FullscreenBreakthroughAlerts = fullscreenBreakthroughAlerts;

        _islandStyle = Enum.IsDefined(islandStyle) ? islandStyle : IslandStyle.DockedNotch;
        _lockIslandPosition = lockIslandPosition;
        if (_lockIslandPosition && (_isDragging || _dragArmed))
        {
            FinishArmedOrActiveDrag(commit: false);
        }

        _persistedOffsetXDip = double.IsFinite(islandOffsetXDip) ? islandOffsetXDip : 0.0;
        _persistedFloatingTopOffsetDip = islandFloatingTopOffsetDip is { } floatingTop && double.IsFinite(floatingTop)
            ? Math.Max(0.0, floatingTop)
            : null;

        _hoverExpandTimer.Interval = TimeSpan.FromMilliseconds(_hoverExpandDelayMs);
        _autoCollapseTimer.Interval = TimeSpan.FromMilliseconds(_autoCollapseDelayMs);

        var nextMonitor = _displayTopologyMonitor.ResolveTargetMonitor(_monitorSelectionMode, _specificMonitorDeviceId);
        var monitorChanged = previousMonitorMode != _monitorSelectionMode
            || !string.Equals(previousDeviceId, _specificMonitorDeviceId, StringComparison.OrdinalIgnoreCase)
            || nextMonitor.Handle != _currentMonitor.Handle;

        _currentMonitor = nextMonitor;
        SyncFullscreenForCurrentMonitor();

        var styleChanged = previousStyle != _islandStyle;
        var positionChanged = Math.Abs(previousOffsetX - _persistedOffsetXDip) > 0.25
            || Math.Abs((previousFloatingTop ?? IslandPositionMath.DefaultFloatingTopMarginDip)
                        - (_persistedFloatingTopOffsetDip ?? IslandPositionMath.DefaultFloatingTopMarginDip)) > 0.25;

        var immediate = _hwnd == 0 || monitorChanged || _powerAndMotionMonitor.IsReducedMotionEnabled;

        if (styleChanged || positionChanged || monitorChanged)
        {
            TransitionStyleAndPosition(immediate);
        }
        else if (_hwnd != 0)
        {
            ApplyResolvedPlacementFrame();
        }
    }

    public void ResetPosition()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ResetPosition);
            return;
        }

        if (_isDragging || _dragArmed)
        {
            FinishArmedOrActiveDrag(commit: false);
        }

        _persistedOffsetXDip = 0.0;
        _persistedFloatingTopOffsetDip = null;

        var immediate = _hwnd == 0 || _powerAndMotionMonitor.IsReducedMotionEnabled;
        StartPositionGlide(
            0.0,
            IslandPositionMath.ResolveEffectiveTopOffsetDip(_islandStyle, null),
            PositionGlideDurationMs,
            immediate);
        UpdateLiftTarget(immediate);

        PositionCommitted?.Invoke(this, new IslandPositionCommittedEventArgs(0.0, null));
    }

    public void ReanchorToPrimaryMonitor()
    {
        _currentMonitor = _displayTopologyMonitor.GetPrimaryMonitor();
        _persistedOffsetXDip = 0.0;
        _persistedFloatingTopOffsetDip = null;
        _liveOffsetXDip = 0.0;
        _liveTopOffsetDip = IslandPositionMath.ResolveEffectiveTopOffsetDip(_islandStyle, null);
        ReanchorCurrentSpec();
        SyncFullscreenForCurrentMonitor();
        PositionCommitted?.Invoke(this, new IslandPositionCommittedEventArgs(0.0, null));
    }

    public void ReanchorToTargetMonitor()
    {
        _currentMonitor = _displayTopologyMonitor.ResolveTargetMonitor(_monitorSelectionMode, _specificMonitorDeviceId);
        ReanchorCurrentSpec();
        SyncFullscreenForCurrentMonitor();
    }

    public void BeginInteractiveInput()
    {
        if (_interactiveInputActive)
        {
            return;
        }

        _interactiveInputActive = true;
        _stateManager.SetInputFocusActive(true);
        if (_hwnd != 0)
        {
            _overlayWindowAdapter.SetInteractiveFocusMode(_hwnd, allowFocus: true);
        }
    }

    public void EndInteractiveInput()
    {
        if (!_interactiveInputActive)
        {
            return;
        }

        _interactiveInputActive = false;
        _stateManager.SetInputFocusActive(false);
        if (_hwnd != 0)
        {
            _overlayWindowAdapter.SetInteractiveFocusMode(_hwnd, allowFocus: false);
        }
    }

    internal bool HitTestInteractiveSurfaceDip(Point windowDipPoint)
    {
        if (_isClickThroughActive)
        {
            return false;
        }

        if (NotchSurfacePath.IsVisible && NotchSurfacePath.Data is Geometry surfaceGeometry)
        {
            try
            {
                var ptInPath = TranslatePoint(windowDipPoint, NotchSurfacePath);
                if (surfaceGeometry.FillContains(ptInPath)
                    || surfaceGeometry.StrokeContains(_hitTestStrokePen, ptInPath))
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        return false;
    }

    internal void SetHoveringForTesting(bool overSurface) =>
        UpdatePointerSurfaceHoverState(overSurface);

    internal void SimulateDragGestureForTesting(double deltaXDip, double deltaYDip, bool completeDrop = true)
    {
        if (_lockIslandPosition)
        {
            return;
        }

        _dragArmed = true;
        _isDragging = false;
        _dragHandledClickSuppression = false;
        _dragStartOffsetXDip = _liveOffsetXDip;
        _dragStartTopOffsetDip = _liveTopOffsetDip;
        ApplyDragDeltaDip(deltaXDip, deltaYDip, capturePointer: false);

        if (completeDrop)
        {
            FinishArmedOrActiveDrag(commit: true);
        }
    }

    internal void ForceCompleteActiveMotionForTesting()
    {
        if (!_motionHookAttached)
        {
            return;
        }

        if (_styleMorphActive)
        {
            _floatingProgress = _styleMorphTarget;
            _styleMorphActive = false;
        }

        if (_stateMorphActive)
        {
            _stateMorphActive = false;
            MorphScaleTransform.ScaleX = 1.0;
            MorphScaleTransform.ScaleY = 1.0;
        }

        if (_positionGlideActive)
        {
            _liveOffsetXDip = _glideTargetOffsetXDip;
            _liveTopOffsetDip = _glideTargetTopOffsetDip;
            _positionGlideActive = false;
        }

        if (_liftAnimActive)
        {
            _currentLiftScale = _liftTargetScale;
            _currentShadowOpacity = _liftTargetShadowOpacity;
            _currentShadowBlur = _liftTargetShadowBlur;
            _currentShadowDepth = _liftTargetShadowDepth;
            _liftAnimActive = false;
        }

        ApplyResolvedPlacementFrame();
        UpdateShadowAndScaleVisuals();
        DetachMotionLoopIfIdle();
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

        _floatingProgress = _islandStyle == IslandStyle.FloatingPill ? 1.0 : 0.0;
        _styleMorphTarget = _floatingProgress;
        _liveOffsetXDip = _persistedOffsetXDip;
        _liveTopOffsetDip = IslandPositionMath.ResolveEffectiveTopOffsetDip(_islandStyle, _persistedFloatingTopOffsetDip);

        UpdateLiftTarget(immediate: true);
        ReanchorToTargetMonitor();
        ApplyVisualState(_stateManager.CurrentState, immediate: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachMotionLoop();
        StopAllTransientTimers();

        _inputFocusSubscription?.Dispose();
        _inputFocusSubscription = null;
        _notificationPeekContentSubscription?.Dispose();
        _notificationPeekContentSubscription = null;
        _notificationPeekDismissSubscription?.Dispose();
        _notificationPeekDismissSubscription = null;

        _stateManager.StateChanged -= OnShellStateChanged;
        _stateManager.CompactPillUpdated -= OnCompactPillUpdated;
        _displayTopologyMonitor.MonitorChanged -= OnMonitorChanged;
        _displayTopologyMonitor.DpiChanged -= OnDpiChanged;
        _fullscreenDetector.FullscreenStateChanged -= OnFullscreenStateChanged;
        _powerAndMotionMonitor.PowerProfileChanged -= OnPowerProfileChanged;
        _powerAndMotionMonitor.ReducedMotionChanged -= OnReducedMotionChanged;
        _powerAndMotionMonitor.SessionLockChanged -= OnSessionLockChanged;
        _globalHotkeyService.HotkeyPressed -= OnGlobalHotkeyPressed;

        _extraMessageHooks.Clear();
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
        void SyncBadge() => ActiveBadgeText.Text = _stateManager.CompactPillBadgeText ?? "Active";

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

    private void OnPowerProfileChanged(object? sender, PowerProfile profile) => EvaluateSystemSuspension();

    private void OnSessionLockChanged(object? sender, bool isLocked) => EvaluateSystemSuspension();

    private void OnReducedMotionChanged(object? sender, bool isReducedMotion)
    {
        void SyncMotion()
        {
            if (ActiveCompanionContentPresenter.Content is IReducedMotionAware aware)
            {
                aware.SetReducedMotion(isReducedMotion);
            }

            if (isReducedMotion)
            {
                ForceCompleteActiveMotionForTesting();
                if (_isCompanionAnimatingExit)
                {
                    FinalizeCompanionExit(clearContent: _targetCompanionContent is null);
                }
                else if (_stateManager.CurrentState == IslandShellState.Active && _targetCompanionContent is not null)
                {
                    AnimateCompanionEnter(immediate: true);
                }
            }
        }

        if (Dispatcher.CheckAccess())
        {
            SyncMotion();
        }
        else
        {
            Dispatcher.BeginInvoke(SyncMotion);
        }
    }

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
        if (nextState != IslandShellState.Expanded)
        {
            EndInteractiveInput();
        }

        if (nextState is IslandShellState.Fullscreen or IslandShellState.Suspended)
        {
            StopAllTransientTimers();
            _styleMorphActive = false;
            _stateMorphActive = false;
            _positionGlideActive = false;
            _liftAnimActive = false;
            DetachMotionLoop();
            RestoreClickThroughImmediate();

            if (_targetCompanionContent is null || ActiveCompanionHost.Visibility == Visibility.Visible)
            {
                FinalizeCompanionExit(clearContent: _targetCompanionContent is null);
            }

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

        var reducedMotion = immediate || _powerAndMotionMonitor.IsReducedMotionEnabled;
        var previousSpec = _currentVisualSpec;
        var previousVisualRect = _currentVisualBoundsDip.Width > 0
            ? _currentVisualBoundsDip
            : ResolveTargetPlacementForSpec(previousSpec, _floatingProgress).VisualRectDip;

        var targetSpec = ConcaveNotchGeometryBuilder.GetSpecForState(nextState);
        var isExpanding = targetSpec.WidthDip >= previousSpec.WidthDip && targetSpec.HeightDip >= previousSpec.HeightDip;
        _currentVisualSpec = targetSpec;

        if (_hwnd != 0)
        {
            _overlayWindowAdapter.ShowOverlay(_hwnd);
        }

        UpdateLayerVisibility(nextState);

        // Update bottom-edge companion slim bar cleanly for target shell state
        if (nextState == IslandShellState.Active && _targetCompanionContent is not null)
        {
            AnimateCompanionEnter(reducedMotion);
        }
        else if (ActiveCompanionHost.Visibility == Visibility.Visible || _isCompanionRenderedVisible || _isCompanionAnimatingExit)
        {
            AnimateCompanionExit(
                clearContentOnComplete: _targetCompanionContent is null,
                immediate: reducedMotion);
        }

        _renderedState = nextState;

        var targetPlacement = ResolveTargetPlacementForSpec(targetSpec, _floatingProgress);

        if (reducedMotion || previousSpec == targetSpec)
        {
            _stateMorphActive = false;
            MorphScaleTransform.ScaleX = 1.0;
            MorphScaleTransform.ScaleY = 1.0;
            ApplyResolvedPlacementFrame();
            DetachMotionLoopIfIdle();
        }
        else
        {
            _stateStartVisualRect = previousVisualRect;
            _stateTargetVisualRect = targetPlacement.VisualRectDip;
            _stateMorphDurationMs = isExpanding ? 220.0 : 180.0;
            _stateMorphStartTimestamp = Stopwatch.GetTimestamp();
            _stateMorphActive = true;

            ApplyStateTransitionFrame(0.0);
            EnsureMotionLoopAttached();
        }

        UpdateLiftTarget(immediate: reducedMotion);
        _logger?.Debug($"Island rendered state {nextState} ({targetSpec.WidthDip}x{targetSpec.HeightDip} DIP).");
    }

    private void TransitionStyleAndPosition(bool immediate)
    {
        var targetFloating = _islandStyle == IslandStyle.FloatingPill ? 1.0 : 0.0;
        var targetTopOffset = IslandPositionMath.ResolveEffectiveTopOffsetDip(_islandStyle, _persistedFloatingTopOffsetDip);

        if (immediate || _powerAndMotionMonitor.IsReducedMotionEnabled)
        {
            _styleMorphActive = false;
            _floatingProgress = targetFloating;
            _styleMorphTarget = targetFloating;

            _positionGlideActive = false;
            _liveOffsetXDip = _persistedOffsetXDip;
            _liveTopOffsetDip = targetTopOffset;

            UpdateLiftTarget(immediate: true);
            ApplyResolvedPlacementFrame();
            DetachMotionLoopIfIdle();
            return;
        }

        if (Math.Abs(_floatingProgress - targetFloating) > 0.001)
        {
            _styleMorphStart = _floatingProgress;
            _styleMorphTarget = targetFloating;
            _styleMorphStartTimestamp = Stopwatch.GetTimestamp();
            _styleMorphActive = true;
        }

        StartPositionGlide(_persistedOffsetXDip, targetTopOffset, StyleMorphDurationMs, immediate: false);
        UpdateLiftTarget(immediate: false);
        EnsureMotionLoopAttached();
    }

    private void StartPositionGlide(double targetOffsetXDip, double targetTopOffsetDip, double durationMs, bool immediate)
    {
        if (immediate || _powerAndMotionMonitor.IsReducedMotionEnabled)
        {
            _positionGlideActive = false;
            _liveOffsetXDip = targetOffsetXDip;
            _liveTopOffsetDip = targetTopOffsetDip;
            ApplyResolvedPlacementFrame();
            DetachMotionLoopIfIdle();
            return;
        }

        if (Math.Abs(_liveOffsetXDip - targetOffsetXDip) <= 0.25
            && Math.Abs(_liveTopOffsetDip - targetTopOffsetDip) <= 0.25)
        {
            _positionGlideActive = false;
            _liveOffsetXDip = targetOffsetXDip;
            _liveTopOffsetDip = targetTopOffsetDip;
            ApplyResolvedPlacementFrame();
            DetachMotionLoopIfIdle();
            return;
        }

        _glideStartOffsetXDip = _liveOffsetXDip;
        _glideStartTopOffsetDip = _liveTopOffsetDip;
        _glideTargetOffsetXDip = targetOffsetXDip;
        _glideTargetTopOffsetDip = targetTopOffsetDip;
        _glideDurationMs = Math.Max(1.0, durationMs);
        _glideStartTimestamp = Stopwatch.GetTimestamp();
        _positionGlideActive = true;

        EnsureMotionLoopAttached();
    }

    private void UpdateLiftTarget(bool immediate)
    {
        double targetScale;
        double targetShadowOpacity;
        double targetBlur;
        double targetDepth;

        if (_isDragging)
        {
            targetScale = DragLiftScale;
            targetShadowOpacity = _islandStyle == IslandStyle.FloatingPill ? DragShadowOpacity : 0.30;
            targetBlur = 16.0;
            targetDepth = 5.0;
        }
        else if (_islandStyle == IslandStyle.FloatingPill || _floatingProgress > 0.05)
        {
            targetScale = _isPointerHoveringSurface ? FloatingHoverLiftScale : 1.0;
            targetShadowOpacity = _isPointerHoveringSurface ? FloatingHoverShadowOpacity : FloatingRestingShadowOpacity;
            targetBlur = _isPointerHoveringSurface ? 15.0 : 12.0;
            targetDepth = _isPointerHoveringSurface ? 4.0 : 3.0;
        }
        else
        {
            targetScale = 1.0;
            targetShadowOpacity = 0.0;
            targetBlur = 12.0;
            targetDepth = 3.0;
        }

        if (immediate || _powerAndMotionMonitor.IsReducedMotionEnabled)
        {
            _liftAnimActive = false;
            _currentLiftScale = targetScale;
            _currentShadowOpacity = targetShadowOpacity;
            _currentShadowBlur = targetBlur;
            _currentShadowDepth = targetDepth;
            _liftTargetScale = targetScale;
            _liftTargetShadowOpacity = targetShadowOpacity;
            _liftTargetShadowBlur = targetBlur;
            _liftTargetShadowDepth = targetDepth;
            UpdateShadowAndScaleVisuals();
            ApplyResolvedPlacementFrame();
            DetachMotionLoopIfIdle();
            return;
        }

        if (Math.Abs(_currentLiftScale - targetScale) <= 0.0005
            && Math.Abs(_currentShadowOpacity - targetShadowOpacity) <= 0.002)
        {
            return;
        }

        _liftStartScale = _currentLiftScale;
        _liftStartShadowOpacity = _currentShadowOpacity;
        _liftStartShadowBlur = _currentShadowBlur;
        _liftStartShadowDepth = _currentShadowDepth;

        _liftTargetScale = targetScale;
        _liftTargetShadowOpacity = targetShadowOpacity;
        _liftTargetShadowBlur = targetBlur;
        _liftTargetShadowDepth = targetDepth;

        _liftDurationMs = _isDragging ? 120.0 : LiftDurationMs;
        _liftStartTimestamp = Stopwatch.GetTimestamp();
        _liftAnimActive = true;

        EnsureMotionLoopAttached();
    }

    private void AnimateCompanionEnter(bool immediate)
    {
        var generation = ++_companionTransitionGeneration;
        _isCompanionAnimatingExit = false;
        _isCompanionRenderedVisible = true;

        var currentlyVisible = ActiveCompanionHost.Visibility == Visibility.Visible && ActiveCompanionHost.Opacity > 0.01;
        var fromOpacity = currentlyVisible ? ActiveCompanionHost.Opacity : 0.0;
        var fromScaleY = currentlyVisible ? ActiveCompanionScaleTransform.ScaleY : 0.2;

        ActiveCompanionHost.BeginAnimation(OpacityProperty, null);
        ActiveCompanionScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        ActiveCompanionHost.Visibility = Visibility.Visible;

        if (immediate)
        {
            ActiveCompanionHost.Opacity = 1.0;
            ActiveCompanionScaleTransform.ScaleY = 1.0;
            return;
        }

        ActiveCompanionHost.Opacity = fromOpacity;
        ActiveCompanionScaleTransform.ScaleY = fromScaleY;

        var duration = TimeSpan.FromMilliseconds(CompanionEnterDurationMs);
        var fadeAnim = new DoubleAnimation(fromOpacity, 1.0, duration) { EasingFunction = _cubicEaseOut };
        var scaleAnim = new DoubleAnimation(fromScaleY, 1.0, duration) { EasingFunction = _cubicEaseOut };

        fadeAnim.Completed += (_, _) =>
        {
            if (_companionTransitionGeneration != generation || !_isCompanionRenderedVisible)
            {
                return;
            }

            ActiveCompanionHost.BeginAnimation(OpacityProperty, null);
            ActiveCompanionScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ActiveCompanionHost.Opacity = 1.0;
            ActiveCompanionScaleTransform.ScaleY = 1.0;
        };

        ActiveCompanionHost.BeginAnimation(OpacityProperty, fadeAnim);
        ActiveCompanionScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private void AnimateCompanionExit(bool clearContentOnComplete, bool immediate)
    {
        var generation = ++_companionTransitionGeneration;
        _isCompanionRenderedVisible = false;

        if (immediate || ActiveCompanionHost.Visibility != Visibility.Visible || ActiveCompanionHost.Opacity <= 0.01)
        {
            FinalizeCompanionExit(clearContentOnComplete);
            return;
        }

        _isCompanionAnimatingExit = true;

        var fromOpacity = ActiveCompanionHost.Opacity;
        var fromScaleY = ActiveCompanionScaleTransform.ScaleY;

        ActiveCompanionHost.BeginAnimation(OpacityProperty, null);
        ActiveCompanionScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        ActiveCompanionHost.Opacity = fromOpacity;
        ActiveCompanionScaleTransform.ScaleY = fromScaleY;

        var duration = TimeSpan.FromMilliseconds(CompanionExitDurationMs);
        var fadeAnim = new DoubleAnimation(fromOpacity, 0.0, duration) { EasingFunction = _cubicEaseOut };
        var scaleAnim = new DoubleAnimation(fromScaleY, 0.2, duration) { EasingFunction = _cubicEaseOut };

        fadeAnim.Completed += (_, _) =>
        {
            if (_companionTransitionGeneration != generation || _isCompanionRenderedVisible)
            {
                return;
            }

            FinalizeCompanionExit(clearContentOnComplete);
        };

        ActiveCompanionHost.BeginAnimation(OpacityProperty, fadeAnim);
        ActiveCompanionScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private void FinalizeCompanionExit(bool clearContent)
    {
        _isCompanionAnimatingExit = false;
        _isCompanionRenderedVisible = false;

        ActiveCompanionHost.BeginAnimation(OpacityProperty, null);
        ActiveCompanionScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        ActiveCompanionHost.Visibility = Visibility.Collapsed;
        ActiveCompanionHost.Opacity = 0.0;
        ActiveCompanionScaleTransform.ScaleY = 0.2;

        if (clearContent)
        {
            ActiveCompanionContentPresenter.Content = null;
        }
    }

    private void EnsureMotionLoopAttached()
    {
        if (_motionHookAttached)
        {
            return;
        }

        _motionHookAttached = true;
        CompositionTarget.Rendering += OnCompositionTargetRendering;
    }

    private void DetachMotionLoopIfIdle()
    {
        if (!_styleMorphActive && !_stateMorphActive && !_positionGlideActive && !_liftAnimActive)
        {
            DetachMotionLoop();
        }
    }

    private void DetachMotionLoop()
    {
        if (!_motionHookAttached)
        {
            return;
        }

        _motionHookAttached = false;
        CompositionTarget.Rendering -= OnCompositionTargetRendering;
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();

        if (_styleMorphActive)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(_styleMorphStartTimestamp, now).TotalMilliseconds;
            var rawT = Math.Clamp(elapsedMs / StyleMorphDurationMs, 0.0, 1.0);
            var eased = _cubicEaseOut.Ease(rawT);
            _floatingProgress = IslandPositionMath.Lerp(_styleMorphStart, _styleMorphTarget, eased);
            if (rawT >= 1.0)
            {
                _floatingProgress = _styleMorphTarget;
                _styleMorphActive = false;
            }
        }

        if (_positionGlideActive)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(_glideStartTimestamp, now).TotalMilliseconds;
            var rawT = Math.Clamp(elapsedMs / _glideDurationMs, 0.0, 1.0);
            var eased = _cubicEaseOut.Ease(rawT);
            _liveOffsetXDip = IslandPositionMath.Lerp(_glideStartOffsetXDip, _glideTargetOffsetXDip, eased);
            _liveTopOffsetDip = IslandPositionMath.Lerp(_glideStartTopOffsetDip, _glideTargetTopOffsetDip, eased);
            if (rawT >= 1.0)
            {
                _liveOffsetXDip = _glideTargetOffsetXDip;
                _liveTopOffsetDip = _glideTargetTopOffsetDip;
                _positionGlideActive = false;
            }
        }

        if (_liftAnimActive)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(_liftStartTimestamp, now).TotalMilliseconds;
            var rawT = Math.Clamp(elapsedMs / _liftDurationMs, 0.0, 1.0);
            var eased = _cubicEaseOut.Ease(rawT);
            _currentLiftScale = IslandPositionMath.Lerp(_liftStartScale, _liftTargetScale, eased);
            _currentShadowOpacity = IslandPositionMath.Lerp(_liftStartShadowOpacity, _liftTargetShadowOpacity, eased);
            _currentShadowBlur = IslandPositionMath.Lerp(_liftStartShadowBlur, _liftTargetShadowBlur, eased);
            _currentShadowDepth = IslandPositionMath.Lerp(_liftStartShadowDepth, _liftTargetShadowDepth, eased);
            if (rawT >= 1.0)
            {
                _currentLiftScale = _liftTargetScale;
                _currentShadowOpacity = _liftTargetShadowOpacity;
                _currentShadowBlur = _liftTargetShadowBlur;
                _currentShadowDepth = _liftTargetShadowDepth;
                _liftAnimActive = false;
            }
        }

        UpdateShadowAndScaleVisuals();

        if (_stateMorphActive)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(_stateMorphStartTimestamp, now).TotalMilliseconds;
            var rawT = Math.Clamp(elapsedMs / _stateMorphDurationMs, 0.0, 1.0);
            var eased = _cubicEaseOut.Ease(rawT);
            ApplyStateTransitionFrame(eased);
            if (rawT >= 1.0)
            {
                _stateMorphActive = false;
                MorphScaleTransform.ScaleX = 1.0;
                MorphScaleTransform.ScaleY = 1.0;
                ApplyResolvedPlacementFrame();
            }
        }
        else
        {
            ApplyResolvedPlacementFrame();
        }

        DetachMotionLoopIfIdle();
    }

    private void ApplyStateTransitionFrame(double easedProgress)
    {
        var spec = _currentVisualSpec;
        UpdateSurfaceGeometry(spec);

        var padding = ResolveCurrentPadding(spec);
        var targetPlacement = ResolveTargetPlacementForSpec(spec, _floatingProgress);
        _stateTargetVisualRect = targetPlacement.VisualRectDip;

        var fromScaleX = Math.Clamp(_stateStartVisualRect.Width / Math.Max(1.0, _stateTargetVisualRect.Width), 0.25, 2.0);
        var fromScaleY = Math.Clamp(_stateStartVisualRect.Height / Math.Max(1.0, _stateTargetVisualRect.Height), 0.25, 2.0);
        var scaleX = IslandPositionMath.Lerp(fromScaleX, 1.0, easedProgress);
        var scaleY = IslandPositionMath.Lerp(fromScaleY, 1.0, easedProgress);

        MorphScaleTransform.CenterX = spec.WidthDip * 0.5;
        MorphScaleTransform.CenterY = 0.0;
        MorphScaleTransform.ScaleX = scaleX;
        MorphScaleTransform.ScaleY = scaleY;

        var (earInset, _, _) = IslandPositionMath.GetVisualDimensions(
            spec.WidthDip,
            spec.HeightDip,
            spec.EarRadiusDip,
            _floatingProgress);

        var windowW = spec.WidthDip + padding.Left + padding.Right;
        var windowH = spec.HeightDip + padding.Top + padding.Bottom;

        var frameWindowRect = IslandPositionMath.InterpolateSurfaceTransitionWindowRect(
            _stateStartVisualRect,
            _stateTargetVisualRect,
            spec.WidthDip,
            spec.HeightDip,
            earInset,
            padding.Left,
            padding.Top,
            windowW,
            windowH,
            transformOriginX: 0.5,
            transformOriginY: 0.0,
            easedProgress);

        _currentVisualBoundsDip = new DipRect(
            IslandPositionMath.Lerp(_stateStartVisualRect.Left, _stateTargetVisualRect.Left, easedProgress),
            IslandPositionMath.Lerp(_stateStartVisualRect.Top, _stateTargetVisualRect.Top, easedProgress),
            IslandPositionMath.Lerp(_stateStartVisualRect.Width, _stateTargetVisualRect.Width, easedProgress),
            IslandPositionMath.Lerp(_stateStartVisualRect.Height, _stateTargetVisualRect.Height, easedProgress));

        CommitWindowLayoutAndPixelRect(spec, padding, frameWindowRect, precomputedPixelRect: null);
    }

    private void ApplyResolvedPlacementFrame(bool allowOverscrollDuringDrag = false)
    {
        var spec = _currentVisualSpec;
        UpdateSurfaceGeometry(spec);

        var padding = ResolveCurrentPadding(spec);
        var workArea = IslandPositionMath.GetEffectiveWorkArea(_currentMonitor);
        var scale = IslandPositionMath.GetEffectiveDpiScale(_currentMonitor.DpiScale);

        IslandWorkAreaPlacement placement;
        if (allowOverscrollDuringDrag)
        {
            var (_, visualW, visualH) = IslandPositionMath.GetVisualDimensions(
                spec.WidthDip,
                spec.HeightDip,
                spec.EarRadiusDip,
                _floatingProgress);

            var workLeftDip = workArea.Left / scale;
            var workTopDip = workArea.Top / scale;
            var workCenterX = workLeftDip + (workArea.Width / scale) / 2.0;

            var visualLeft = workCenterX + _liveOffsetXDip - visualW / 2.0;
            var visualTop = workTopDip + (_islandStyle == IslandStyle.DockedNotch ? 0.0 : _liveTopOffsetDip);
            var insetLeft = spec.EarRadiusDip * _floatingProgress;
            var windowRect = new DipRect(
                visualLeft - insetLeft - padding.Left,
                visualTop - padding.Top,
                spec.WidthDip + padding.Left + padding.Right,
                spec.HeightDip + padding.Top + padding.Bottom);
            placement = new IslandWorkAreaPlacement(
                new DipRect(visualLeft, visualTop, visualW, visualH),
                windowRect,
                IslandPositionMath.DipRectToPixelRect(windowRect, scale),
                _liveOffsetXDip,
                _islandStyle == IslandStyle.DockedNotch ? 0.0 : _liveTopOffsetDip);
        }
        else
        {
            var extraBottom = GetExtraCompanionWorkAreaClearanceDip(spec);
            placement = IslandPositionMath.CalculatePlacement(
                workArea,
                scale,
                spec.WidthDip,
                spec.HeightDip,
                spec.EarRadiusDip,
                _floatingProgress,
                _liveOffsetXDip,
                _islandStyle == IslandStyle.DockedNotch ? 0.0 : _liveTopOffsetDip,
                padding.Left,
                padding.Top,
                padding.Right,
                padding.Bottom,
                extraBottom);
        }

        _currentVisualBoundsDip = placement.VisualRectDip;
        CommitWindowLayoutAndPixelRect(
            spec,
            padding,
            placement.WindowRectDip,
            allowOverscrollDuringDrag ? null : placement.PixelRect);
    }

    private IslandWorkAreaPlacement ResolveTargetPlacementForSpec(IslandVisualSpec spec, double floatingProgress)
    {
        var workArea = IslandPositionMath.GetEffectiveWorkArea(_currentMonitor);
        var padding = ResolveCurrentPadding(spec);
        var extraBottom = GetExtraCompanionWorkAreaClearanceDip(spec);

        return IslandPositionMath.CalculatePlacement(
            workArea,
            _currentMonitor.DpiScale,
            spec.WidthDip,
            spec.HeightDip,
            spec.EarRadiusDip,
            floatingProgress,
            _liveOffsetXDip,
            _islandStyle == IslandStyle.DockedNotch ? 0.0 : _liveTopOffsetDip,
            padding.Left,
            padding.Top,
            padding.Right,
            padding.Bottom,
            extraBottom);
    }

    private double GetExtraCompanionWorkAreaClearanceDip(IslandVisualSpec spec) => 0.0;

    private Thickness ResolveCurrentPadding(IslandVisualSpec spec)
    {
        var needsShadowPad = _islandStyle == IslandStyle.FloatingPill
            || _floatingProgress > 0.001
            || _styleMorphActive
            || _isDragging
            || _currentShadowOpacity > 0.002
            || _liftAnimActive;

        var padLeft = needsShadowPad ? FloatingPadSideDip : 0.0;
        var padTop = needsShadowPad ? FloatingPadTopDip : 0.0;
        var padRight = needsShadowPad ? FloatingPadSideDip : 0.0;
        var padBottom = needsShadowPad ? FloatingPadBottomDip : 0.0;

        return new Thickness(padLeft, padTop, padRight, padBottom);
    }

    private void UpdateSurfaceGeometry(IslandVisualSpec spec)
    {
        var geometry = ConcaveNotchGeometryBuilder.Build(spec, _floatingProgress);
        NotchSurfacePath.Data = geometry;
        IslandShadowPath.Data = geometry;
    }

    private void UpdateShadowAndScaleVisuals()
    {
        MorphScaleTransform.CenterX = _currentVisualSpec.WidthDip * 0.5;
        MorphScaleTransform.CenterY = 0.0;

        LiftScaleTransform.CenterX = _currentVisualSpec.WidthDip * 0.5;
        LiftScaleTransform.CenterY = _currentVisualSpec.HeightDip * 0.5;
        LiftScaleTransform.ScaleX = _currentLiftScale;
        LiftScaleTransform.ScaleY = _currentLiftScale;

        if (_currentShadowOpacity <= 0.002 && !_liftAnimActive && !_styleMorphActive && !_isDragging && _islandStyle == IslandStyle.DockedNotch)
        {
            IslandShadowPath.Effect = null;
            IslandShadowPath.Visibility = Visibility.Collapsed;
            IslandShadowPath.Opacity = 0.0;
        }
        else
        {
            _surfaceDropShadow ??= CreateThemeDropShadow(_currentShadowOpacity, _currentShadowBlur, _currentShadowDepth);
            _surfaceDropShadow.Opacity = Math.Clamp(_currentShadowOpacity, 0.0, 1.0);
            _surfaceDropShadow.BlurRadius = _currentShadowBlur;
            _surfaceDropShadow.ShadowDepth = _currentShadowDepth;
            if (!ReferenceEquals(IslandShadowPath.Effect, _surfaceDropShadow))
            {
                IslandShadowPath.Effect = _surfaceDropShadow;
            }

            IslandShadowPath.Opacity = 1.0;
            IslandShadowPath.Visibility = Visibility.Visible;
        }
    }

    private DropShadowEffect CreateThemeDropShadow(double opacity, double blur, double depth)
    {
        var shadowColor = TryFindResource("DropShadowColor") is Color tokenColor
            ? tokenColor
            : Colors.Black;

        return new DropShadowEffect
        {
            Color = shadowColor,
            Direction = 270,
            ShadowDepth = depth,
            BlurRadius = blur,
            Opacity = Math.Clamp(opacity, 0.0, 1.0)
        };
    }

    private void CommitWindowLayoutAndPixelRect(
        IslandVisualSpec spec,
        Thickness padding,
        DipRect windowRectDip,
        MonitorBounds? precomputedPixelRect)
    {
        _currentPadLeftDip = padding.Left;
        _currentPadTopDip = padding.Top;
        _currentPadRightDip = padding.Right;
        _currentPadBottomDip = padding.Bottom;
        _currentWindowBoundsDip = windowRectDip;

        if (Math.Abs(RootContainer.Margin.Left - padding.Left) > 0.01
            || Math.Abs(RootContainer.Margin.Top - padding.Top) > 0.01
            || Math.Abs(RootContainer.Margin.Right - padding.Right) > 0.01)
        {
            RootContainer.Margin = new Thickness(padding.Left, padding.Top, padding.Right, 0);
        }

        if (Math.Abs(RootContainer.Width - spec.WidthDip) > 0.01)
        {
            RootContainer.Width = spec.WidthDip;
        }

        if (Math.Abs(MainIslandSurfaceHost.Width - spec.WidthDip) > 0.01)
        {
            MainIslandSurfaceHost.Width = spec.WidthDip;
        }

        if (Math.Abs(MainIslandSurfaceHost.Height - spec.HeightDip) > 0.01)
        {
            MainIslandSurfaceHost.Height = spec.HeightDip;
        }

        if (Math.Abs(Width - windowRectDip.Width) > 0.01)
        {
            Width = windowRectDip.Width;
        }

        if (Math.Abs(Height - windowRectDip.Height) > 0.01)
        {
            Height = windowRectDip.Height;
        }

        var pixelRect = precomputedPixelRect ?? IslandPositionMath.DipRectToPixelRect(
            windowRectDip,
            _currentMonitor.DpiScale);
        _currentWindowPixelRect = pixelRect;

        if (_hwnd != 0)
        {
            _overlayWindowAdapter.SetPixelRect(_hwnd, pixelRect);
        }
    }

    private void UpdateLayerVisibility(IslandShellState state)
    {
        ActiveBadgeText.Text = _stateManager.CompactPillBadgeText ?? "Active";

        ActiveLayer.Visibility = state == IslandShellState.Active ? Visibility.Visible : Visibility.Collapsed;
        NotificationLayer.Visibility = state == IslandShellState.Notification ? Visibility.Visible : Visibility.Collapsed;
        ExpandedLayer.Visibility = state == IslandShellState.Expanded ? Visibility.Visible : Visibility.Collapsed;
        UpdateCollapsedOverlayVisibility(state);

        var surfaceBrushKey = state is IslandShellState.Expanded or IslandShellState.Notification
            ? "SurfaceExpanded"
            : "SurfaceCollapsed";
        NotchSurfacePath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, surfaceBrushKey);
        IslandShadowPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, surfaceBrushKey);
    }

    private void UpdateCollapsedOverlayVisibility(IslandShellState state)
    {
        CollapsedOverlayContentPresenter.Visibility = state == IslandShellState.Collapsed
            && CollapsedOverlayContentPresenter.Content is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void ConfigurePeekActionButton(System.Windows.Controls.Button button, NotificationPeekAction? action)
    {
        if (action is null)
        {
            button.Visibility = Visibility.Collapsed;
            button.Content = null;
            button.IsEnabled = true;
            return;
        }

        button.Content = action.Label;
        AutomationProperties.SetName(button, action.AutomationName);
        button.IsEnabled = true;
        button.Visibility = Visibility.Visible;
    }

    private void ReanchorCurrentSpec()
    {
        if (_renderedState is IslandShellState.Fullscreen or IslandShellState.Suspended)
        {
            return;
        }

        ApplyResolvedPlacementFrame();
    }

    private void SyncFullscreenForCurrentMonitor() =>
        _stateManager.SetFullscreenOnHostMonitor(_fullscreenDetector.IsMonitorFullscreen(_currentMonitor.Handle));

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            return;
        }

        UpdatePointerSurfaceHoverState(IsPointOverVisibleSurface(e.GetPosition(this)));
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            return;
        }

        var overSurface = IsPointOverVisibleSurface(e.GetPosition(this));
        UpdatePointerSurfaceHoverState(overSurface);

        if (overSurface && TryActivateAltClickThrough())
        {
            _hoverExpandTimer.Stop();
        }
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        _hoverExpandTimer.Stop();
        UpdatePointerSurfaceHoverState(overSurface: false);

        if (_isDragging)
        {
            return;
        }

        if (_stateManager.CurrentState is IslandShellState.Expanded or IslandShellState.Notification
            && !_pinController.IsPinned)
        {
            _autoCollapseTimer.Stop();
            _autoCollapseTimer.Interval = TimeSpan.FromMilliseconds(_autoCollapseDelayMs);
            _autoCollapseTimer.Start();
        }
    }

    private void UpdatePointerSurfaceHoverState(bool overSurface)
    {
        if (!overSurface)
        {
            _hoverExpandTimer.Stop();
            SetPointerHoveringSurface(false);
            return;
        }

        _autoCollapseTimer.Stop();

        if (TryActivateAltClickThrough())
        {
            SetPointerHoveringSurface(false);
            return;
        }

        var newlyEntered = !_isPointerHoveringSurface;
        SetPointerHoveringSurface(true);

        if (newlyEntered && _stateManager.CurrentState is IslandShellState.Collapsed or IslandShellState.Active)
        {
            _eventDispatcher?.Publish(new IslandCollapsedHoveredEvent(DateTimeOffset.UtcNow));
        }

        if (newlyEntered && _expandOnHover && _stateManager.CurrentState is IslandShellState.Collapsed or IslandShellState.Active)
        {
            _hoverExpandTimer.Stop();
            _hoverExpandTimer.Interval = TimeSpan.FromMilliseconds(_hoverExpandDelayMs);
            _hoverExpandTimer.Start();
        }
    }

    private void SetPointerHoveringSurface(bool hovering)
    {
        if (_isPointerHoveringSurface == hovering)
        {
            return;
        }

        _isPointerHoveringSurface = hovering;
        UpdateLiftTarget(immediate: _powerAndMotionMonitor.IsReducedMotionEnabled);
    }

    private void OnWindowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragHandledClickSuppression = false;
        if (e.ChangedButton != MouseButton.Left
            || _lockIslandPosition
            || _isClickThroughActive
            || (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            return;
        }

        if (!IsPointOverVisibleSurface(e.GetPosition(this)) || !CanInitiateDragFromSource(e.OriginalSource))
        {
            return;
        }

        if (_positionGlideActive)
        {
            _positionGlideActive = false;
            _persistedOffsetXDip = _liveOffsetXDip;
            _persistedFloatingTopOffsetDip = _islandStyle == IslandStyle.FloatingPill ? _liveTopOffsetDip : _persistedFloatingTopOffsetDip;
        }

        _dragArmed = true;
        _isDragging = false;
        _dragStartCursorScreenPx = SampleCursorScreenPosition(e.GetPosition(this));
        _dragStartOffsetXDip = _liveOffsetXDip;
        _dragStartTopOffsetDip = _islandStyle == IslandStyle.FloatingPill
            ? _liveTopOffsetDip
            : 0.0;

        if (_renderedState is IslandShellState.Collapsed or IslandShellState.Active)
        {
            CaptureMouse();
        }
    }

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishArmedOrActiveDrag(commit: _isDragging);
            return;
        }

        var currentScreenPx = SampleCursorScreenPosition(e.GetPosition(this));
        var scale = IslandPositionMath.GetEffectiveDpiScale(_currentMonitor.DpiScale);
        var dxDip = (currentScreenPx.X - _dragStartCursorScreenPx.X) / scale;
        var dyDip = (currentScreenPx.Y - _dragStartCursorScreenPx.Y) / scale;

        ApplyDragDeltaDip(dxDip, dyDip);
        if (_isDragging)
        {
            e.Handled = true;
        }
    }

    private void OnWindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragArmed && !_isDragging)
        {
            return;
        }

        var wasDragging = _isDragging;
        FinishArmedOrActiveDrag(commit: wasDragging);
        if (wasDragging)
        {
            _dragHandledClickSuppression = true;
            e.Handled = true;
        }
    }

    private void OnWindowLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragArmed || _isDragging)
        {
            FinishArmedOrActiveDrag(commit: _isDragging);
        }
    }

    private void ApplyDragDeltaDip(double dxDip, double dyDip, bool capturePointer = true)
    {
        var newlyStartedDragging = false;
        if (!_isDragging)
        {
            var distance = Math.Sqrt(dxDip * dxDip + dyDip * dyDip);
            if (distance < IslandPositionMath.DragThresholdDip)
            {
                return;
            }

            _isDragging = true;
            newlyStartedDragging = true;
            _hoverExpandTimer.Stop();
            _autoCollapseTimer.Stop();
            UpdateLiftTarget(immediate: _powerAndMotionMonitor.IsReducedMotionEnabled);
        }

        var workArea = IslandPositionMath.GetEffectiveWorkArea(_currentMonitor);
        var scale = IslandPositionMath.GetEffectiveDpiScale(_currentMonitor.DpiScale);
        var (_, visualW, visualH) = IslandPositionMath.GetVisualDimensions(
            _currentVisualSpec.WidthDip,
            _currentVisualSpec.HeightDip,
            _currentVisualSpec.EarRadiusDip,
            _floatingProgress);

        var workWidthDip = Math.Max(visualW, workArea.Width / scale);
        var workHeightDip = Math.Max(visualH, workArea.Height / scale);
        var extraBottom = GetExtraCompanionWorkAreaClearanceDip(_currentVisualSpec);

        var maxOffsetX = Math.Max(0.0, (workWidthDip - visualW) * 0.5);
        var minOffsetX = -maxOffsetX;
        var rawOffsetX = _dragStartOffsetXDip + dxDip;
        _liveOffsetXDip = ApplySoftEdgeClamp(rawOffsetX, minOffsetX, maxOffsetX);

        if (_islandStyle == IslandStyle.FloatingPill)
        {
            var maxTop = Math.Max(0.0, workHeightDip - visualH - extraBottom);
            var rawTop = _dragStartTopOffsetDip + dyDip;
            _liveTopOffsetDip = ApplySoftEdgeClamp(rawTop, 0.0, maxTop);
        }
        else
        {
            _liveTopOffsetDip = 0.0;
        }

        ApplyResolvedPlacementFrame(allowOverscrollDuringDrag: !_powerAndMotionMonitor.IsReducedMotionEnabled);

        if (newlyStartedDragging && capturePointer && !IsMouseCaptured)
        {
            CaptureMouse();
        }
    }

    private double ApplySoftEdgeClamp(double rawValue, double minValue, double maxValue)
    {
        if (_powerAndMotionMonitor.IsReducedMotionEnabled)
        {
            return Math.Clamp(rawValue, minValue, maxValue);
        }

        if (rawValue < minValue)
        {
            var overscroll = Math.Min(DragOverscrollCapDip, (minValue - rawValue) * DragOverscrollDamping);
            return minValue - overscroll;
        }

        if (rawValue > maxValue)
        {
            var overscroll = Math.Min(DragOverscrollCapDip, (rawValue - maxValue) * DragOverscrollDamping);
            return maxValue + overscroll;
        }

        return rawValue;
    }

    private void FinishArmedOrActiveDrag(bool commit)
    {
        var shouldCommit = commit && _isDragging;

        // Clear drag flags before releasing capture so synchronous LostMouseCapture
        // never re-enters FinishArmedOrActiveDrag or emits a duplicate PositionCommitted.
        _dragArmed = false;
        _isDragging = false;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (shouldCommit)
        {
            CompleteDrag(commitAndGlide: true);
        }
    }

    private void CompleteDrag(bool commitAndGlide)
    {
        _isDragging = false;
        var reducedMotion = _powerAndMotionMonitor.IsReducedMotionEnabled;
        UpdateLiftTarget(immediate: reducedMotion);

        if (!commitAndGlide)
        {
            return;
        }

        var workArea = IslandPositionMath.GetEffectiveWorkArea(_currentMonitor);
        var extraBottom = GetExtraCompanionWorkAreaClearanceDip(_currentVisualSpec);
        var (resolvedOffsetX, resolvedFloatingTop, _) = IslandPositionMath.ResolveDropTarget(
            workArea,
            _currentMonitor.DpiScale,
            _currentVisualSpec.WidthDip,
            _currentVisualSpec.HeightDip,
            _currentVisualSpec.EarRadiusDip,
            _islandStyle,
            _liveOffsetXDip,
            _liveTopOffsetDip,
            extraBottom);

        _persistedOffsetXDip = resolvedOffsetX;
        if (_islandStyle == IslandStyle.FloatingPill)
        {
            _persistedFloatingTopOffsetDip = resolvedFloatingTop;
        }

        var targetTop = IslandPositionMath.ResolveEffectiveTopOffsetDip(_islandStyle, _persistedFloatingTopOffsetDip);
        StartPositionGlide(resolvedOffsetX, targetTop, PositionGlideDurationMs, immediate: reducedMotion);

        PositionCommitted?.Invoke(
            this,
            new IslandPositionCommittedEventArgs(_persistedOffsetXDip, _persistedFloatingTopOffsetDip));
    }

    private Point SampleCursorScreenPosition(Point windowDipPoint)
    {
        if (_overlayWindowAdapter.TryGetCursorScreenPosition(out var px, out var py))
        {
            return new Point(px, py);
        }

        var scale = IslandPositionMath.GetEffectiveDpiScale(_currentMonitor.DpiScale);
        return new Point(
            _currentWindowPixelRect.Left + windowDipPoint.X * scale,
            _currentWindowPixelRect.Top + windowDipPoint.Y * scale);
    }

    private bool IsPointOverVisibleSurface(Point windowDipPoint)
    {
        if (_currentVisualSpec.WidthDip <= 0 || _currentVisualSpec.HeightDip <= 0)
        {
            return false;
        }

        if (HitTestInteractiveSurfaceDip(windowDipPoint))
        {
            return true;
        }

        // Fallback before first Measure/Arrange render completes
        var xInMain = windowDipPoint.X - _currentPadLeftDip;
        var yInMain = windowDipPoint.Y - _currentPadTopDip;
        var (inset, visualW, visualH) = IslandPositionMath.GetVisualDimensions(
            _currentVisualSpec.WidthDip,
            _currentVisualSpec.HeightDip,
            _currentVisualSpec.EarRadiusDip,
            _floatingProgress);
        return xInMain >= inset && xInMain <= inset + visualW && yInMain >= 0 && yInMain <= visualH;
    }

    private static bool CanInitiateDragFromSource(object? originalSource)
    {
        for (var current = originalSource as DependencyObject; current is not null;)
        {
            if (current is ButtonBase
                or TextBoxBase
                or PasswordBox
                or RangeBase
                or Thumb
                or Selector
                or ScrollBar)
            {
                return false;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return true;
    }

    private void OnWindowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isClickThroughActive)
        {
            return;
        }

        if (_dragHandledClickSuppression)
        {
            _dragHandledClickSuppression = false;
            e.Handled = true;
            return;
        }

        if (!IsPointOverVisibleSurface(e.GetPosition(this)))
        {
            return;
        }

        if (_stateManager.CurrentState == IslandShellState.Notification)
        {
            _notificationPeekTimer.Stop();
            _stateManager.ClearNotificationPeek();
            _stateManager.RequestUserExpand();
            if (!string.IsNullOrWhiteSpace(_activeNotificationWidgetId))
            {
                WidgetHostControl.SelectWidget(_activeNotificationWidgetId);
            }

            return;
        }

        if (_stateManager.CurrentState is IslandShellState.Collapsed or IslandShellState.Active)
        {
            _hoverExpandTimer.Stop();
            _stateManager.RequestUserExpand();
        }
    }

    private void OnExpandedLayerPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var textInput = FindAncestorTextBox(e.OriginalSource);
        if (textInput is not null)
        {
            BeginInteractiveInput();
            if (!textInput.IsKeyboardFocused)
            {
                textInput.Focus();
            }
        }
        else if (_interactiveInputActive)
        {
            EndInteractiveInput();
        }
    }

    private void OnExpandedLayerGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (FindAncestorTextBox(e.NewFocus) is not null)
        {
            BeginInteractiveInput();
        }
    }

    private void OnExpandedLayerLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (FindAncestorTextBox(e.NewFocus) is null)
        {
            EndInteractiveInput();
        }
    }

    private static TextBoxBase? FindAncestorTextBox(object? source)
    {
        for (var current = source as DependencyObject; current is not null;)
        {
            if (current is TextBoxBase textBox)
            {
                return textBox;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
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

    private async void OnNotificationPrimaryActionClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await ExecuteNotificationPeekActionAsync(_primaryPeekAction).ConfigureAwait(true);
    }

    private async void OnNotificationSecondaryActionClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await ExecuteNotificationPeekActionAsync(_secondaryPeekAction).ConfigureAwait(true);
    }

    private async Task ExecuteNotificationPeekActionAsync(NotificationPeekAction? action)
    {
        if (action is null)
        {
            return;
        }

        NotificationPrimaryActionButton.IsEnabled = false;
        NotificationSecondaryActionButton.IsEnabled = false;
        try
        {
            await action.ExecuteAsync().ConfigureAwait(true);
        }
        finally
        {
            NotificationPrimaryActionButton.IsEnabled = true;
            NotificationSecondaryActionButton.IsEnabled = true;
        }
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
        if (_isClickThroughActive || _isDragging || (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            return;
        }

        _stateManager.RequestUserExpand();
    }

    private void OnAutoCollapseTimerTick(object? sender, EventArgs e)
    {
        _autoCollapseTimer.Stop();
        if (!_isDragging)
        {
            _stateManager.RequestUserCollapse();
        }
    }

    private void OnClickThroughRestoreTimerTick(object? sender, EventArgs e) => RestoreClickThroughImmediate();

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
        if (_extraMessageHooks.Count > 0)
        {
            foreach (var hook in _extraMessageHooks.ToArray())
            {
                if (hook(msg, wParam, lParam))
                {
                    handled = true;
                    return 0;
                }
            }
        }

        if (msg == WmNcHitTest)
        {
            var raw = lParam.ToInt64();
            var screenX = unchecked((short)(raw & 0xFFFF));
            var screenY = unchecked((short)((raw >> 16) & 0xFFFF));
            var scale = IslandPositionMath.GetEffectiveDpiScale(_currentMonitor.DpiScale);
            var dipX = (screenX - _currentWindowPixelRect.Left) / scale;
            var dipY = (screenY - _currentWindowPixelRect.Top) / scale;

            if (!HitTestInteractiveSurfaceDip(new Point(dipX, dipY)))
            {
                handled = true;
                return HtTransparent;
            }
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
