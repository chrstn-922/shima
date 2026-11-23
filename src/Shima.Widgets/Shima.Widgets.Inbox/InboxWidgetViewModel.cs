using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Shima.Core.Contracts;

namespace Shima.Widgets.Inbox;

public sealed record InboxItemViewModel(
    string NotificationId,
    uint? OsToastId,
    string AppId,
    string AppName,
    string Title,
    string BodyText,
    string TimeLabel,
    string DismissActionLabel,
    string AccessibleState);

public sealed class InboxAppGroupViewModel : INotifyPropertyChanged
{
    private bool _isExpanded = true;

    public InboxAppGroupViewModel(
        string appId,
        string appName,
        BitmapSource? iconBitmap,
        IReadOnlyList<InboxItemViewModel> items,
        bool isExpanded = true)
    {
        AppId = appId;
        AppName = appName;
        AppInitial = string.IsNullOrWhiteSpace(appName)
            ? "?"
            : appName.Trim()[..1].ToUpperInvariant();
        IconBitmap = iconBitmap;
        _isExpanded = isExpanded;
        Items = new ObservableCollection<InboxItemViewModel>(items);
    }

    public string AppId { get; }

    public string AppName { get; }

    public string AppInitial { get; }

    public BitmapSource? IconBitmap { get; }

    public bool HasIcon => IconBitmap is not null;

    public ObservableCollection<InboxItemViewModel> Items { get; }

    public string CountLabel => Items.Count == 1 ? "1 alert" : $"{Items.Count} alerts";

    public string ExpandActionLabel => IsExpanded ? $"Collapse {AppName} notifications" : $"Expand {AppName} notifications";

    public string ClearGroupActionLabel => $"Clear all notifications from {AppName}";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandActionLabel)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Observable view-model managing grouped local Windows toast inbox items, Do Not Disturb
/// popup suppression, per-app exclusions, bidirectional Action Center dismissal, and Sparse Package guidance.
/// </summary>
public sealed class InboxWidgetViewModel : INotifyPropertyChanged, IDisposable
{
    public const string WindowsNotificationSettingsUri = "ms-settings:privacy-notifications";
    public const string NonNativePopupNotice =
        "Note: Apps that draw their own custom notification windows (instead of Windows Action Center toasts) cannot be captured.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly INotificationListenerAdapter _listenerAdapter;
    private readonly IInboxRepository _repository;
    private readonly IAppPreferencesService? _preferencesService;
    private readonly Action<string> _openUriAction;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly SemaphoreSlim _workGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationToken _disposeToken;
    private readonly Dictionary<string, bool> _groupExpandState = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    private IWidgetContext? _context;
    private Action<Action>? _uiDispatcher;
    private InboxSettings _settings = new();
    private Task _pendingWorkTask = Task.CompletedTask;
    private long _refreshVersion;

    private NotificationAccessState _accessState = NotificationAccessState.IdentityUnavailable;
    private bool _hasNotifications;
    private int _totalActiveCount;
    private string _statusMessage = string.Empty;
    private bool _disposed;

    public InboxWidgetViewModel(
        INotificationListenerAdapter listenerAdapter,
        IInboxRepository repository,
        IAppPreferencesService? preferencesService = null,
        Action<string>? openUriAction = null,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        _listenerAdapter = listenerAdapter ?? throw new ArgumentNullException(nameof(listenerAdapter));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _preferencesService = preferencesService;
        _openUriAction = openUriAction ?? DefaultOpenUri;
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _disposeToken = _disposeCts.Token;
    }

    public ObservableCollection<InboxAppGroupViewModel> AppGroups { get; } = [];

    public int RetentionDays => Math.Clamp(_preferencesService?.Current.InboxRetentionDays ?? 14, 1, 90);

    public bool SyncDismissalWithActionCenter =>
        _preferencesService?.Current.SyncInboxDismissalWithActionCenter ?? true;

    public string NonNativeNoticeText => NonNativePopupNotice;

    public NotificationAccessState AccessState
    {
        get => _accessState;
        private set
        {
            if (SetField(ref _accessState, value))
            {
                OnPropertyChanged(nameof(ShowAccessGuidanceCard));
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(AccessGuidanceTitle));
                OnPropertyChanged(nameof(AccessGuidanceMessage));
                OnPropertyChanged(nameof(ShowOpenWindowsSettingsButton));
                OnPropertyChanged(nameof(ShowRequestAccessButton));
                OnPropertyChanged(nameof(AccessStatusLabel));
                OnPropertyChanged(nameof(AccessibleStatus));
            }
        }
    }

    public bool ShowAccessGuidanceCard => _accessState != NotificationAccessState.Allowed;

    public bool ShowOpenWindowsSettingsButton =>
        _accessState is NotificationAccessState.IdentityUnavailable or NotificationAccessState.DeniedByUserOrPolicy;

    public bool ShowRequestAccessButton => _accessState == NotificationAccessState.DeniedByUserOrPolicy;

    public string AccessGuidanceTitle => _accessState switch
    {
        NotificationAccessState.Allowed => "Notification listener active",
        NotificationAccessState.DeniedByUserOrPolicy => "Windows notification access disabled",
        NotificationAccessState.UnsupportedOs => "Windows 10 (1809+) required",
        _ => "Windows package identity required"
    };

    public string AccessGuidanceMessage => _accessState switch
    {
        NotificationAccessState.Allowed => "Capturing native Windows Action Center toasts locally.",
        NotificationAccessState.DeniedByUserOrPolicy =>
            "Allow Shima in Windows Notification Settings, or grant access below. Custom app popups aren't captured.",
        NotificationAccessState.UnsupportedOs =>
            "Requires Windows 10 (1809+) for native notifications. Other Shima widgets continue operating normally.",
        _ =>
            "Register Sparse MSIX identity (or run Shima Setup) to unlock live toasts. Other widgets work normally."
    };

    public string AccessStatusLabel => _accessState switch
    {
        NotificationAccessState.Allowed => "Active (Windows UserNotificationListener)",
        NotificationAccessState.DeniedByUserOrPolicy => "Denied by user or Windows policy",
        NotificationAccessState.UnsupportedOs => "Unsupported OS version",
        _ => "Unavailable (unpackaged runtime without Sparse Package identity)"
    };

    public bool HasNotifications
    {
        get => _hasNotifications;
        private set
        {
            if (SetField(ref _hasNotifications, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(AccessibleStatus));
            }
        }
    }

    public bool ShowEmptyState => !ShowAccessGuidanceCard && !_hasNotifications;

    public int TotalActiveCount
    {
        get => _totalActiveCount;
        private set
        {
            if (SetField(ref _totalActiveCount, value))
            {
                OnPropertyChanged(nameof(TotalCountLabel));
                OnPropertyChanged(nameof(AccessibleStatus));
            }
        }
    }

    public string TotalCountLabel => TotalActiveCount switch
    {
        0 => "Empty",
        1 => "1 alert",
        _ => $"{TotalActiveCount} alerts"
    };

    public bool MaskNotificationBodyInPopups
    {
        get
        {
            lock (_sync)
            {
                return _settings.MaskNotificationBodyInPopups;
            }
        }
    }

    public int PreviewDurationSeconds
    {
        get
        {
            lock (_sync)
            {
                return Math.Clamp(_settings.PreviewDurationSeconds, 2, 15);
            }
        }
    }

    public string ExcludedAppsInputText
    {
        get
        {
            lock (_sync)
            {
                return string.Join(", ", _settings.ExcludedApps);
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string AccessibleStatus => ShowAccessGuidanceCard
        ? $"Notification Inbox: {AccessGuidanceTitle}"
        : $"Notification Inbox: {TotalCountLabel}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SettingsChanged;

    public void AttachUiDispatcher(Action<Action> dispatch) =>
        _uiDispatcher = dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var rawJson = await context.Settings.ReadJsonAsync(ct).ConfigureAwait(false);
        var loaded = NormalizeSettings(ParseSettingsOrDefault(rawJson));
        lock (_sync)
        {
            _settings = loaded;
        }

        _listenerAdapter.NotificationArrived -= OnAdapterNotificationArrived;
        _listenerAdapter.NotificationArrived += OnAdapterNotificationArrived;
        _listenerAdapter.NotificationRemovedExternally -= OnAdapterNotificationRemovedExternally;
        _listenerAdapter.NotificationRemovedExternally += OnAdapterNotificationRemovedExternally;

        if (_preferencesService is not null)
        {
            _preferencesService.PreferencesChanged -= OnPreferencesChanged;
            _preferencesService.PreferencesChanged += OnPreferencesChanged;
        }

        await RunUnderWorkGateAsync(
            token => _repository.PruneExpiredAsync(RetentionDays, InboxNotificationItem.MaxStoredItems, _utcNowProvider(), token),
            ct).ConfigureAwait(false);

        await RefreshListenerAndSnapshotAsync(ct).ConfigureAwait(false);
        await RunUnderWorkGateAsync(RefreshGroupsCoreAsync, ct).ConfigureAwait(false);
        RaiseSettingsProperties();
    }

    public async Task RefreshAccessStateAsync(CancellationToken ct = default)
    {
        await RefreshListenerAndSnapshotAsync(ct).ConfigureAwait(false);
        await RunUnderWorkGateAsync(RefreshGroupsCoreAsync, ct).ConfigureAwait(false);
    }

    public async Task RequestNotificationAccessAsync(CancellationToken ct = default)
    {
        var requestedState = await _listenerAdapter.RequestAccessAsync(ct).ConfigureAwait(false);
        DispatchToUi(() => AccessState = requestedState);

        if (requestedState == NotificationAccessState.Allowed)
        {
            await _listenerAdapter.StartListeningAsync(ct).ConfigureAwait(false);
            await ImportCurrentSnapshotSilentlyAsync(ct).ConfigureAwait(false);
        }

        await RunUnderWorkGateAsync(RefreshGroupsCoreAsync, ct).ConfigureAwait(false);
    }

    public void OpenWindowsNotificationSettings()
    {
        try
        {
            _openUriAction(WindowsNotificationSettingsUri);
            DispatchToUi(() => StatusMessage = "Opened Windows Notification Settings");
        }
        catch (Exception ex)
        {
            _context?.Logger.Warn("Unable to open Windows Notification Settings deep-link.", ex);
            DispatchToUi(() => StatusMessage = "Unable to open Windows Settings");
        }
    }

    public void ToggleGroupExpanded(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        DispatchToUi(() =>
        {
            var group = AppGroups.FirstOrDefault(g => string.Equals(g.AppId, appId, StringComparison.OrdinalIgnoreCase));
            if (group is not null)
            {
                group.IsExpanded = !group.IsExpanded;
                lock (_sync)
                {
                    _groupExpandState[group.AppId] = group.IsExpanded;
                }
            }
        });
    }

    public async Task DismissNotificationAsync(string notificationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);
        uint? osToastId = null;

        await _workGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = AppGroups
                .SelectMany(g => g.Items)
                .FirstOrDefault(i => string.Equals(i.NotificationId, notificationId, StringComparison.Ordinal));
            osToastId = existing?.OsToastId;

            await _repository.MarkDismissedAsync(notificationId, ct).ConfigureAwait(false);
            await RefreshGroupsCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _workGate.Release();
        }

        if (SyncDismissalWithActionCenter && osToastId.HasValue)
        {
            await _listenerAdapter.TryRemoveFromActionCenterAsync(osToastId.Value, ct).ConfigureAwait(false);
        }
    }

    public async Task ClearAppGroupAsync(string appId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        IReadOnlyList<uint> toastIds;

        await _workGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            toastIds = await _repository.ClearAppGroupAsync(appId, ct).ConfigureAwait(false);
            await RefreshGroupsCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _workGate.Release();
        }

        if (SyncDismissalWithActionCenter)
        {
            foreach (var osToastId in toastIds)
            {
                await _listenerAdapter.TryRemoveFromActionCenterAsync(osToastId, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        IReadOnlyList<uint> toastIds;

        await _workGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            toastIds = await _repository.ClearAllAsync(ct).ConfigureAwait(false);
            await RefreshGroupsCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _workGate.Release();
        }

        if (SyncDismissalWithActionCenter)
        {
            foreach (var osToastId in toastIds)
            {
                await _listenerAdapter.TryRemoveFromActionCenterAsync(osToastId, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task SetMaskNotificationBodyAsync(bool mask, CancellationToken ct = default)
    {
        await UpdateSettingsAsync(s => s with { MaskNotificationBodyInPopups = mask }, ct).ConfigureAwait(false);
        RaiseSettingsProperties();
    }

    public async Task SetPreviewDurationSecondsAsync(int seconds, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(seconds, 2, 15);
        await UpdateSettingsAsync(s => s with { PreviewDurationSeconds = clamped }, ct).ConfigureAwait(false);
        RaiseSettingsProperties();
    }

    public async Task SetExcludedAppsFromTextAsync(string? rawList, CancellationToken ct = default)
    {
        var parsed = ParseExcludedApps(rawList);
        await UpdateSettingsAsync(s => s with { ExcludedApps = parsed }, ct).ConfigureAwait(false);
        RaiseSettingsProperties();
    }

    public async Task SetExcludedAppsAsync(IReadOnlyList<string> apps, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(apps);
        var normalized = apps
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await UpdateSettingsAsync(s => s with { ExcludedApps = normalized }, ct).ConfigureAwait(false);
        RaiseSettingsProperties();
    }

    public async Task ResetToDefaultsAsync(CancellationToken ct = default)
    {
        var defaults = new InboxSettings();
        lock (_sync)
        {
            _settings = defaults;
        }

        if (_context is not null)
        {
            await _context.Settings.WriteJsonAsync(JsonSerializer.Serialize(defaults, JsonOptions), ct)
                .ConfigureAwait(false);
        }

        RaiseSettingsProperties();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task WaitForPendingWorkAsync()
    {
        Task pending;
        lock (_sync)
        {
            pending = _pendingWorkTask;
        }

        await pending.ConfigureAwait(false);
    }

    internal async Task HandleNotificationArrivedAsync(
        OsNotificationDto dto,
        bool allowPeek = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (_disposed || IsExcludedApp(dto.AppId, dto.AppName))
        {
            return;
        }

        InboxNotificationItem saved;
        await _workGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || IsExcludedApp(dto.AppId, dto.AppName))
            {
                return;
            }

            string? iconRelPath = null;
            if (dto.AppIconPngBytes is { Length: > 0 })
            {
                iconRelPath = await _repository.SaveAppIconAssetAsync(dto.AppId, dto.AppIconPngBytes, ct)
                    .ConfigureAwait(false);
            }

            var receivedAt = dto.ReceivedAtUtc == default ? _utcNowProvider().ToUniversalTime() : dto.ReceivedAtUtc.ToUniversalTime();
            saved = await _repository.UpsertToastAsync(
                new InboxNotificationItem
                {
                    NotificationId = Guid.NewGuid().ToString("N"),
                    OsToastId = dto.OsToastId,
                    AppId = dto.AppId,
                    AppName = string.IsNullOrWhiteSpace(dto.AppName) ? dto.AppId : dto.AppName,
                    AppIconRelPath = iconRelPath,
                    Title = dto.Title ?? string.Empty,
                    BodyText = dto.BodyText ?? string.Empty,
                    IsDismissed = false,
                    ReceivedAtUtc = receivedAt
                },
                ct).ConfigureAwait(false);

            await RefreshGroupsCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _workGate.Release();
        }

        if (!allowPeek || _listenerAdapter.IsDoNotDisturbActive())
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(saved.Title) ? saved.AppName : saved.Title;
        var body = MaskNotificationBodyInPopups
            ? "Notification content hidden (privacy mode)"
            : saved.BodyText;

        _context?.Events.Publish(new NotificationPeekContentEvent(
            InboxWidgetModule.Id,
            title,
            body,
            saved.NotificationId));
        _context?.Island.RequestPeekNotification(
            NotificationPriority.Normal,
            TimeSpan.FromSeconds(PreviewDurationSeconds));
    }

    internal async Task HandleNotificationRemovedExternallyAsync(uint osToastId, CancellationToken ct = default)
    {
        if (_disposed || !SyncDismissalWithActionCenter)
        {
            return;
        }

        await _workGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _repository.MarkDismissedByOsToastIdAsync(osToastId, ct).ConfigureAwait(false);
            await RefreshGroupsCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _workGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerAdapter.NotificationArrived -= OnAdapterNotificationArrived;
        _listenerAdapter.NotificationRemovedExternally -= OnAdapterNotificationRemovedExternally;
        if (_preferencesService is not null)
        {
            _preferencesService.PreferencesChanged -= OnPreferencesChanged;
        }

        try
        {
            _disposeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _workGate.Dispose();
        _disposeCts.Dispose();
    }

    private async Task RefreshListenerAndSnapshotAsync(CancellationToken ct)
    {
        var state = await _listenerAdapter.GetAccessStateAsync(ct).ConfigureAwait(false);
        DispatchToUi(() => AccessState = state);

        if (state == NotificationAccessState.Allowed)
        {
            await _listenerAdapter.StartListeningAsync(ct).ConfigureAwait(false);
            await ImportCurrentSnapshotSilentlyAsync(ct).ConfigureAwait(false);
        }
        else
        {
            _listenerAdapter.StopListening();
        }
    }

    private async Task ImportCurrentSnapshotSilentlyAsync(CancellationToken ct)
    {
        var snapshot = await _listenerAdapter.GetCurrentToastSnapshotAsync(ct).ConfigureAwait(false);
        foreach (var dto in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            await HandleNotificationArrivedAsync(dto, allowPeek: false, ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshGroupsCoreAsync(CancellationToken ct)
    {
        var refreshVersion = Interlocked.Increment(ref _refreshVersion);
        var groups = await _repository.GetGroupedByAppAsync(includeDismissed: false, ct).ConfigureAwait(false);
        var vms = new List<InboxAppGroupViewModel>(groups.Count);
        var total = 0;

        foreach (var group in groups)
        {
            bool isExpanded;
            lock (_sync)
            {
                if (!_groupExpandState.TryGetValue(group.AppId, out isExpanded))
                {
                    isExpanded = true;
                    _groupExpandState[group.AppId] = true;
                }
            }

            var icon = TryLoadIconBitmap(group.AppIconRelPath);
            var itemVms = group.Items.Select(item =>
            {
                total++;
                var timeLabel = item.ReceivedAtUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
                var safeTitle = string.IsNullOrWhiteSpace(item.Title) ? group.AppName : item.Title;
                return new InboxItemViewModel(
                    item.NotificationId,
                    item.OsToastId,
                    item.AppId,
                    item.AppName,
                    safeTitle,
                    item.BodyText,
                    timeLabel,
                    $"Dismiss notification {safeTitle}",
                    $"{group.AppName}: {safeTitle}, {timeLabel}");
            }).ToArray();

            vms.Add(new InboxAppGroupViewModel(group.AppId, group.AppName, icon, itemVms, isExpanded));
        }

        DispatchToUi(() =>
        {
            if (_disposed || Volatile.Read(ref _refreshVersion) != refreshVersion)
            {
                return;
            }

            AppGroups.Clear();
            foreach (var vm in vms)
            {
                AppGroups.Add(vm);
            }

            TotalActiveCount = total;
            HasNotifications = total > 0;
        });
    }

    private async Task RunUnderWorkGateAsync(Func<CancellationToken, Task> work, CancellationToken ct)
    {
        await _workGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await work(ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseWorkGateSafely();
        }
    }

    private async Task RunUnderWorkGateSafeAsync(Func<CancellationToken, Task> work, CancellationToken ct)
    {
        try
        {
            await _workGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException)
            && (_disposed || ct.IsCancellationRequested))
        {
            return;
        }

        try
        {
            if (_disposed || ct.IsCancellationRequested)
            {
                return;
            }

            await work(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException)
            && (_disposed || ct.IsCancellationRequested))
        {
            // Ignore shutdown cancellations cleanly during teardown.
        }
        finally
        {
            ReleaseWorkGateSafely();
        }
    }

    private void ReleaseWorkGateSafely()
    {
        try
        {
            _workGate.Release();
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
    }

    private void OnAdapterNotificationArrived(object? sender, OsNotificationEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        EnqueueWork(async () =>
        {
            try
            {
                await HandleNotificationArrivedAsync(e.Notification, allowPeek: true, _disposeToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException)
                && (_disposed || _disposeToken.IsCancellationRequested))
            {
            }
        });
    }

    private void OnAdapterNotificationRemovedExternally(object? sender, uint osToastId)
    {
        if (_disposed || !SyncDismissalWithActionCenter)
        {
            return;
        }

        EnqueueWork(async () =>
        {
            try
            {
                await HandleNotificationRemovedExternallyAsync(osToastId, _disposeToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException)
                && (_disposed || _disposeToken.IsCancellationRequested))
            {
            }
        });
    }

    private void OnPreferencesChanged(object? sender, AppPreferencesSnapshot e)
    {
        if (_disposed)
        {
            return;
        }

        EnqueueWork(() => RunUnderWorkGateSafeAsync(
            async ct =>
            {
                await _repository
                    .PruneExpiredAsync(RetentionDays, InboxNotificationItem.MaxStoredItems, _utcNowProvider(), ct)
                    .ConfigureAwait(false);
                await RefreshGroupsCoreAsync(ct).ConfigureAwait(false);
            },
            _disposeToken));
    }

    private void EnqueueWork(Func<Task> work)
    {
        lock (_sync)
        {
            _pendingWorkTask = _pendingWorkTask.ContinueWith(
                async _ =>
                {
                    if (!_disposed)
                    {
                        await work().ConfigureAwait(false);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private bool IsExcludedApp(string appId, string appName)
    {
        IReadOnlyList<string> excluded;
        lock (_sync)
        {
            excluded = _settings.ExcludedApps;
        }

        if (excluded.Count == 0)
        {
            return false;
        }

        foreach (var candidate in excluded)
        {
            if (string.Equals(candidate, appId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate, appName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(appId) && appId.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(appName) && appName.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private BitmapSource? TryLoadIconBitmap(string? iconRelPath)
    {
        var fullPath = _repository.ResolveIconFullPath(iconRelPath);
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            if (bytes.Length == 0)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 48;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private async Task UpdateSettingsAsync(
        Func<InboxSettings, InboxSettings> transform,
        CancellationToken ct)
    {
        InboxSettings updated;
        lock (_sync)
        {
            updated = NormalizeSettings(transform(_settings));
            _settings = updated;
        }

        if (_context is not null)
        {
            await _context.Settings.WriteJsonAsync(JsonSerializer.Serialize(updated, JsonOptions), ct)
                .ConfigureAwait(false);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseSettingsProperties()
    {
        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(MaskNotificationBodyInPopups));
            OnPropertyChanged(nameof(PreviewDurationSeconds));
            OnPropertyChanged(nameof(ExcludedAppsInputText));
            OnPropertyChanged(nameof(AccessStatusLabel));
        });
    }

    private static IReadOnlyList<string> ParseExcludedApps(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => !string.IsNullOrEmpty(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static InboxSettings NormalizeSettings(InboxSettings settings) =>
        settings with
        {
            PreviewDurationSeconds = Math.Clamp(settings.PreviewDurationSeconds, 2, 15),
            ExcludedApps = (settings.ExcludedApps ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

    private static InboxSettings ParseSettingsOrDefault(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || string.Equals(rawJson.Trim(), "{}", StringComparison.Ordinal))
        {
            return new InboxSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<InboxSettings>(rawJson, JsonOptions) ?? new InboxSettings();
        }
        catch (JsonException)
        {
            return new InboxSettings();
        }
    }

    private static void DefaultOpenUri(string uri)
    {
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private void DispatchToUi(Action action)
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher is not null)
        {
            dispatcher(action);
        }
        else
        {
            action();
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
