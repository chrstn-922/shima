using Microsoft.Win32;
using Shima.Core.Contracts;

namespace Shima.Widgets.Clock;

/// <summary>
/// Clock &amp; World Timezone widget module (shima.clock) with boundary-aligned refresh timer
/// that suspends completely while Collapsed, Fullscreen, or DisplayOff.
/// </summary>
public sealed class ClockWidgetModule : IWidgetModule
{
    public const string Id = "shima.clock";

    private readonly IClockRepository _repository;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly IClockTimer _timer;
    private readonly object _stateSync = new();

    private IWidgetContext? _context;
    private IDisposable? _timeChangeSubscription;
    private bool _systemEventsSubscribed;
    private bool _isActive;
    private bool _isDisplayOff;
    private IslandShellState _shellState = IslandShellState.Expanded;
    private bool _disposed;

    public ClockWidgetModule(
        IClockRepository repository,
        Func<DateTimeOffset>? utcNowProvider = null,
        IClockTimer? timer = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _timer = timer ?? new SystemClockTimer();
        ViewModel = new ClockWidgetViewModel(_repository, _utcNowProvider);
        ViewModel.SettingsChanged += (_, _) => EvaluateTimerLifecycle();
    }

    public WidgetDescriptor Metadata { get; } = new()
    {
        WidgetId = Id,
        DisplayName = "Clock",
        DefaultOrder = 0,
        RequiresBackgroundSubscription = false,
        SupportsCompactActiveState = false,
        SupportsNotificationPeek = false,
        DefaultAccentHex = "#FFFFFF"
    };

    public ClockWidgetViewModel ViewModel { get; }

    public bool IsTimerRunning => _timer.IsRunning;

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _context = context ?? throw new ArgumentNullException(nameof(context));

        await ViewModel.InitializeAsync(context, ct).ConfigureAwait(false);

        _timeChangeSubscription?.Dispose();
        _timeChangeSubscription = context.Events.Subscribe<SystemTimeChangedEvent>(_ => OnTimeEnvironmentChanged());

        if (!_systemEventsSubscribed)
        {
            SystemEvents.TimeChanged += OnSystemEventsTimeChanged;
            _systemEventsSubscribed = true;
        }
    }

    public Task ActivateAsync(WidgetActivationReason reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateSync)
        {
            _isActive = true;
            _shellState = IslandShellState.Expanded;
        }

        ViewModel.RefreshReadout();
        EvaluateTimerLifecycle();
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        lock (_stateSync)
        {
            _isActive = false;
        }

        _timer.Stop();
        return Task.CompletedTask;
    }

    public void OnShellStateChanged(IslandShellState newState)
    {
        lock (_stateSync)
        {
            _shellState = newState;
        }

        if (newState is IslandShellState.Collapsed or IslandShellState.Fullscreen or IslandShellState.Suspended)
        {
            _timer.Stop();
            return;
        }

        EvaluateTimerLifecycle();
    }

    public void OnPowerProfileChanged(PowerProfile profile)
    {
        lock (_stateSync)
        {
            _isDisplayOff = profile == PowerProfile.DisplayOff;
        }

        if (profile == PowerProfile.DisplayOff)
        {
            _timer.Stop();
            return;
        }

        EvaluateTimerLifecycle();
    }

    public object CreateExpandedView() => new Views.ClockView(ViewModel);

    public object? CreateCompactActiveView() => null;

    public object CreateSettingsView() => new Views.ClockSettingsView(ViewModel);

    public async Task ResetSettingsAsync()
    {
        await ViewModel.ResetToDefaultsAsync().ConfigureAwait(false);
        EvaluateTimerLifecycle();
    }

    public void RescheduleForSettingsChange()
    {
        ViewModel.RefreshReadout();
        EvaluateTimerLifecycle();
    }

    private void OnSystemEventsTimeChanged(object? sender, EventArgs e) => OnTimeEnvironmentChanged();

    private void OnTimeEnvironmentChanged()
    {
        if (_disposed)
        {
            return;
        }

        TimeZoneInfo.ClearCachedData();
        ViewModel.RefreshReadout();
        EvaluateTimerLifecycle();
    }

    private void EvaluateTimerLifecycle()
    {
        bool shouldRun;
        lock (_stateSync)
        {
            shouldRun = !_disposed
                && _isActive
                && !_isDisplayOff
                && _shellState == IslandShellState.Expanded;
        }

        if (!shouldRun)
        {
            _timer.Stop();
            return;
        }

        ScheduleNextBoundaryTick();
    }

    private void ScheduleNextBoundaryTick()
    {
        var delay = ClockFormatting.CalculateDelayUntilNextBoundary(
            _utcNowProvider(),
            ViewModel.CurrentSettings.ShowSeconds);
        _timer.Schedule(delay, OnBoundaryTick);
    }

    private void OnBoundaryTick()
    {
        if (_disposed)
        {
            return;
        }

        ViewModel.RefreshReadout();
        EvaluateTimerLifecycle();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        lock (_stateSync)
        {
            _disposed = true;
            _isActive = false;
        }

        _timer.Dispose();
        _timeChangeSubscription?.Dispose();
        _timeChangeSubscription = null;

        if (_systemEventsSubscribed)
        {
            SystemEvents.TimeChanged -= OnSystemEventsTimeChanged;
            _systemEventsSubscribed = false;
        }

        return ValueTask.CompletedTask;
    }
}
