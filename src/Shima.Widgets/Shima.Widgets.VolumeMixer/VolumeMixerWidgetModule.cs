using Shima.Core.Contracts;

namespace Shima.Widgets.VolumeMixer;

/// <summary>
/// Event-driven CoreAudio WASAPI Volume Mixer widget module (shima.mixer) with zero polling timers.
/// </summary>
public sealed class VolumeMixerWidgetModule : IWidgetModule
{
    public const string Id = "shima.mixer";

    private readonly IAudioMixerAdapter _audioAdapter;
    private readonly object _stateSync = new();

    private bool _isSuspended;
    private bool _disposed;

    public VolumeMixerWidgetModule(IAudioMixerAdapter audioAdapter)
    {
        _audioAdapter = audioAdapter ?? throw new ArgumentNullException(nameof(audioAdapter));
        ViewModel = new VolumeMixerWidgetViewModel(_audioAdapter);
    }

    public WidgetDescriptor Metadata { get; } = new()
    {
        WidgetId = Id,
        DisplayName = "Volume Mixer",
        DefaultOrder = 6,
        RequiresBackgroundSubscription = true,
        SupportsCompactActiveState = false,
        SupportsNotificationPeek = false,
        DefaultAccentHex = "#38BDF8"
    };

    public VolumeMixerWidgetViewModel ViewModel { get; }

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);

        await ViewModel.InitializeAsync(context, ct).ConfigureAwait(false);
        _audioAdapter.MixerChanged += OnMixerChanged;
        await _audioAdapter.StartAsync(ct).ConfigureAwait(false);
        ViewModel.ApplySnapshot(_audioAdapter.CurrentSnapshot);
    }

    public Task ActivateAsync(WidgetActivationReason reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ViewModel.ApplySnapshot(_audioAdapter.CurrentSnapshot);
        return Task.CompletedTask;
    }

    public Task DeactivateAsync() => Task.CompletedTask;

    public void OnShellStateChanged(IslandShellState newState)
    {
        var wasSuspended = IsUiSuspended;
        lock (_stateSync)
        {
            _isSuspended = newState == IslandShellState.Suspended;
        }

        if (wasSuspended && newState == IslandShellState.Expanded && !_disposed)
        {
            ViewModel.ApplySnapshot(_audioAdapter.CurrentSnapshot);
        }
    }

    public void OnPowerProfileChanged(PowerProfile profile)
    {
        var wasSuspended = IsUiSuspended;
        lock (_stateSync)
        {
            _isSuspended = profile == PowerProfile.DisplayOff;
        }

        if (wasSuspended && profile != PowerProfile.DisplayOff && !_disposed)
        {
            ViewModel.ApplySnapshot(_audioAdapter.CurrentSnapshot);
        }
    }

    public object CreateExpandedView() => new Views.VolumeMixerView(ViewModel);

    public object? CreateCompactActiveView() => null;

    public object CreateSettingsView() => new Views.VolumeMixerSettingsView(ViewModel);

    public Task ResetSettingsAsync() => ViewModel.ResetToDefaultsAsync();

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        lock (_stateSync)
        {
            _disposed = true;
        }

        _audioAdapter.MixerChanged -= OnMixerChanged;
        _audioAdapter.Stop();
        return ValueTask.CompletedTask;
    }

    private bool IsUiSuspended
    {
        get
        {
            lock (_stateSync)
            {
                return _isSuspended;
            }
        }
    }

    private void OnMixerChanged(object? sender, AudioMixerSnapshot snapshot)
    {
        if (_disposed || IsUiSuspended)
        {
            return;
        }

        ViewModel.ApplySnapshot(snapshot);
    }
}
