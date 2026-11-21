using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Shima.Core.Appearance;
using Shima.Core.Color;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Core.Widgets;
using Shima.Infrastructure.Appearance;
using Shima.Infrastructure.Color;
using Shima.Infrastructure.Configuration;
using Shima.Infrastructure.Persistence;
using Shima.Infrastructure.Repositories;
using Shima.Infrastructure.Storage;
using Shima.Presentation.Settings;
using Shima.Presentation.Shell;
using Shima.Presentation.Theming;
using Shima.Widgets.Clock;
using Shima.WindowsInterop.Overlay;
using Shima.WindowsInterop.System;

namespace Shima.App;

public partial class App : Application
{
    public const string DefaultGlobalHotkey = "Ctrl+Alt+Space";

    private SingleInstanceGuard? _singleInstanceGuard;
    private ServiceProvider? _serviceProvider;
    private ILogger? _logger;
    private SettingsWindow? _settingsWindow;
    private IDisposable? _openSettingsSubscription;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceGuard = new SingleInstanceGuard();
        if (!_singleInstanceGuard.IsPrimaryInstance)
        {
            Shutdown(0);
            return;
        }

        try
        {
            var dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Shima");
            Directory.CreateDirectory(dataRoot);

            var databasePath = Path.Combine(dataRoot, "shima.db");
            var logDirectory = Path.Combine(dataRoot, "logs");
            var logFilePath = Path.Combine(logDirectory, "shima.log");

            var services = new ServiceCollection();
            ConfigureServices(services, dataRoot, databasePath, logDirectory, logFilePath);

            _serviceProvider = services.BuildServiceProvider();
            _logger = _serviceProvider.GetRequiredService<ILogger>();

            var migrationRunner = _serviceProvider.GetRequiredService<SqliteMigrationRunner>();
            await migrationRunner.InitializeAsync().ConfigureAwait(true);

            var preferencesService = _serviceProvider.GetRequiredService<IAppPreferencesService>();
            await preferencesService.InitializeAsync().ConfigureAwait(true);
            var preferences = preferencesService.Current;

            var appearanceManager = _serviceProvider.GetRequiredService<IAppearanceManager>();
            await appearanceManager.InitializeAsync().ConfigureAwait(true);

            var widgetRegistry = _serviceProvider.GetRequiredService<WidgetRegistry>();
            await widgetRegistry.InitializeAsync().ConfigureAwait(true);

            var islandWindow = _serviceProvider.GetRequiredService<IslandWindow>();
            var themeRenderer = _serviceProvider.GetRequiredService<ThemeRenderer>();
            if (Current?.Resources is not null)
            {
                themeRenderer.AttachResourceDictionary(Current.Resources);
            }

            themeRenderer.AttachFrameworkElement(islandWindow);
            themeRenderer.AttachFrameworkElement(islandWindow.WidgetHost);
            themeRenderer.ApplyNow();

            ApplyPreferencesToShell(islandWindow, preferences);
            SyncIslandWidgetSlots(islandWindow, widgetRegistry);

            var stateManager = _serviceProvider.GetRequiredService<IIslandStateManager>();
            var powerMonitor = _serviceProvider.GetRequiredService<IPowerAndMotionMonitor>();
            var coordinator = _serviceProvider.GetRequiredService<WidgetRegistrySettingsCoordinator>();
            var eventDispatcher = _serviceProvider.GetRequiredService<IEventDispatcher>();

            stateManager.StateChanged += (_, args) =>
            {
                widgetRegistry.OnShellStateChanged(args.NewState);
                if (args.NewState == IslandShellState.Expanded)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        var selectedId = islandWindow.WidgetHost.SelectedWidgetId ?? widgetRegistry.ActiveWidgetId;
                        if (!string.IsNullOrWhiteSpace(selectedId))
                        {
                            _ = widgetRegistry.ActivateAsync(selectedId, WidgetActivationReason.UserSelection);
                        }
                    });
                }
            };

            powerMonitor.PowerProfileChanged += (_, profile) => widgetRegistry.OnPowerProfileChanged(profile);
            islandWindow.WidgetHost.SelectedWidgetChanged += (_, widgetId) =>
                _ = widgetRegistry.ActivateAsync(widgetId, WidgetActivationReason.UserSelection);
            coordinator.WidgetsChanged += (_, _) =>
                Dispatcher.BeginInvoke(() => SyncIslandWidgetSlots(islandWindow, widgetRegistry));
            _openSettingsSubscription = eventDispatcher.Subscribe<OpenSettingsRequestedEvent>(
                evt => Dispatcher.BeginInvoke(() => ShowOrActivateSettings(evt.SectionTag)));

            islandWindow.Show();

            var hotkeyService = _serviceProvider.GetRequiredService<IGlobalHotkeyService>();
            TryApplyGlobalHotkey(hotkeyService, islandWindow.WindowHandle, preferences.GlobalToggleHotkey);

            preferencesService.PreferencesChanged += (_, updated) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ApplyPreferencesToShell(islandWindow, updated);
                    TryApplyGlobalHotkey(hotkeyService, islandWindow.WindowHandle, updated.GlobalToggleHotkey);
                });
            };

            var trayService = _serviceProvider.GetRequiredService<SystemTrayService>();
            trayService.Attach(
                islandWindow,
                () => Dispatcher.Invoke(() => Shutdown(0)),
                () => Dispatcher.BeginInvoke(() => ShowOrActivateSettings(sectionTag: null)));
        }
        catch (Exception ex)
        {
            _logger?.Critical("Fatal error during Shima startup.", ex);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _openSettingsSubscription?.Dispose();
        _openSettingsSubscription = null;

        if (_settingsWindow is not null)
        {
            try
            {
                _settingsWindow.Close();
            }
            catch
            {
                // Ignore Settings teardown errors on shutdown.
            }

            _settingsWindow = null;
        }

        if (_serviceProvider is not null)
        {
            try
            {
                var window = _serviceProvider.GetService<IslandWindow>();
                if (window is { IsLoaded: true })
                {
                    window.Close();
                }
            }
            catch
            {
                // Ignore window teardown failures on process exit.
            }

            _serviceProvider.Dispose();
            _serviceProvider = null;
        }

        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;

        base.OnExit(e);
    }

    private void ShowOrActivateSettings(string? sectionTag)
    {
        if (_serviceProvider is null)
        {
            return;
        }

        if (_settingsWindow is { IsLoaded: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            if (!string.IsNullOrWhiteSpace(sectionTag))
            {
                _settingsWindow.SelectSection(sectionTag);
            }

            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        if (!string.IsNullOrWhiteSpace(sectionTag))
        {
            _settingsWindow.SelectSection(sectionTag);
        }

        _settingsWindow.Activate();
    }

    private static void SyncIslandWidgetSlots(IslandWindow islandWindow, WidgetRegistry widgetRegistry)
    {
        var slots = widgetRegistry.Widgets
            .Where(widget => widget.IsEnabled)
            .OrderBy(widget => widget.SortOrder)
            .Select(widget => new WidgetSlotItem(
                widget.Metadata.WidgetId,
                widget.Metadata.DisplayName,
                widgetRegistry.CreateExpandedView(widget.Metadata.WidgetId)))
            .ToArray();

        islandWindow.SetWidgetSlots(slots, widgetRegistry.ActiveWidgetId);
    }

    private static void ApplyPreferencesToShell(IslandWindow islandWindow, AppPreferencesSnapshot preferences) =>
        islandWindow.ApplyBehaviorPreferences(
            preferences.MonitorSelectionMode,
            preferences.SpecificMonitorDeviceId,
            preferences.ExpandOnHover,
            preferences.HoverExpandDelayMs,
            preferences.AutoCollapseDelayMs,
            preferences.FullscreenBreakthroughAlerts);

    private void TryApplyGlobalHotkey(IGlobalHotkeyService hotkeyService, nint hwnd, string? rawGesture)
    {
        if (hwnd == 0)
        {
            return;
        }

        var gesture = string.IsNullOrWhiteSpace(rawGesture) ? DefaultGlobalHotkey : rawGesture;
        if (!hotkeyService.TryRegister(hwnd, gesture, out var hotkeyError))
        {
            _logger?.Warn($"Global hotkey '{gesture}' could not be registered: {hotkeyError}");
        }
    }

    private static void ConfigureServices(
        IServiceCollection services,
        string dataRoot,
        string databasePath,
        string logDirectory,
        string logFilePath)
    {
        services.AddSingleton<ILogger>(_ => new SanitizedFileLogger(logFilePath));
        services.AddSingleton<IDiagnosticLogService>(_ => new DiagnosticLogService(logDirectory));
        services.AddSingleton<ISecureSecretStore, DpapiSecretStore>();

        services.AddSingleton<IAtomicConfigStore<AppPreferences>>(_ => new AtomicConfigStore<AppPreferences>(dataRoot));
        services.AddSingleton<AppPreferencesStore>();
        services.AddSingleton<IAppPreferencesService, AppPreferencesService>();

        services.AddSingleton<IAtomicConfigStore<AppearanceConfiguration>>(_ => new AtomicConfigStore<AppearanceConfiguration>(dataRoot));
        services.AddSingleton<IAppearanceConfigRepository, AtomicAppearanceConfigRepository>();
        services.AddSingleton<IAtomicConfigStore<PresetCatalog>>(_ => new AtomicConfigStore<PresetCatalog>(dataRoot));
        services.AddSingleton<IPresetCatalogRepository, AtomicPresetCatalogRepository>();

        services.AddSingleton<ColorProcessingService>();
        services.AddSingleton<IColorProcessingService>(sp => sp.GetRequiredService<ColorProcessingService>());
        services.AddSingleton<IContrastAuditor>(sp => sp.GetRequiredService<ColorProcessingService>());
        services.AddSingleton<IPresetManager, PresetManager>();
        services.AddSingleton<AppearanceManager>();
        services.AddSingleton<IAppearanceManager>(sp => sp.GetRequiredService<AppearanceManager>());
        services.AddSingleton<IThemeTokenProvider>(sp => sp.GetRequiredService<AppearanceManager>());
        services.AddSingleton<ThemeRenderer>();

        services.AddSingleton(_ => new SqliteMigrationRunner(databasePath));
        services.AddSingleton<IWidgetConfigRepository>(_ => new SqliteWidgetConfigRepository(databasePath));
        services.AddSingleton<IClockRepository>(_ => new ClockRepository(databasePath));

        services.AddSingleton<IEventDispatcher, InMemoryEventDispatcher>();
        services.AddSingleton<IOverlayPinController, OverlayPinController>();
        services.AddSingleton<IIslandStateManager, IslandStateManager>();
        services.AddSingleton<IIslandAttentionRequester, IslandAttentionRequester>();
        services.AddSingleton<IEnumerable<WidgetModuleRegistration>>(sp =>
        [
            new WidgetModuleRegistration(
                new WidgetDescriptor
                {
                    WidgetId = ClockWidgetModule.Id,
                    DisplayName = "Clock",
                    DefaultOrder = 0,
                    DefaultAccentHex = "#FFFFFF"
                },
                () => new ClockWidgetModule(sp.GetRequiredService<IClockRepository>()))
        ]);
        services.AddSingleton<WidgetRegistry>();
        services.AddSingleton<WidgetRegistrySettingsCoordinator>();
        services.AddSingleton<IWidgetSettingsCoordinator>(sp => sp.GetRequiredService<WidgetRegistrySettingsCoordinator>());

        services.AddSingleton<IOverlayWindowAdapter, OverlayWindowAdapter>();
        services.AddSingleton<IDisplayTopologyMonitor, DisplayTopologyMonitor>();
        services.AddSingleton<IFullscreenDetector, FullscreenDetector>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<IPowerAndMotionMonitor, PowerAndMotionMonitor>();
        services.AddSingleton<IStartupRegistrationAdapter, StartupRegistrationAdapter>();

        services.AddSingleton<SystemTrayService>();
        services.AddSingleton<IslandWindow>();
        services.AddTransient<SettingsWindow>();
    }
}
