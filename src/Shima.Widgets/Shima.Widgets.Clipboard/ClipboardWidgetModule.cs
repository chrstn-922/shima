using Shima.Core.Contracts;

namespace Shima.Widgets.Clipboard;

/// <summary>
/// Clipboard history widget module (shima.clipboard) backed by WM_CLIPBOARDUPDATE events,
/// password-manager exclusion rules, SQLite deduplication/retention, and one-click recopy.
/// </summary>
public sealed class ClipboardWidgetModule : IWidgetModule
{
    public const string Id = "shima.clipboard";

    private readonly IClipboardMonitorAdapter _clipboardAdapter;
    private bool _disposed;

    public ClipboardWidgetModule(
        IClipboardMonitorAdapter clipboardAdapter,
        IClipboardRepository repository,
        IAppPreferencesService? preferencesService = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        IOneShotTimer? copyFeedbackTimer = null)
    {
        _clipboardAdapter = clipboardAdapter ?? throw new ArgumentNullException(nameof(clipboardAdapter));
        ViewModel = new ClipboardWidgetViewModel(
            _clipboardAdapter,
            repository,
            preferencesService,
            utcNowProvider,
            copyFeedbackTimer);
    }

    public WidgetDescriptor Metadata { get; } = new()
    {
        WidgetId = Id,
        DisplayName = "Clipboard",
        DefaultOrder = 5,
        RequiresBackgroundSubscription = true,
        SupportsCompactActiveState = false,
        SupportsNotificationPeek = false,
        DefaultAccentHex = "#38BDF8"
    };

    public ClipboardWidgetViewModel ViewModel { get; }

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ViewModel.InitializeAsync(context, ct).ConfigureAwait(false);
    }

    public Task ActivateAsync(WidgetActivationReason reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        ViewModel.ClearCopyFeedback();
        ViewModel.NotifyInputFocusChanged(false);
        return Task.CompletedTask;
    }

    public void OnShellStateChanged(IslandShellState newState)
    {
        if (newState is IslandShellState.Collapsed or IslandShellState.Fullscreen or IslandShellState.Suspended)
        {
            ViewModel.ClearCopyFeedback();
            ViewModel.NotifyInputFocusChanged(false);
        }
    }

    public void OnPowerProfileChanged(PowerProfile profile)
    {
        if (profile == PowerProfile.DisplayOff)
        {
            ViewModel.ClearCopyFeedback();
            ViewModel.NotifyInputFocusChanged(false);
        }
    }

    public object CreateExpandedView() => new Views.ClipboardView(ViewModel);

    public object? CreateCompactActiveView() => null;

    public object CreateSettingsView() => new Views.ClipboardSettingsView(ViewModel);

    public Task ResetSettingsAsync() => ViewModel.ResetToDefaultsAsync(CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _clipboardAdapter.Stop();
        ViewModel.Dispose();
        return ValueTask.CompletedTask;
    }
}
