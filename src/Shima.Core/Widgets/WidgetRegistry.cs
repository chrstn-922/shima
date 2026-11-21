using System.Text.Json;
using Shima.Core.Contracts;

namespace Shima.Core.Widgets;

public sealed record WidgetModuleRegistration(WidgetDescriptor Metadata, Func<IWidgetModule> Create);

public sealed record WidgetRegistrationSnapshot(
    WidgetDescriptor Metadata,
    bool IsEnabled,
    int SortOrder,
    bool AllowPeekNotifications,
    bool IsFaulted);

public sealed record FaultedWidgetFallbackView(string WidgetId, string DisplayName, string Message);

public sealed class WidgetRegistry : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, WidgetEntry> _entries;
    private readonly IWidgetConfigRepository _configRepository;
    private readonly ILogger _logger;
    private readonly IEventDispatcher _events;
    private readonly IThemeTokenProvider _theme;
    private readonly IIslandAttentionRequester _island;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;
    private string? _activeWidgetId;

    public WidgetRegistry(
        IEnumerable<WidgetModuleRegistration> registrations,
        IWidgetConfigRepository configRepository,
        ILogger logger,
        IEventDispatcher events,
        IThemeTokenProvider theme,
        IIslandAttentionRequester island)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _configRepository = configRepository ?? throw new ArgumentNullException(nameof(configRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _island = island ?? throw new ArgumentNullException(nameof(island));

        _entries = new Dictionary<string, WidgetEntry>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            registration.Metadata.Validate();
            if (!_entries.TryAdd(registration.Metadata.WidgetId, new WidgetEntry(registration)))
            {
                throw new ArgumentException(
                    $"Duplicate widget id '{registration.Metadata.WidgetId}'.",
                    nameof(registrations));
            }
        }
    }

    public string? ActiveWidgetId
    {
        get
        {
            lock (_sync)
            {
                return _activeWidgetId;
            }
        }
    }

    public IReadOnlyList<WidgetRegistrationSnapshot> Widgets
    {
        get
        {
            lock (_sync)
            {
                return _entries.Values
                    .OrderBy(entry => entry.Config?.SortOrder ?? entry.Registration.Metadata.DefaultOrder)
                    .ThenBy(entry => entry.Registration.Metadata.WidgetId, StringComparer.Ordinal)
                    .Select(entry => new WidgetRegistrationSnapshot(
                        entry.Registration.Metadata,
                        entry.Config?.IsEnabled ?? true,
                        entry.Config?.SortOrder ?? entry.Registration.Metadata.DefaultOrder,
                        entry.Config?.AllowPeekNotifications ?? true,
                        entry.IsFaulted))
                    .ToArray();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initializeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var configs = await _configRepository.GetAllAsync().ConfigureAwait(false);
            var configMap = configs
                .GroupBy(config => config.WidgetId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            foreach (var entry in SnapshotEntries().OrderBy(entry => entry.Registration.Metadata.DefaultOrder))
            {
                ct.ThrowIfCancellationRequested();
                if (!configMap.TryGetValue(entry.WidgetId, out var config))
                {
                    config = CreateDefaultConfig(entry.Registration.Metadata);
                    await _configRepository.SaveAsync(config).ConfigureAwait(false);
                }

                lock (_sync)
                {
                    entry.Config = config;
                }

                if (config.IsEnabled)
                {
                    await InitializeEntryAsync(entry, ct).ConfigureAwait(false);
                }
            }

            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task SetEnabledAsync(string widgetId, bool isEnabled, CancellationToken ct = default)
    {
        var entry = GetEntry(widgetId);
        ct.ThrowIfCancellationRequested();

        if (!isEnabled)
        {
            if (string.Equals(ActiveWidgetId, widgetId, StringComparison.Ordinal))
            {
                await DeactivateActiveAsync().ConfigureAwait(false);
            }

            await DisposeEntryAsync(entry).ConfigureAwait(false);
        }

        var current = entry.Config ?? CreateDefaultConfig(entry.Registration.Metadata);
        var updated = current with { IsEnabled = isEnabled, UpdatedAtUtc = DateTimeOffset.UtcNow };
        lock (_sync)
        {
            entry.Config = updated;
        }

        await _configRepository.SaveAsync(updated).ConfigureAwait(false);

        if (isEnabled && !entry.IsInitialized && !entry.IsFaulted)
        {
            await InitializeEntryAsync(entry, ct).ConfigureAwait(false);
        }
    }

    public async Task ActivateAsync(
        string widgetId,
        WidgetActivationReason reason,
        CancellationToken ct = default)
    {
        var entry = GetEntry(widgetId);
        ct.ThrowIfCancellationRequested();

        var config = entry.Config;
        if (config is null || !config.IsEnabled || entry.IsFaulted)
        {
            return;
        }

        if (!entry.IsInitialized)
        {
            await InitializeEntryAsync(entry, ct).ConfigureAwait(false);
        }

        var instance = entry.Instance;
        if (instance is null || entry.IsFaulted)
        {
            return;
        }

        string? previous;
        lock (_sync)
        {
            previous = _activeWidgetId;
        }

        if (previous is not null && !string.Equals(previous, widgetId, StringComparison.Ordinal))
        {
            await DeactivateAsync(previous).ConfigureAwait(false);
        }

        try
        {
            await instance.ActivateAsync(reason).ConfigureAwait(false);
            lock (_sync)
            {
                _activeWidgetId = widgetId;
            }
        }
        catch (Exception exception)
        {
            MarkFaulted(entry, nameof(IWidgetModule.ActivateAsync), exception);
            lock (_sync)
            {
                if (string.Equals(_activeWidgetId, widgetId, StringComparison.Ordinal))
                {
                    _activeWidgetId = null;
                }
            }
        }
    }

    public async Task DeactivateActiveAsync()
    {
        string? active;
        lock (_sync)
        {
            active = _activeWidgetId;
        }

        if (active is not null)
        {
            await DeactivateAsync(active).ConfigureAwait(false);
        }
    }

    public object CreateExpandedView(string widgetId)
    {
        var entry = GetEntry(widgetId);
        var instance = entry.Instance;
        if (instance is null || entry.IsFaulted)
        {
            return CreateFallback(entry);
        }

        try
        {
            return instance.CreateExpandedView();
        }
        catch (Exception exception)
        {
            MarkFaulted(entry, nameof(IWidgetModule.CreateExpandedView), exception);
            return CreateFallback(entry);
        }
    }

    public object? CreateCompactActiveView(string widgetId)
    {
        var entry = GetEntry(widgetId);
        var instance = entry.Instance;
        if (instance is null || entry.IsFaulted)
        {
            return null;
        }

        try
        {
            return instance.CreateCompactActiveView();
        }
        catch (Exception exception)
        {
            MarkFaulted(entry, nameof(IWidgetModule.CreateCompactActiveView), exception);
            return null;
        }
    }

    public object CreateSettingsView(string widgetId)
    {
        var entry = GetEntry(widgetId);
        var instance = entry.Instance;
        if (instance is null || entry.IsFaulted)
        {
            return CreateFallback(entry);
        }

        try
        {
            return instance.CreateSettingsView();
        }
        catch (Exception exception)
        {
            MarkFaulted(entry, nameof(IWidgetModule.CreateSettingsView), exception);
            return CreateFallback(entry);
        }
    }

    public void OnShellStateChanged(IslandShellState newState)
    {
        foreach (var entry in SnapshotEntries())
        {
            var instance = entry.Instance;
            if (instance is null || entry.IsFaulted)
            {
                continue;
            }

            try
            {
                instance.OnShellStateChanged(newState);
            }
            catch (Exception exception)
            {
                MarkFaulted(entry, nameof(IWidgetModule.OnShellStateChanged), exception);
            }
        }

        if (newState is IslandShellState.Collapsed or IslandShellState.Fullscreen or IslandShellState.Suspended)
        {
            _ = DeactivateAfterShellStateChangeAsync();
        }
    }

    public void OnPowerProfileChanged(PowerProfile profile)
    {
        foreach (var entry in SnapshotEntries())
        {
            var instance = entry.Instance;
            if (instance is null || entry.IsFaulted)
            {
                continue;
            }

            try
            {
                instance.OnPowerProfileChanged(profile);
            }
            catch (Exception exception)
            {
                MarkFaulted(entry, nameof(IWidgetModule.OnPowerProfileChanged), exception);
            }
        }
    }

    public async Task ResetSettingsAsync(string widgetId, CancellationToken ct = default)
    {
        var entry = GetEntry(widgetId);
        ct.ThrowIfCancellationRequested();

        var instance = entry.Instance;
        if (instance is not null && !entry.IsFaulted)
        {
            try
            {
                await instance.ResetSettingsAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                MarkFaulted(entry, nameof(IWidgetModule.ResetSettingsAsync), exception);
                return;
            }
        }

        var current = entry.Config ?? CreateDefaultConfig(entry.Registration.Metadata);
        var updated = current with { SettingsJson = "{}", UpdatedAtUtc = DateTimeOffset.UtcNow };
        lock (_sync)
        {
            entry.Config = updated;
        }

        await _configRepository.SaveAsync(updated).ConfigureAwait(false);
    }

    public async Task ReloadAsync(string widgetId, CancellationToken ct = default)
    {
        var entry = GetEntry(widgetId);
        ct.ThrowIfCancellationRequested();

        var config = entry.Config;
        if (config is null || !config.IsEnabled)
        {
            return;
        }

        await DisposeEntryAsync(entry).ConfigureAwait(false);
        entry.IsFaulted = false;
        await InitializeEntryAsync(entry, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in SnapshotEntries())
        {
            await DisposeEntryAsync(entry).ConfigureAwait(false);
        }

        _initializeGate.Dispose();
    }

    private async Task InitializeEntryAsync(WidgetEntry entry, CancellationToken ct)
    {
        if (entry.IsInitialized || entry.IsFaulted)
        {
            return;
        }

        IWidgetModule instance;
        try
        {
            instance = entry.Registration.Create();
        }
        catch (Exception exception)
        {
            MarkFaulted(entry, "create", exception);
            return;
        }

        var context = new WidgetContext(entry, this, _events, _theme, _island, _logger);
        entry.Instance = instance;

        try
        {
            await instance.InitializeAsync(context, ct).ConfigureAwait(false);
            entry.IsInitialized = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            MarkFaulted(entry, nameof(IWidgetModule.InitializeAsync), exception);
        }
    }

    private async Task DeactivateAsync(string widgetId)
    {
        if (!_entries.TryGetValue(widgetId, out var entry))
        {
            return;
        }

        lock (_sync)
        {
            if (string.Equals(_activeWidgetId, widgetId, StringComparison.Ordinal))
            {
                _activeWidgetId = null;
            }
        }

        var instance = entry.Instance;
        if (instance is null || entry.IsFaulted)
        {
            return;
        }

        try
        {
            await instance.DeactivateAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            MarkFaulted(entry, nameof(IWidgetModule.DeactivateAsync), exception);
        }
    }

    private async Task DeactivateAfterShellStateChangeAsync()
    {
        try
        {
            await DeactivateActiveAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to deactivate the active widget after a shell state change.", exception);
        }
    }

    private async Task DisposeEntryAsync(WidgetEntry entry)
    {
        var instance = entry.Instance;
        entry.Instance = null;
        entry.IsInitialized = false;

        if (instance is null)
        {
            _island.UpdateCompactPill(entry.WidgetId, null);
            return;
        }

        try
        {
            await instance.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Warn($"Widget '{entry.WidgetId}' failed during disposal.", exception);
        }
        finally
        {
            _island.UpdateCompactPill(entry.WidgetId, null);
        }
    }

    private void MarkFaulted(WidgetEntry entry, string operation, Exception exception)
    {
        entry.IsFaulted = true;
        _logger.Error($"Widget '{entry.WidgetId}' faulted during {operation}.", exception);
    }

    private static FaultedWidgetFallbackView CreateFallback(WidgetEntry entry) =>
        new(
            entry.WidgetId,
            entry.Registration.Metadata.DisplayName,
            "This widget stopped responding. Reload it to try again.");

    private static WidgetConfig CreateDefaultConfig(WidgetDescriptor metadata) =>
        new()
        {
            WidgetId = metadata.WidgetId,
            IsEnabled = true,
            SortOrder = metadata.DefaultOrder,
            AllowPeekNotifications = metadata.SupportsNotificationPeek,
            SettingsJson = "{}",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

    private WidgetEntry GetEntry(string widgetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        if (_entries.TryGetValue(widgetId, out var entry))
        {
            return entry;
        }

        throw new KeyNotFoundException($"Widget '{widgetId}' is not registered.");
    }

    private List<WidgetEntry> SnapshotEntries()
    {
        lock (_sync)
        {
            return _entries.Values.ToList();
        }
    }

    private sealed class WidgetEntry
    {
        public WidgetEntry(WidgetModuleRegistration registration)
        {
            Registration = registration;
        }

        public WidgetModuleRegistration Registration { get; }

        public string WidgetId => Registration.Metadata.WidgetId;

        public WidgetConfig? Config { get; set; }

        public IWidgetModule? Instance { get; set; }

        public bool IsInitialized { get; set; }

        public bool IsFaulted { get; set; }
    }

    private sealed class WidgetContext : IWidgetContext
    {
        public WidgetContext(
            WidgetEntry entry,
            WidgetRegistry registry,
            IEventDispatcher events,
            IThemeTokenProvider theme,
            IIslandAttentionRequester island,
            ILogger logger)
        {
            WidgetId = entry.WidgetId;
            Events = events;
            Settings = new WidgetSettingsStore(registry, entry);
            Theme = theme;
            Island = new ScopedIslandAttentionRequester(entry.WidgetId, island);
            Logger = logger;
        }

        public string WidgetId { get; }

        public IEventDispatcher Events { get; }

        public IWidgetSettingsStore Settings { get; }

        public IIslandAttentionRequester Island { get; }

        public IThemeTokenProvider Theme { get; }

        public ILogger Logger { get; }
    }

    private sealed class ScopedIslandAttentionRequester(string widgetId, IIslandAttentionRequester inner)
        : IIslandAttentionRequester
    {
        public void RequestPeekNotification(NotificationPriority priority, TimeSpan ttl) =>
            inner.RequestPeekNotification(priority, ttl);

        public void UpdateCompactPill(string? badgeText) =>
            inner.UpdateCompactPill(widgetId, badgeText);

        public void UpdateCompactPill(string? targetWidgetId, string? badgeText) =>
            inner.UpdateCompactPill(targetWidgetId ?? widgetId, badgeText);
    }

    private sealed class WidgetSettingsStore : IWidgetSettingsStore
    {
        private readonly WidgetRegistry _registry;
        private readonly WidgetEntry _entry;

        public WidgetSettingsStore(WidgetRegistry registry, WidgetEntry entry)
        {
            _registry = registry;
            _entry = entry;
        }

        public Task<string?> ReadJsonAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_entry.Config?.SettingsJson);
        }

        public async Task WriteJsonAsync(string settingsJson, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(settingsJson);
            ct.ThrowIfCancellationRequested();

            try
            {
                using var document = JsonDocument.Parse(settingsJson);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException("Widget settings must be valid JSON.", nameof(settingsJson), exception);
            }

            var current = _entry.Config ?? CreateDefaultConfig(_entry.Registration.Metadata);
            var updated = current with { SettingsJson = settingsJson, UpdatedAtUtc = DateTimeOffset.UtcNow };
            _entry.Config = updated;
            await _registry._configRepository.SaveAsync(updated).ConfigureAwait(false);
        }

        public Task ResetAsync(CancellationToken ct = default) => WriteJsonAsync("{}", ct);
    }
}
