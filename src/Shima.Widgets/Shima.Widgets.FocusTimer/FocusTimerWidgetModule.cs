using Shima.Core.Contracts;

namespace Shima.Widgets.FocusTimer;

/// <summary>
/// Focus Timer widget module (shima.focus) supporting Pomodoro/custom intervals,
/// procedural Zen ambient noise (White/Pink/Brown), crash-safe Magnification screen dimming/grayscale,
/// compact Active countdown badge, and zero idle polling timers when inactive.
/// </summary>
public sealed class FocusTimerWidgetModule : IWidgetModule
{
    public const string Id = "shima.focus";

    private readonly IZenAudioSynthAdapter _zenAudio;
    private readonly IZenDisplayEffectAdapter _zenDisplay;
    private readonly IFocusTickTimer _tickTimer;
    private readonly bool _ownsTickTimer;
    private bool _disposed;

    public FocusTimerWidgetModule(
        IFocusRepository repository,
        IZenAudioSynthAdapter zenAudio,
        IZenDisplayEffectAdapter zenDisplay,
        IFocusTickTimer? tickTimer = null,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        _zenAudio = zenAudio ?? throw new ArgumentNullException(nameof(zenAudio));
        _zenDisplay = zenDisplay ?? throw new ArgumentNullException(nameof(zenDisplay));
        _ownsTickTimer = tickTimer is null;
        _tickTimer = tickTimer ?? new SystemFocusTickTimer();

        ViewModel = new FocusTimerWidgetViewModel(
            repository,
            _zenAudio,
            _zenDisplay,
            _tickTimer,
            utcNowProvider);
    }

    public WidgetDescriptor Metadata { get; } = new()
    {
        WidgetId = Id,
        DisplayName = "Focus Timer",
        DefaultOrder = 2,
        RequiresBackgroundSubscription = false,
        SupportsCompactActiveState = true,
        SupportsNotificationPeek = true,
        DefaultAccentHex = "#FB923C"
    };

    public FocusTimerWidgetViewModel ViewModel { get; }

    public bool IsTimerRunning => _tickTimer.IsRunning;

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _zenDisplay.RecoverFromStartupIfNeeded();
        await ViewModel.InitializeAsync(context, ct).ConfigureAwait(false);
    }

    public Task ActivateAsync(WidgetActivationReason reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.CompletedTask;
    }

    public Task DeactivateAsync() => Task.CompletedTask;

    public void OnShellStateChanged(IslandShellState newState)
    {
        if (ViewModel.PauseScreenEffectOnFullscreen)
        {
            _zenDisplay.SetPausedByFullscreen(newState == IslandShellState.Fullscreen);
        }

        if (newState == IslandShellState.Suspended)
        {
            ViewModel.OnPowerProfileChanged(PowerProfile.DisplayOff);
        }
    }

    public void OnPowerProfileChanged(PowerProfile profile) =>
        ViewModel.OnPowerProfileChanged(profile);

    public object CreateExpandedView() => new Views.FocusTimerView(ViewModel);

    public object? CreateCompactActiveView() => new Views.FocusTimerCompactView(ViewModel);

    public object CreateSettingsView() => new Views.FocusTimerSettingsView(ViewModel);

    public Task ResetSettingsAsync() => ViewModel.ResetToDefaultsAsync(CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        ViewModel.Dispose();
        if (_ownsTickTimer)
        {
            _tickTimer.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
