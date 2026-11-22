using Shima.Core.Contracts;

namespace Shima.Widgets.Inbox;

/// <summary>
/// Notification Inbox widget module (shima.inbox) backed strictly by Windows UserNotificationListener,
/// grouped by app with bidirectional Action Center dismissal, DND suppression, and identity guidance.
/// </summary>
public sealed class InboxWidgetModule : IWidgetModule
{
    public const string Id = "shima.inbox";

    private readonly INotificationListenerAdapter _listenerAdapter;
    private bool _disposed;

    public InboxWidgetModule(
        INotificationListenerAdapter listenerAdapter,
        IInboxRepository repository,
        IAppPreferencesService? preferencesService = null,
        Action<string>? openUriAction = null,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        _listenerAdapter = listenerAdapter ?? throw new ArgumentNullException(nameof(listenerAdapter));
        ViewModel = new InboxWidgetViewModel(
            _listenerAdapter,
            repository,
            preferencesService,
            openUriAction,
            utcNowProvider);
    }

    public WidgetDescriptor Metadata { get; } = new()
    {
        WidgetId = Id,
        DisplayName = "Inbox",
        DefaultOrder = 4,
        RequiresBackgroundSubscription = true,
        SupportsCompactActiveState = false,
        SupportsNotificationPeek = true,
        DefaultAccentHex = "#F43F5E"
    };

    public InboxWidgetViewModel ViewModel { get; }

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

    public Task DeactivateAsync() => Task.CompletedTask;

    public void OnShellStateChanged(IslandShellState newState)
    {
    }

    public void OnPowerProfileChanged(PowerProfile profile)
    {
    }

    public object CreateExpandedView() => new Views.InboxView(ViewModel);

    public object? CreateCompactActiveView() => null;

    public object CreateSettingsView() => new Views.InboxSettingsView(ViewModel);

    public Task ResetSettingsAsync() => ViewModel.ResetToDefaultsAsync(CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _listenerAdapter.StopListening();
        ViewModel.Dispose();
        return ValueTask.CompletedTask;
    }
}
