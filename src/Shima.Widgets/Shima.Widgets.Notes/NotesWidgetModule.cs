using Shima.Core.Contracts;

namespace Shima.Widgets.Notes;

/// <summary>
/// Notes scratchpad widget module (shima.notes) supporting 500ms debounced auto-save,
/// multi-note management, keyword search, and focus-safe island collapse protection.
/// </summary>
public sealed class NotesWidgetModule : IWidgetModule
{
    public const string Id = "shima.notes";

    private readonly INotesDebounceTimer _debounceTimer;
    private readonly bool _ownsDebounceTimer;
    private bool _disposed;

    public NotesWidgetModule(
        INoteRepository repository,
        IClipboardMonitorAdapter? clipboardAdapter = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        INotesDebounceTimer? debounceTimer = null,
        IOneShotTimer? copyFeedbackTimer = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _ownsDebounceTimer = debounceTimer is null;
        _debounceTimer = debounceTimer ?? new SystemNotesDebounceTimer();
        ViewModel = new NotesWidgetViewModel(
            repository,
            _debounceTimer,
            clipboardAdapter,
            utcNowProvider,
            copyTextOverride: null,
            copyFeedbackTimer);
    }

    public WidgetDescriptor Metadata { get; } = new()
    {
        WidgetId = Id,
        DisplayName = "Notes",
        DefaultOrder = 3,
        RequiresBackgroundSubscription = false,
        SupportsCompactActiveState = false,
        SupportsNotificationPeek = false,
        DefaultAccentHex = "#FBBF24"
    };

    public NotesWidgetViewModel ViewModel { get; }

    public bool IsAutoSavePending => _debounceTimer.IsPending;

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

    public async Task DeactivateAsync()
    {
        ViewModel.ClearCopyFeedback();
        ViewModel.NotifyInputFocusChanged(false);
        await ViewModel.FlushPendingSaveAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public void OnShellStateChanged(IslandShellState newState)
    {
        if (newState is IslandShellState.Collapsed or IslandShellState.Fullscreen or IslandShellState.Suspended)
        {
            ViewModel.ClearCopyFeedback();
            ViewModel.NotifyInputFocusChanged(false);
            _ = ViewModel.FlushPendingSaveAsync(CancellationToken.None);
        }
    }

    public void OnPowerProfileChanged(PowerProfile profile)
    {
        if (profile == PowerProfile.DisplayOff)
        {
            ViewModel.ClearCopyFeedback();
            ViewModel.NotifyInputFocusChanged(false);
            _ = ViewModel.FlushPendingSaveAsync(CancellationToken.None);
        }
    }

    public object CreateExpandedView() => new Views.NotesView(ViewModel);

    public object? CreateCompactActiveView() => null;

    public object CreateSettingsView() => new Views.NotesSettingsView(ViewModel);

    public Task ResetSettingsAsync() => ViewModel.ResetToDefaultsAsync(CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ViewModel.NotifyInputFocusChanged(false);
        await ViewModel.FlushPendingSaveAsync(CancellationToken.None).ConfigureAwait(false);
        ViewModel.Dispose();
        if (_ownsDebounceTimer)
        {
            _debounceTimer.Dispose();
        }
    }
}
