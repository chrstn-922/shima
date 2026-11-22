using Shima.Core.Contracts;

namespace Shima.Widgets.Developer;

/// <summary>
/// Developer Activity widget module (shima.developer) coordinating independent Claude Code
/// and OpenCode adapters with zero cross-adapter failure coupling.
/// </summary>
public sealed class DeveloperWidgetModule :
    IWidgetModule,
    ICollapsedStatusViewProvider,
    IExpandAutoSelectProvider
{
    public const string Id = "shima.developer";

    private readonly IReadOnlyList<ICodingAgentAdapter> _adapters;
    private bool _disposed;

    public DeveloperWidgetModule(
        IReadOnlyList<ICodingAgentAdapter> adapters,
        IAgentSessionLogRepository logRepository,
        IAppPreferencesService preferences,
        IOneShotTimer? finishedDwellTimer = null,
        IOneShotTimer? errorCapTimer = null,
        IOneShotTimer? retentionTimer = null,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        ViewModel = new DeveloperWidgetViewModel(
            _adapters,
            logRepository,
            preferences,
            finishedDwellTimer,
            errorCapTimer,
            retentionTimer,
            utcNowProvider);
    }

    public WidgetDescriptor Metadata { get; } = new()
    {
        WidgetId = Id,
        DisplayName = "Developer Activity",
        DefaultOrder = 7,
        RequiresBackgroundSubscription = true,
        SupportsCompactActiveState = true,
        SupportsNotificationPeek = true,
        DefaultAccentHex = "#38BDF8"
    };

    public DeveloperWidgetViewModel ViewModel { get; }

    public bool ShouldAutoSelectOnExpand => ViewModel.ShouldAutoSelectOnExpand;

    public void OnCollapsedNotchHovered() => ViewModel.AcknowledgeCollapsedBarHoverOrOpen();

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ViewModel.SetWidgetEnabled(true);
        await ViewModel.InitializeAsync(context, ct).ConfigureAwait(false);

        // Start each adapter independently so one failing adapter never disrupts another or the widget shell.
        foreach (var adapter in _adapters)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await adapter.StartAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.Logger.Warn($"Adapter '{adapter.AdapterId}' failed to start cleanly; continuing isolated.", ex);
            }
        }
    }

    public Task ActivateAsync(WidgetActivationReason reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ViewModel.AcknowledgeCollapsedBarHoverOrOpen();
        ViewModel.RefreshTimestamps();
        return Task.CompletedTask;
    }

    public Task DeactivateAsync() => Task.CompletedTask;

    public void OnShellStateChanged(IslandShellState newState) =>
        ViewModel.OnShellStateChanged(newState);

    public void OnPowerProfileChanged(PowerProfile profile) =>
        ViewModel.OnPowerProfileChanged(profile);

    public object CreateExpandedView() => new Views.DeveloperView(ViewModel);

    public object? CreateCompactActiveView() => new Views.DeveloperCompactView(ViewModel);

    public object? CreateCollapsedStatusView() =>
        ViewModel.IsCollapsedBarVisible ? new Views.DeveloperCollapsedBarView(ViewModel) : null;

    public object CreateSettingsView() => new Views.DeveloperSettingsView(ViewModel);

    public Task ResetSettingsAsync() => ViewModel.RefreshDiagnosticsAsync(CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ViewModel.SetWidgetEnabled(false);
        ViewModel.Dispose();

        foreach (var adapter in _adapters)
        {
            try
            {
                await adapter.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Isolate adapter shutdown exceptions on widget disable/dispose.
            }
        }
    }
}
