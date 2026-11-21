using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Core.Widgets;
using Shima.Infrastructure.Configuration;
using Shima.Infrastructure.Persistence;
using Shima.Infrastructure.Storage;
using Shima.Presentation.Shell;
using Shima.WindowsInterop.Overlay;
using Shima.WindowsInterop.System;

namespace Shima.App;

public partial class App : Application
{
    public const string DefaultGlobalHotkey = "Ctrl+Alt+Space";

    private SingleInstanceGuard? _singleInstanceGuard;
    private ServiceProvider? _serviceProvider;
    private ILogger? _logger;

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
            var logFilePath = Path.Combine(dataRoot, "logs", "shima.log");

            var services = new ServiceCollection();
            ConfigureServices(services, dataRoot, databasePath, logFilePath);

            _serviceProvider = services.BuildServiceProvider();
            _logger = _serviceProvider.GetRequiredService<ILogger>();

            var migrationRunner = _serviceProvider.GetRequiredService<SqliteMigrationRunner>();
            await migrationRunner.InitializeAsync().ConfigureAwait(true);

            var preferencesStore = _serviceProvider.GetRequiredService<AppPreferencesStore>();
            var preferences = await preferencesStore.LoadAsync().ConfigureAwait(true);

            var widgetRegistry = _serviceProvider.GetRequiredService<WidgetRegistry>();
            await widgetRegistry.InitializeAsync().ConfigureAwait(true);

            var islandWindow = _serviceProvider.GetRequiredService<IslandWindow>();
            islandWindow.ApplyBehaviorPreferences(
                MapMonitorSelectionMode(preferences.TargetMonitorMode),
                preferences.SpecificMonitorDeviceId,
                preferences.ExpandOnHover,
                preferences.HoverExpandDelayMs,
                preferences.AutoCollapseDelayMs,
                preferences.FullscreenBreakthroughAlerts);

            var slots = widgetRegistry.Widgets
                .Where(widget => widget.IsEnabled)
                .Select(widget => new WidgetSlotItem(widget.Metadata.WidgetId, widget.Metadata.DisplayName))
                .ToArray();
            islandWindow.SetWidgetSlots(slots, widgetRegistry.ActiveWidgetId);

            islandWindow.Show();

            var hotkeyService = _serviceProvider.GetRequiredService<IGlobalHotkeyService>();
            var gesture = string.IsNullOrWhiteSpace(preferences.GlobalToggleHotkey)
                ? DefaultGlobalHotkey
                : preferences.GlobalToggleHotkey;
            if (!hotkeyService.TryRegister(islandWindow.WindowHandle, gesture, out var hotkeyError))
            {
                _logger.Warn($"Global hotkey '{gesture}' could not be registered: {hotkeyError}");
            }

            var trayService = _serviceProvider.GetRequiredService<SystemTrayService>();
            trayService.Attach(islandWindow, () => Dispatcher.Invoke(() => Shutdown(0)));
        }
        catch (Exception ex)
        {
            _logger?.Critical("Fatal error during Shima startup.", ex);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
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

    private static void ConfigureServices(
        IServiceCollection services,
        string dataRoot,
        string databasePath,
        string logFilePath)
    {
        services.AddSingleton<ILogger>(_ => new SanitizedFileLogger(logFilePath));
        services.AddSingleton<IAtomicConfigStore<AppPreferences>>(_ => new AtomicConfigStore<AppPreferences>(dataRoot));
        services.AddSingleton<AppPreferencesStore>();
        services.AddSingleton(_ => new SqliteMigrationRunner(databasePath));
        services.AddSingleton<IWidgetConfigRepository>(_ => new SqliteWidgetConfigRepository(databasePath));

        services.AddSingleton<IEventDispatcher, InMemoryEventDispatcher>();
        services.AddSingleton<IOverlayPinController, OverlayPinController>();
        services.AddSingleton<IIslandStateManager, IslandStateManager>();
        services.AddSingleton<IIslandAttentionRequester, IslandAttentionRequester>();
        services.AddSingleton<IThemeTokenProvider, DefaultThemeTokenProvider>();
        services.AddSingleton<IEnumerable<WidgetModuleRegistration>>(Array.Empty<WidgetModuleRegistration>());
        services.AddSingleton<WidgetRegistry>();

        services.AddSingleton<IOverlayWindowAdapter, OverlayWindowAdapter>();
        services.AddSingleton<IDisplayTopologyMonitor, DisplayTopologyMonitor>();
        services.AddSingleton<IFullscreenDetector, FullscreenDetector>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<IPowerAndMotionMonitor, PowerAndMotionMonitor>();
        services.AddSingleton<IStartupRegistrationAdapter, StartupRegistrationAdapter>();

        services.AddSingleton<SystemTrayService>();
        services.AddSingleton<IslandWindow>();
    }

    private static MonitorSelectionMode MapMonitorSelectionMode(TargetMonitorMode mode) => mode switch
    {
        TargetMonitorMode.ActiveCursor => MonitorSelectionMode.ActiveCursor,
        TargetMonitorMode.SpecificMonitorId => MonitorSelectionMode.SpecificMonitorId,
        _ => MonitorSelectionMode.Primary
    };
}
