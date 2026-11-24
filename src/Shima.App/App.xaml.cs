using System.IO;
using System.Security.Cryptography;
using System.Text;
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
using Shima.Infrastructure.Services;
using Shima.Infrastructure.Storage;
using Shima.Presentation.Settings;
using Shima.Presentation.Shell;
using Shima.Presentation.Theming;
using Shima.Widgets.Clipboard;
using Shima.Widgets.Clock;
using Shima.Widgets.Developer;
using Shima.Widgets.FocusTimer;
using Shima.Widgets.Inbox;
using Shima.Widgets.Notes;
using Shima.Widgets.NowPlaying;
using Shima.Widgets.VolumeMixer;
using Shima.WindowsInterop.Agents;
using Shima.WindowsInterop.Audio;
using Shima.WindowsInterop.Clipboard;
using Shima.WindowsInterop.Media;
using Shima.WindowsInterop.Notifications;
using Shima.WindowsInterop.Overlay;
using Shima.WindowsInterop.System;
using Shima.WindowsInterop.Zen;

namespace Shima.App;

public partial class App : Application
{
    public const string DefaultGlobalHotkey = "Ctrl+Alt+Space";

    private SingleInstanceGuard? _singleInstanceGuard;
    private ServiceProvider? _serviceProvider;
    private ILogger? _logger;
    private SettingsWindow? _settingsWindow;
    private IDisposable? _openSettingsSubscription;
    private IDisposable? _collapsedIndicatorSubscription;
    private IDisposable? _collapsedHoveredSubscription;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataRoot = ResolveDataRoot(e.Args, out var isIsolatedProfile);
        var mutexName = isIsolatedProfile
            ? $"Local\\Shima.SingleInstance.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot.ToLowerInvariant())))[..16]}"
            : SingleInstanceGuard.DefaultMutexName;

        _singleInstanceGuard = new SingleInstanceGuard(mutexName);
        if (!_singleInstanceGuard.IsPrimaryInstance)
        {
            Shutdown(0);
            return;
        }

        try
        {
            Directory.CreateDirectory(dataRoot);

            var databasePath = Path.Combine(dataRoot, "shima.db");
            var logDirectory = Path.Combine(dataRoot, "logs");
            var logFilePath = Path.Combine(logDirectory, "shima.log");

            var services = new ServiceCollection();
            ConfigureServices(services, dataRoot, databasePath, logDirectory, logFilePath);

            _serviceProvider = services.BuildServiceProvider();
            _logger = _serviceProvider.GetRequiredService<ILogger>();

            var zenDisplayEffect = _serviceProvider.GetRequiredService<IZenDisplayEffectAdapter>();
            if (zenDisplayEffect.RecoverFromStartupIfNeeded())
            {
                _logger.Warn("Recovered Zen display filter cleanly from runtime journal after unexpected termination.");
            }

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledDomainException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            var migrationRunner = _serviceProvider.GetRequiredService<SqliteMigrationRunner>();
            await migrationRunner.InitializeAsync().ConfigureAwait(true);

            var preferencesService = _serviceProvider.GetRequiredService<IAppPreferencesService>();
            await preferencesService.InitializeAsync().ConfigureAwait(true);
            var preferences = preferencesService.Current;

            var appearanceManager = _serviceProvider.GetRequiredService<IAppearanceManager>();
            await appearanceManager.InitializeAsync().ConfigureAwait(true);

            var widgetRegistry = _serviceProvider.GetRequiredService<WidgetRegistry>();
            var eventDispatcher = _serviceProvider.GetRequiredService<IEventDispatcher>();
            var islandWindow = _serviceProvider.GetRequiredService<IslandWindow>();

            void SyncCollapsedStatusView()
            {
                var devWidget = widgetRegistry.Widgets.FirstOrDefault(w =>
                    string.Equals(w.Metadata.WidgetId, DeveloperWidgetModule.Id, StringComparison.Ordinal)
                    && w.IsEnabled
                    && !w.IsFaulted);
                var statusView = devWidget is not null
                    ? widgetRegistry.CreateCollapsedStatusView(DeveloperWidgetModule.Id)
                    : null;
                islandWindow.SetCollapsedStatusContent(statusView);
            }

            _collapsedIndicatorSubscription = eventDispatcher.Subscribe<CollapsedStatusIndicatorChangedEvent>(
                evt => Dispatcher.BeginInvoke(() =>
                {
                    if (!evt.IsVisible)
                    {
                        islandWindow.SetCollapsedStatusContent(null);
                    }
                    else
                    {
                        islandWindow.SetCollapsedStatusContent(widgetRegistry.CreateCollapsedStatusView(evt.WidgetId));
                    }
                }));

            _collapsedHoveredSubscription = eventDispatcher.Subscribe<IslandCollapsedHoveredEvent>(
                _ => widgetRegistry.NotifyCollapsedNotchHovered());

            await widgetRegistry.InitializeAsync().ConfigureAwait(true);

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
            SyncCollapsedStatusView();

            var stateManager = _serviceProvider.GetRequiredService<IIslandStateManager>();
            var powerMonitor = _serviceProvider.GetRequiredService<IPowerAndMotionMonitor>();
            var coordinator = _serviceProvider.GetRequiredService<WidgetRegistrySettingsCoordinator>();

            object? cachedCompactView = null;
            string? cachedCompactWidgetId = null;
            object? cachedCompanionView = null;
            string? cachedCompanionWidgetId = null;

            void SyncCompactActiveView()
            {
                var widgetId = stateManager.CompactActiveWidgetId;
                if (string.IsNullOrWhiteSpace(widgetId))
                {
                    cachedCompactWidgetId = null;
                    cachedCompactView = null;
                    cachedCompanionWidgetId = null;
                    cachedCompanionView = null;
                    islandWindow.SetCompactActiveContent(null);
                    islandWindow.SetCompactCompanionContent(null);
                    return;
                }

                if (!string.Equals(cachedCompactWidgetId, widgetId, StringComparison.Ordinal))
                {
                    cachedCompactWidgetId = widgetId;
                    cachedCompactView = widgetRegistry.CreateCompactActiveView(widgetId);
                }

                var companionWidgetId = stateManager.CompactCompanionWidgetId;
                if (string.IsNullOrWhiteSpace(companionWidgetId))
                {
                    cachedCompanionWidgetId = null;
                    cachedCompanionView = null;
                }
                else if (!string.Equals(cachedCompanionWidgetId, companionWidgetId, StringComparison.Ordinal))
                {
                    cachedCompanionWidgetId = companionWidgetId;
                    cachedCompanionView = widgetRegistry.CreateCompactCompanionView(companionWidgetId);
                }

                islandWindow.SetCompactActiveContent(cachedCompactView);
                islandWindow.SetCompactCompanionContent(cachedCompanionView);
            }

            SyncCompactActiveView();
            stateManager.CompactPillUpdated += (_, _) => Dispatcher.BeginInvoke(SyncCompactActiveView);

            string? lastManualWidgetId = islandWindow.WidgetHost.SelectedWidgetId ?? widgetRegistry.ActiveWidgetId;
            string? autoSwitchedWidgetId = null;
            var suppressManualTracking = false;

            stateManager.StateChanged += (_, args) =>
            {
                widgetRegistry.OnShellStateChanged(args.NewState);
                if (args.NewState == IslandShellState.Expanded)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        var autoSelectId = widgetRegistry.ResolveAutoSelectWidgetIdOnExpand();
                        if (!string.IsNullOrWhiteSpace(autoSelectId))
                        {
                            suppressManualTracking = true;
                            try
                            {
                                autoSwitchedWidgetId = autoSelectId;
                                islandWindow.WidgetHost.SelectWidget(autoSelectId);
                            }
                            finally
                            {
                                suppressManualTracking = false;
                            }
                        }
                        else if (autoSwitchedWidgetId is not null)
                        {
                            var restoreId = lastManualWidgetId ?? islandWindow.WidgetHost.SelectedWidgetId;
                            autoSwitchedWidgetId = null;
                            if (!string.IsNullOrWhiteSpace(restoreId))
                            {
                                suppressManualTracking = true;
                                try
                                {
                                    islandWindow.WidgetHost.SelectWidget(restoreId);
                                }
                                finally
                                {
                                    suppressManualTracking = false;
                                }
                            }
                        }

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
            {
                if (!suppressManualTracking)
                {
                    lastManualWidgetId = widgetId;
                    autoSwitchedWidgetId = null;
                }

                _ = widgetRegistry.ActivateAsync(widgetId, WidgetActivationReason.UserSelection);
            };
            coordinator.WidgetsChanged += (_, _) =>
                Dispatcher.BeginInvoke(() =>
                {
                    cachedCompactWidgetId = null;
                    cachedCompactView = null;
                    cachedCompanionWidgetId = null;
                    cachedCompanionView = null;
                    SyncIslandWidgetSlots(islandWindow, widgetRegistry);
                    SyncCompactActiveView();
                    SyncCollapsedStatusView();
                });
            _openSettingsSubscription = eventDispatcher.Subscribe<OpenSettingsRequestedEvent>(
                evt => Dispatcher.BeginInvoke(() => ShowOrActivateSettings(evt.SectionTag)));

            islandWindow.Show();

            var clipboardMonitor = _serviceProvider.GetRequiredService<IClipboardMonitorAdapter>();
            clipboardMonitor.AttachHostWindow(islandWindow.WindowHandle);
            islandWindow.AddWindowMessageHook(clipboardMonitor.HandleWindowMessage);

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

            islandWindow.PositionCommitted += (_, pos) =>
            {
                _ = PersistCommittedPositionAsync(preferencesService, pos, _logger);
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
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledDomainException;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        _openSettingsSubscription?.Dispose();
        _openSettingsSubscription = null;
        _collapsedIndicatorSubscription?.Dispose();
        _collapsedIndicatorSubscription = null;
        _collapsedHoveredSubscription?.Dispose();
        _collapsedHoveredSubscription = null;

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
            var provider = _serviceProvider;
            _serviceProvider = null;

            try
            {
                provider.GetService<IZenAudioSynthAdapter>()?.Stop();
                provider.GetService<IZenDisplayEffectAdapter>()?.MarkCleanExit();

                var window = provider.GetService<IslandWindow>();
                var clipboardMonitor = provider.GetService<IClipboardMonitorAdapter>();
                if (window is not null && clipboardMonitor is not null)
                {
                    window.RemoveWindowMessageHook(clipboardMonitor.HandleWindowMessage);
                }

                if (window is { IsLoaded: true })
                {
                    window.Close();
                }
            }
            catch
            {
                // Ignore window teardown failures on process exit.
            }

            try
            {
                provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore container disposal errors on process exit.
            }
        }

        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;

        base.OnExit(e);
    }

    private void OnUnhandledDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _logger?.Critical("Unhandled AppDomain exception.", ex);
        }

        RestoreZenHardwareSafely();
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Critical("Unhandled WPF Dispatcher exception.", e.Exception);
        RestoreZenHardwareSafely();
    }

    private void RestoreZenHardwareSafely()
    {
        try
        {
            _serviceProvider?.GetService<IZenAudioSynthAdapter>()?.Stop();
            _serviceProvider?.GetService<IZenDisplayEffectAdapter>()?.RestoreDisplay();
        }
        catch
        {
            // Never throw inside unhandled exception display recovery hooks.
        }
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
                Content: null,
                ContentFactory: () => widgetRegistry.CreateExpandedView(widget.Metadata.WidgetId)))
            .ToArray();

        islandWindow.SetWidgetSlots(slots, widgetRegistry.ActiveWidgetId);
    }

    private static async Task PersistCommittedPositionAsync(
        IAppPreferencesService preferencesService,
        IslandPositionCommittedEventArgs pos,
        ILogger? logger)
    {
        try
        {
            var next = preferencesService.Current with
            {
                IslandOffsetXDip = pos.OffsetXDip,
                IslandFloatingTopOffsetDip = pos.FloatingTopOffsetDip
            };
            await preferencesService.SaveAsync(next).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.Warn("Failed to persist committed island position.", ex);
        }
    }

    private static void ApplyPreferencesToShell(IslandWindow islandWindow, AppPreferencesSnapshot preferences) =>
        islandWindow.ApplyBehaviorPreferences(
            preferences.MonitorSelectionMode,
            preferences.SpecificMonitorDeviceId,
            preferences.ExpandOnHover,
            preferences.HoverExpandDelayMs,
            preferences.AutoCollapseDelayMs,
            preferences.FullscreenBreakthroughAlerts,
            preferences.IslandStyle,
            preferences.LockIslandPosition,
            preferences.IslandOffsetXDip,
            preferences.IslandFloatingTopOffsetDip);

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

    private static string ResolveDataRoot(string[] args, out bool isIsolatedProfile)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--data-dir", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                isIsolatedProfile = true;
                return Path.GetFullPath(args[i + 1].Trim().Trim('"'));
            }

            const string prefix = "--data-dir=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = arg[prefix.Length..].Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    isIsolatedProfile = true;
                    return Path.GetFullPath(candidate);
                }
            }
        }

        isIsolatedProfile = false;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shima");
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
        services.AddSingleton<IUpdateService>(sp => new UpdateServiceAdapter(
            sp.GetRequiredService<IAppPreferencesService>(),
            dataRoot,
            logger: sp.GetRequiredService<ILogger>()));
        services.AddSingleton<ILicenseService>(sp => new LicenseServiceAdapter(
            sp.GetRequiredService<IAppPreferencesService>(),
            logger: sp.GetRequiredService<ILogger>()));

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
        services.AddSingleton<INoteRepository>(_ => new NoteRepository(databasePath));
        services.AddSingleton<IClipboardRepository>(_ => new ClipboardRepository(databasePath, dataRoot));
        services.AddSingleton<IInboxRepository>(_ => new InboxRepository(databasePath, dataRoot));
        services.AddSingleton<IFocusRepository>(_ => new FocusRepository(databasePath));
        services.AddSingleton<IAgentSessionLogRepository>(_ => new AgentSessionLogRepository(databasePath));

        services.AddSingleton<IEventDispatcher, InMemoryEventDispatcher>();
        services.AddSingleton<IOverlayPinController, OverlayPinController>();
        services.AddSingleton<IIslandStateManager, IslandStateManager>();
        services.AddSingleton<IIslandAttentionRequester, IslandAttentionRequester>();
        services.AddSingleton<IMediaSessionAdapter, MediaSessionAdapter>();
        services.AddSingleton<IAudioMixerAdapter, AudioMixerAdapter>();
        services.AddSingleton<IClipboardMonitorAdapter, ClipboardMonitorAdapter>();
        services.AddSingleton<INotificationListenerAdapter, NotificationListenerAdapter>();
        services.AddSingleton<IZenAudioSynthAdapter>(sp => new ZenAudioSynthAdapter(sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IZenDisplayEffectAdapter>(sp => new ZenDisplayEffectAdapter(dataRoot, sp.GetRequiredService<ILogger>()));
        services.AddSingleton<ClaudeCodeAdapter>();
        services.AddSingleton<OpenCodeAdapter>();
        services.AddSingleton<IEnumerable<ICodingAgentAdapter>>(sp =>
        [
            sp.GetRequiredService<ClaudeCodeAdapter>(),
            sp.GetRequiredService<OpenCodeAdapter>()
        ]);

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
                () => new ClockWidgetModule(sp.GetRequiredService<IClockRepository>())),
            new WidgetModuleRegistration(
                new WidgetDescriptor
                {
                    WidgetId = NowPlayingWidgetModule.Id,
                    DisplayName = "Now Playing",
                    DefaultOrder = 1,
                    RequiresBackgroundSubscription = true,
                    SupportsCompactActiveState = true,
                    SupportsNotificationPeek = false,
                    DefaultAccentHex = "#34D399"
                },
                () => new NowPlayingWidgetModule(sp.GetRequiredService<IMediaSessionAdapter>())),
            new WidgetModuleRegistration(
                new WidgetDescriptor
                {
                    WidgetId = FocusTimerWidgetModule.Id,
                    DisplayName = "Focus Timer",
                    DefaultOrder = 2,
                    RequiresBackgroundSubscription = false,
                    SupportsCompactActiveState = true,
                    SupportsNotificationPeek = true,
                    DefaultAccentHex = "#FB923C"
                },
                () => new FocusTimerWidgetModule(
                    sp.GetRequiredService<IFocusRepository>(),
                    sp.GetRequiredService<IZenAudioSynthAdapter>(),
                    sp.GetRequiredService<IZenDisplayEffectAdapter>())),
            new WidgetModuleRegistration(
                new WidgetDescriptor
                {
                    WidgetId = NotesWidgetModule.Id,
                    DisplayName = "Notes",
                    DefaultOrder = 3,
                    RequiresBackgroundSubscription = false,
                    SupportsCompactActiveState = false,
                    SupportsNotificationPeek = false,
                    DefaultAccentHex = "#FBBF24"
                },
                () => new NotesWidgetModule(
                    sp.GetRequiredService<INoteRepository>(),
                    sp.GetRequiredService<IClipboardMonitorAdapter>())),
            new WidgetModuleRegistration(
                new WidgetDescriptor
                {
                    WidgetId = InboxWidgetModule.Id,
                    DisplayName = "Inbox",
                    DefaultOrder = 4,
                    RequiresBackgroundSubscription = true,
                    SupportsCompactActiveState = false,
                    SupportsNotificationPeek = true,
                    DefaultAccentHex = "#F43F5E"
                },
                () => new InboxWidgetModule(
                    sp.GetRequiredService<INotificationListenerAdapter>(),
                    sp.GetRequiredService<IInboxRepository>(),
                    sp.GetRequiredService<IAppPreferencesService>())),
            new WidgetModuleRegistration(
                new WidgetDescriptor
                {
                    WidgetId = ClipboardWidgetModule.Id,
                    DisplayName = "Clipboard",
                    DefaultOrder = 5,
                    RequiresBackgroundSubscription = true,
                    SupportsCompactActiveState = false,
                    SupportsNotificationPeek = false,
                    DefaultAccentHex = "#38BDF8"
                },
                () => new ClipboardWidgetModule(
                    sp.GetRequiredService<IClipboardMonitorAdapter>(),
                    sp.GetRequiredService<IClipboardRepository>(),
                    sp.GetRequiredService<IAppPreferencesService>())),
            new WidgetModuleRegistration(
                new WidgetDescriptor
                {
                    WidgetId = VolumeMixerWidgetModule.Id,
                    DisplayName = "Volume Mixer",
                    DefaultOrder = 6,
                    RequiresBackgroundSubscription = true,
                    SupportsCompactActiveState = false,
                    SupportsNotificationPeek = false,
                    DefaultAccentHex = "#38BDF8"
                },
                () => new VolumeMixerWidgetModule(sp.GetRequiredService<IAudioMixerAdapter>())),
            new WidgetModuleRegistration(
                new WidgetDescriptor
                {
                    WidgetId = DeveloperWidgetModule.Id,
                    DisplayName = "Developer Activity",
                    DefaultOrder = 7,
                    RequiresBackgroundSubscription = true,
                    SupportsCompactActiveState = true,
                    SupportsNotificationPeek = true,
                    DefaultAccentHex = "#38BDF8"
                },
                () => new DeveloperWidgetModule(
                    sp.GetRequiredService<IEnumerable<ICodingAgentAdapter>>().ToArray(),
                    sp.GetRequiredService<IAgentSessionLogRepository>(),
                    sp.GetRequiredService<IAppPreferencesService>()))
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
