using System.IO;
using IoPath = System.IO.Path;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FlaUI.UIA3;
using FluentAssertions;
using NSubstitute;
using Shima.Core.Appearance;
using Shima.Core.Color;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Infrastructure.Appearance;
using Shima.Infrastructure.Color;
using Shima.Infrastructure.Configuration;
using Shima.Infrastructure.Storage;
using Shima.Presentation.ColorPicker;
using Shima.Presentation.Settings;
using Shima.Presentation.Shell;
using Shima.Presentation.Theming;
using Shima.WindowsInterop.Overlay;
using Shima.WindowsInterop.System;
using Xunit;

namespace Shima.UI.Tests;

[Collection("IslandShellUi")]
public sealed class ColorPickerUiTests
{
    [Fact]
    public async Task ColorPicker_AcquiresPin_PreservesFocus_UpdatesFieldAndHue_AndTogglesCollapsedExpandedLive()
    {
        using var host = new StaWpfTestHost();
        await using var harness = await CreateHarnessAsync(host);

        await host.RunAsync(() =>
        {
            harness.OwnerWindow.Show();
            harness.OwnerWindow.Activate();
            harness.IslandWindow.Show();
        });

        await host.PumpRenderAsync(harness.ThemeRenderer);

        var appearanceFile = IoPath.Combine(harness.RootDir, AtomicAppearanceConfigRepository.RelativePath);
        File.Exists(appearanceFile).Should().BeFalse();

        var request = new ColorPickerRequest(
            AppearanceTokenSlot.Background,
            IslandStateTarget.Both,
            "Surface background",
            "#000000",
            PreviewTargetState.Expanded);

        var dialog = await host.RunAsync(() =>
        {
            var dlg = new ColorPickerDialog(
                harness.AppearanceManager,
                harness.ColorService,
                request,
                harness.PresetManager)
            {
                Owner = harness.OwnerWindow
            };
            harness.ThemeRenderer.AttachFrameworkElement(dlg);
            dlg.Show();
            dlg.Activate();
            return dlg;
        });

        await host.PumpRenderAsync(harness.ThemeRenderer);

        // CP-03: Pin acquired cleanly, Island expanded, mouse leave cannot collapse, foreground NEVER moves onto IslandWindow
        harness.PinController.IsPinned.Should().BeTrue();
        harness.StateManager.CurrentState.Should().Be(IslandShellState.Expanded);

        var (islandHwnd, dialogHwnd) = await host.RunAsync(() => (
            harness.IslandWindow.WindowHandle,
            new WindowInteropHelper(dialog).Handle));

        await host.RunAsync(() => harness.StateManager.RequestUserCollapse());
        await host.PumpRenderAsync(harness.ThemeRenderer);
        harness.StateManager.CurrentState.Should().Be(IslandShellState.Expanded);
        GetForegroundWindow().Should().NotBe(islandHwnd);

        // Verify FlaUI UIA3 tree on ColorPickerDialog exposes all mandatory controls
        using (var automation = new UIA3Automation())
        {
            var dlgElement = automation.FromHandle(dialogHwnd);
            dlgElement.Should().NotBeNull();
            dlgElement.FindFirstDescendant(cf => cf.ByAutomationId("ColorSelectionField")).Should().NotBeNull();
            dlgElement.FindFirstDescendant(cf => cf.ByAutomationId("HueSpectrumSlider")).Should().NotBeNull();
            dlgElement.FindFirstDescendant(cf => cf.ByAutomationId("HexInputBox")).Should().NotBeNull();
            dlgElement.FindFirstDescendant(cf => cf.ByAutomationId("PreviewCollapsedRadio")).Should().NotBeNull();
            dlgElement.FindFirstDescendant(cf => cf.ByAutomationId("PreviewExpandedRadio")).Should().NotBeNull();
            dlgElement.FindFirstDescendant(cf => cf.ByAutomationId("ResetSlotButton")).Should().NotBeNull();
            dlgElement.FindFirstDescendant(cf => cf.ByAutomationId("CancelColorPickerButton")).Should().NotBeNull();
            dlgElement.FindFirstDescendant(cf => cf.ByAutomationId("ApplyColorPickerButton")).Should().NotBeNull();
        }

        // CP-02: Arrow & PageUp keyboard interactions on HueSpectrumSlider & ColorSelectionField update state while keeping Island unfocused
        var (hueSlider, selectionField, numericInputs, hexBox, collapsedRadio, expandedRadio, applyButton, cancelButton) = await host.RunAsync(() => (
            Find<HueSpectrumSlider>(dialog, "HueSlider"),
            Find<ColorSelectionField>(dialog, "SelectionField"),
            Find<ColorNumericInputs>(dialog, "NumericInputs"),
            Find<TextBox>(Find<ColorNumericInputs>(dialog, "NumericInputs"), "HexInputBox"),
            Find<RadioButton>(dialog, "PreviewCollapsedRadio"),
            Find<RadioButton>(dialog, "PreviewExpandedRadio"),
            Find<Button>(dialog, "ApplyButton"),
            Find<Button>(dialog, "CancelButton")));

        await host.RunAsync(() =>
        {
            hueSlider.Focus();
            RaiseKeyDown(hueSlider, Key.PageUp);
            RaiseKeyDown(hueSlider, Key.Up);

            selectionField.Focus();
            for (var i = 0; i < 6; i++)
            {
                RaiseKeyDown(selectionField, Key.Right);
                RaiseKeyDown(selectionField, Key.Up);
            }
        });

        await host.PumpRenderAsync(harness.ThemeRenderer);
        harness.AppearanceManager.ActiveRendered.ExpandedTokens.BackgroundHex.Should().NotBe("#000000");
        GetForegroundWindow().Should().NotBe(islandHwnd);

        // CP-04: Enter valid hex -> live renders #1E3A8A on IslandWindow surface with 0 disk writes, toggle Collapsed/Expanded resizes Island
        await host.RunAsync(() => hexBox.Text = "#1E3A8A");

        await host.PumpRenderAsync(harness.ThemeRenderer);
        File.Exists(appearanceFile).Should().BeFalse();

        var liveSurfaceColor = await host.RunAsync(() =>
        {
            var path = Find<System.Windows.Shapes.Path>(harness.IslandWindow, "NotchSurfacePath");
            return ((SolidColorBrush)path.Fill).Color;
        });

        liveSurfaceColor.Should().Be(System.Windows.Media.Color.FromRgb(0x1E, 0x3A, 0x8A));
        harness.ThemeRenderer.IsRenderingHookAttached.Should().BeFalse();

        // Toggle Collapsed vs Expanded PreviewMode on the real IslandWindow
        await host.RunAsync(() => collapsedRadio.IsChecked = true);
        await host.PumpRenderAsync(harness.ThemeRenderer);
        harness.PinController.ActiveTargetState.Should().Be(PreviewTargetState.Collapsed);
        harness.StateManager.CurrentState.Should().Be(IslandShellState.Collapsed);

        var (collapsedW, collapsedH) = await host.RunAsync(() =>
            (harness.IslandWindow.ActualWidth, harness.IslandWindow.ActualHeight));
        collapsedW.Should().BeApproximately(136.0, 1.0);
        collapsedH.Should().BeApproximately(28.0, 1.0);

        await host.RunAsync(() => expandedRadio.IsChecked = true);
        await host.PumpRenderAsync(harness.ThemeRenderer);
        harness.PinController.ActiveTargetState.Should().Be(PreviewTargetState.Expanded);
        harness.StateManager.CurrentState.Should().Be(IslandShellState.Expanded);

        var (expandedW, expandedH) = await host.RunAsync(() =>
            (harness.IslandWindow.ActualWidth, harness.IslandWindow.ActualHeight));
        expandedW.Should().BeApproximately(412.0, 1.0);
        expandedH.Should().BeApproximately(168.0, 1.0);

        File.Exists(appearanceFile).Should().BeFalse();

        await host.RunAsync(() => InvokeButton(cancelButton));
        await dialog.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await host.PumpRenderAsync(harness.ThemeRenderer);
        harness.PinController.IsPinned.Should().BeFalse();
    }

    [Fact]
    public async Task ColorPicker_LowContrastAdvisory_RestoresContrast_AndSavesNonBlockinglyOnApply()
    {
        using var host = new StaWpfTestHost();
        await using var harness = await CreateHarnessAsync(host);

        await host.RunAsync(() =>
        {
            harness.OwnerWindow.Show();
            harness.IslandWindow.Show();
        });

        var request = new ColorPickerRequest(
            AppearanceTokenSlot.PrimaryText,
            IslandStateTarget.Both,
            "Primary text",
            "#F8FAFC",
            PreviewTargetState.Expanded);

        var dialog = await host.RunAsync(() =>
        {
            var dlg = new ColorPickerDialog(
                harness.AppearanceManager,
                harness.ColorService,
                request,
                harness.PresetManager)
            {
                Owner = harness.OwnerWindow
            };
            harness.ThemeRenderer.AttachFrameworkElement(dlg);
            dlg.Show();
            return dlg;
        });

        await host.PumpRenderAsync(harness.ThemeRenderer);

        var (hexBox, warningBanner, restoreButton, applyButton) = await host.RunAsync(() =>
        {
            var inputs = Find<ColorNumericInputs>(dialog, "NumericInputs");
            return (
                Find<TextBox>(inputs, "HexInputBox"),
                Find<Border>(dialog, "ContrastWarningBanner"),
                Find<Button>(dialog, "RestoreContrastButton"),
                Find<Button>(dialog, "ApplyButton"));
        });

        // CP-05: Selecting #111111 text on #000000 shows ContrastWarningBanner while keeping Apply ALWAYS enabled
        await host.RunAsync(() => hexBox.Text = "#111111");
        await host.PumpRenderAsync(harness.ThemeRenderer);

        var (warningVisible, applyEnabled) = await host.RunAsync(() =>
            (warningBanner.Visibility == Visibility.Visible, applyButton.IsEnabled));
        warningVisible.Should().BeTrue();
        applyEnabled.Should().BeTrue();

        // CP-06: Restore recommended contrast fixes PrimaryText to >= 4.5:1 while keeping #000000 background intact
        await host.RunAsync(() => InvokeButton(restoreButton));
        await host.PumpRenderAsync(harness.ThemeRenderer);

        var (afterFixWarningVisible, repairedHex) = await host.RunAsync(() => (
            warningBanner.Visibility == Visibility.Visible,
            hexBox.Text));

        afterFixWarningVisible.Should().BeFalse();
        harness.ColorService.CalculateContrastRatio(repairedHex, "#000000").Should().BeGreaterOrEqualTo(4.5);
        harness.AppearanceManager.ActiveRendered.ExpandedTokens.BackgroundHex.Should().Be("#000000");

        // Re-select intentional low-contrast #111111 and click Apply -> non-blocking save MUST succeed!
        await host.RunAsync(() => hexBox.Text = "#111111");
        await host.PumpRenderAsync(harness.ThemeRenderer);
        await host.RunAsync(() => warningBanner.Visibility.Should().Be(Visibility.Visible));
        await host.RunAsync(() => applyButton.IsEnabled.Should().BeTrue());

        await host.RunAsync(() => InvokeButton(applyButton));
        await dialog.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await host.PumpAsync();
        await host.PumpRenderAsync(harness.ThemeRenderer);

        harness.PinController.IsPinned.Should().BeFalse();
        dialog.Result.Should().NotBeNull();
        dialog.Result!.Confirmed.Should().BeTrue();
        dialog.Result.SelectedColor.Hex.Should().Be("#111111");
        harness.AppearanceManager.CurrentCommitted.ExpandedTokens.PrimaryTextHex.Should().Be("#111111");
        harness.PresetManager.RecentColors.Should().Contain("#111111");
        File.Exists(IoPath.Combine(harness.RootDir, AtomicAppearanceConfigRepository.RelativePath)).Should().BeTrue();
    }

    [Fact]
    public async Task ColorPicker_SlotScopedReset_CancelRollback_AndRestartSurvival()
    {
        using var host = new StaWpfTestHost();
        await using var harness = await CreateHarnessAsync(host);

        // Pre-commit customized PrimaryText (#E0F2FE) + Accent (#38BDF8) and DPAPI OpenCode password
        using (var prep = harness.AppearanceManager.BeginEditingSession(acquireLivePreviewPin: false))
        {
            prep.UpdateToken(AppearanceTokenSlot.PrimaryText, IslandStateTarget.Both, "#E0F2FE");
            prep.UpdateToken(AppearanceTokenSlot.Accent, IslandStateTarget.Both, "#38BDF8");
            await prep.CommitAsync();
        }

        await harness.PreferencesService.SaveAsync(
            harness.PreferencesService.Current with { OpenCodeOptInEnabled = true },
            newOpenCodeServerPassword: "phase4-restart-secret");

        await host.RunAsync(() =>
        {
            harness.OwnerWindow.Show();
            harness.IslandWindow.Show();
        });
        await host.PumpRenderAsync(harness.ThemeRenderer);

        // CP-07: Slot-scoped Reset resets ONLY Accent in draft and Cancel rolls back cleanly
        var dialog1 = await host.RunAsync(() =>
        {
            var dlg = new ColorPickerDialog(
                harness.AppearanceManager,
                harness.ColorService,
                new ColorPickerRequest(AppearanceTokenSlot.Accent, IslandStateTarget.Both, "Accent", "#38BDF8"),
                harness.PresetManager)
            {
                Owner = harness.OwnerWindow
            };
            dlg.Show();
            return dlg;
        });

        var (d1Hex, d1Reset, d1Cancel) = await host.RunAsync(() => (
            Find<TextBox>(Find<ColorNumericInputs>(dialog1, "NumericInputs"), "HexInputBox"),
            Find<Button>(dialog1, "ResetSlotButton"),
            Find<Button>(dialog1, "CancelButton")));

        await host.RunAsync(() => d1Hex.Text = "#F43F5E");
        await host.PumpRenderAsync(harness.ThemeRenderer);
        harness.AppearanceManager.ActiveRendered.ExpandedTokens.AccentHex.Should().Be("#F43F5E");

        await host.RunAsync(() => InvokeButton(d1Reset));
        await host.PumpRenderAsync(harness.ThemeRenderer);
        harness.AppearanceManager.ActiveRendered.ExpandedTokens.AccentHex.Should().Be("#FFFFFF");
        harness.AppearanceManager.ActiveRendered.ExpandedTokens.PrimaryTextHex.Should().Be("#E0F2FE");

        await host.RunAsync(() => InvokeButton(d1Cancel));
        await dialog1.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await host.PumpRenderAsync(harness.ThemeRenderer);

        harness.PinController.IsPinned.Should().BeFalse();
        harness.AppearanceManager.ActiveRendered.ExpandedTokens.AccentHex.Should().Be("#38BDF8");
        harness.AppearanceManager.CurrentCommitted.ExpandedTokens.AccentHex.Should().Be("#38BDF8");

        // Escape key close also rolls back & releases pin
        var dialog2 = await host.RunAsync(() =>
        {
            var dlg = new ColorPickerDialog(
                harness.AppearanceManager,
                harness.ColorService,
                new ColorPickerRequest(AppearanceTokenSlot.Accent, IslandStateTarget.Both, "Accent", "#38BDF8"),
                harness.PresetManager)
            {
                Owner = harness.OwnerWindow
            };
            dlg.Show();
            return dlg;
        });

        await host.RunAsync(() =>
        {
            Find<TextBox>(Find<ColorNumericInputs>(dialog2, "NumericInputs"), "HexInputBox").Text = "#22C55E";
            RaiseKeyDown(dialog2, Key.Escape);
        });
        await dialog2.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await host.PumpRenderAsync(harness.ThemeRenderer);

        harness.PinController.IsPinned.Should().BeFalse();
        harness.AppearanceManager.ActiveRendered.ExpandedTokens.AccentHex.Should().Be("#38BDF8");

        // CP-08: Fresh manager instances loaded from disk survive simulated restart / upgrade
        var restarted = CreatePersistenceStack(harness.RootDir, harness.PinController);
        await restarted.AppearanceManager.InitializeAsync();
        await restarted.PreferencesService.InitializeAsync();

        restarted.AppearanceManager.CurrentCommitted.ExpandedTokens.AccentHex.Should().Be("#38BDF8");
        restarted.AppearanceManager.CurrentCommitted.ExpandedTokens.PrimaryTextHex.Should().Be("#E0F2FE");
        restarted.PresetManager.RecentColors.Should().Contain("#38BDF8");
        restarted.PreferencesService.Current.HasOpenCodeServerPassword.Should().BeTrue();
        restarted.PreferencesService.ReadOpenCodeServerPassword().Should().Be("phase4-restart-secret");
    }

    [Fact]
    public async Task SettingsWindow_ExposesAllTenSections_LiveMiniPreview_AndResetAllAppearance()
    {
        using var host = new StaWpfTestHost();
        await using var harness = await CreateHarnessAsync(host);

        var settingsWindow = await host.RunAsync(() =>
        {
            harness.IslandWindow.Show();
            var win = new SettingsWindow(
                harness.AppearanceManager,
                harness.PresetManager,
                harness.ColorService,
                harness.PreferencesService,
                harness.DiagnosticLogService,
                displayMonitor: null,
                startupAdapter: null,
                widgetCoordinator: null,
                hotkeyService: null,
                themeRenderer: harness.ThemeRenderer);
            win.Show();
            return win;
        });

        await host.PumpAsync();
        await host.PumpRenderAsync(harness.ThemeRenderer);

        var (minW, minH, actualW, actualH, settingsHwnd) = await host.RunAsync(() => (
            settingsWindow.MinWidth,
            settingsWindow.MinHeight,
            settingsWindow.ActualWidth,
            settingsWindow.ActualHeight,
            new WindowInteropHelper(settingsWindow).Handle));

        minW.Should().Be(860.0);
        minH.Should().Be(620.0);
        actualW.Should().BeGreaterOrEqualTo(860.0);
        actualH.Should().BeGreaterOrEqualTo(620.0);

        string[] expectedNavIds =
        [
            "NavGeneral", "NavWidgets", "NavNotifications", "NavClipboard",
            "NavFocusTimer", "NavClock", "NavDeveloperIntegrations", "NavAppearance",
            "NavUpdates", "NavLicense"
        ];

        using (var automation = new UIA3Automation())
        {
            var rootEl = automation.FromHandle(settingsHwnd);
            rootEl.Should().NotBeNull();
            foreach (var navId in expectedNavIds)
            {
                rootEl.FindFirstDescendant(cf => cf.ByAutomationId(navId)).Should().NotBeNull($"Settings must expose {navId}");
            }
        }

        // Switch to Appearance & Customization, apply Ocean preset, verify live Miniature Card updates to #071324, then Reset All to Midnight
        var (miniPreview, resetAllButton) = await host.RunAsync(() =>
        {
            settingsWindow.SelectSection("Appearance");
            return (
                Find<Border>(settingsWindow, "MiniIslandPreviewSurface"),
                Find<Button>(settingsWindow, "ResetAllAppearanceButton"));
        });
        await host.PumpAsync();
        await harness.AppearanceManager.ApplyPresetAsync("builtin.ocean");
        await host.PumpRenderAsync(harness.ThemeRenderer);

        var oceanMiniBg = await host.RunAsync(() => ((SolidColorBrush)miniPreview.Background).Color);
        oceanMiniBg.Should().Be(System.Windows.Media.Color.FromRgb(0x07, 0x13, 0x24));

        await host.RunAsync(() => InvokeButton(resetAllButton));
        await WaitUntilAsync(
            () => harness.AppearanceManager.CurrentCommitted.ActivePresetId == "builtin.midnight",
            () => host.PumpRenderAsync(harness.ThemeRenderer));

        var resetMiniBg = await host.RunAsync(() => ((SolidColorBrush)miniPreview.Background).Color);
        resetMiniBg.Should().Be(System.Windows.Media.Color.FromRgb(0x00, 0x00, 0x00));
        harness.AppearanceManager.CurrentCommitted.ActivePresetId.Should().Be("builtin.midnight");

        await host.RunAsync(() => settingsWindow.Close());
    }

    private static async Task<PickerUiHarness> CreateHarnessAsync(StaWpfTestHost host)
    {
        var rootDir = IoPath.Combine(IoPath.GetTempPath(), "shima-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);

        var pinController = new OverlayPinController();
        var stack = CreatePersistenceStack(rootDir, pinController);
        await stack.AppearanceManager.InitializeAsync();
        await stack.PreferencesService.InitializeAsync();

        return await host.RunAsync(() =>
        {
            using var realTopology = new DisplayTopologyMonitor();
            var primaryMonitor = realTopology.GetPrimaryMonitor();

            var displayMonitor = Substitute.For<IDisplayTopologyMonitor>();
            displayMonitor.GetMonitors().Returns([primaryMonitor]);
            displayMonitor.GetPrimaryMonitor().Returns(primaryMonitor);
            displayMonitor.ResolveTargetMonitor(Arg.Any<MonitorSelectionMode>(), Arg.Any<string?>()).Returns(primaryMonitor);

            var fullscreenDetector = Substitute.For<IFullscreenDetector>();
            fullscreenDetector.IsMonitorFullscreen(Arg.Any<nint>()).Returns(false);

            var hotkeyService = Substitute.For<IGlobalHotkeyService>();
            var powerMonitor = Substitute.For<IPowerAndMotionMonitor>();
            powerMonitor.CurrentPowerProfile.Returns(PowerProfile.AC);
            powerMonitor.IsReducedMotionEnabled.Returns(true);
            powerMonitor.IsSessionLocked.Returns(false);

            var stateManager = new IslandStateManager(pinController);
            var overlayAdapter = new OverlayWindowAdapter();

            var islandWindow = new IslandWindow(
                stateManager,
                pinController,
                overlayAdapter,
                displayMonitor,
                fullscreenDetector,
                hotkeyService,
                powerMonitor);

            var themeRenderer = new ThemeRenderer(stack.AppearanceManager, stack.ColorService, Dispatcher.CurrentDispatcher);
            themeRenderer.AttachFrameworkElement(islandWindow);
            themeRenderer.AttachFrameworkElement(islandWindow.WidgetHost);
            themeRenderer.ApplyNow();

            var ownerWindow = new Window
            {
                Title = "SettingsOwnerHost",
                Width = 320,
                Height = 200,
                Left = 40,
                Top = 260
            };

            return new PickerUiHarness(
                host,
                rootDir,
                pinController,
                stateManager,
                stack.ColorService,
                stack.PresetManager,
                stack.AppearanceManager,
                stack.PreferencesService,
                stack.DiagnosticLogService,
                themeRenderer,
                islandWindow,
                ownerWindow);
        });
    }

    private static (
        ColorProcessingService ColorService,
        PresetManager PresetManager,
        AppearanceManager AppearanceManager,
        AppPreferencesService PreferencesService,
        DiagnosticLogService DiagnosticLogService) CreatePersistenceStack(string rootDir, IOverlayPinController pinController)
    {
        var colorService = new ColorProcessingService();
        var appearanceRepo = new AtomicAppearanceConfigRepository(new AtomicConfigStore<AppearanceConfiguration>(rootDir));
        var presetRepo = new AtomicPresetCatalogRepository(new AtomicConfigStore<PresetCatalog>(rootDir));
        var presetManager = new PresetManager(presetRepo, colorService);
        var appearanceManager = new AppearanceManager(appearanceRepo, presetManager, colorService, pinController);
        var prefsStore = new AppPreferencesStore(new AtomicConfigStore<AppPreferences>(rootDir));
        var prefsService = new AppPreferencesService(prefsStore, new DpapiSecretStore());
        var diagService = new DiagnosticLogService(IoPath.Combine(rootDir, "logs"));
        return (colorService, presetManager, appearanceManager, prefsService, diagService);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<Task> pump)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await pump();
            await Task.Delay(15);
        }

        await pump();
    }

    private static T Find<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T
            ?? throw new InvalidOperationException($"Named element '{name}' of type {typeof(T).Name} was not found.");

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoker)
        {
            invoker.Invoke();
        }
    }

    private static void RaiseKeyDown(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target);
        if (source is null)
        {
            return;
        }

        var previewArgs = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(previewArgs);

        if (!previewArgs.Handled)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            target.RaiseEvent(args);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private sealed class PickerUiHarness(
        StaWpfTestHost host,
        string rootDir,
        OverlayPinController pinController,
        IslandStateManager stateManager,
        ColorProcessingService colorService,
        PresetManager presetManager,
        AppearanceManager appearanceManager,
        AppPreferencesService preferencesService,
        DiagnosticLogService diagnosticLogService,
        ThemeRenderer themeRenderer,
        IslandWindow islandWindow,
        Window ownerWindow) : IAsyncDisposable
    {
        public string RootDir { get; } = rootDir;
        public OverlayPinController PinController { get; } = pinController;
        public IslandStateManager StateManager { get; } = stateManager;
        public ColorProcessingService ColorService { get; } = colorService;
        public PresetManager PresetManager { get; } = presetManager;
        public AppearanceManager AppearanceManager { get; } = appearanceManager;
        public AppPreferencesService PreferencesService { get; } = preferencesService;
        public DiagnosticLogService DiagnosticLogService { get; } = diagnosticLogService;
        public ThemeRenderer ThemeRenderer { get; } = themeRenderer;
        public IslandWindow IslandWindow { get; } = islandWindow;
        public Window OwnerWindow { get; } = ownerWindow;

        public async ValueTask DisposeAsync()
        {
            await host.RunAsync(() =>
            {
                ThemeRenderer.Dispose();
                IslandWindow.Close();
                OwnerWindow.Close();
                StateManager.Dispose();
            }).ConfigureAwait(false);

            try
            {
                if (Directory.Exists(RootDir))
                {
                    Directory.Delete(RootDir, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class StaWpfTestHost : IDisposable
    {
        private readonly TaskCompletionSource<Dispatcher> _dispatcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        public StaWpfTestHost()
        {
            _thread = new Thread(() =>
            {
                if (Application.Current is null)
                {
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                }

                var dispatcher = Dispatcher.CurrentDispatcher;
                _dispatcherReady.SetResult(dispatcher);
                Dispatcher.Run();
            })
            {
                IsBackground = true
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public async Task<T> RunAsync<T>(Func<T> func)
        {
            var dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
            return await dispatcher.InvokeAsync(func).Task.ConfigureAwait(false);
        }

        public async Task<T> RunAsync<T>(Func<Task<T>> asyncFunc)
        {
            var dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
            var task = await dispatcher.InvokeAsync(asyncFunc).Task.ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }

        public async Task RunAsync(Action action)
        {
            var dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
            await dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
        }

        public async Task PumpAsync()
        {
            var dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }

        public async Task PumpRenderAsync(ThemeRenderer renderer)
        {
            var dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render).Task.ConfigureAwait(false);
            if (renderer.IsRenderingHookAttached)
            {
                await dispatcher.InvokeAsync(() => renderer.ApplyNow(), DispatcherPriority.Render).Task.ConfigureAwait(false);
            }

            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_dispatcherReady.Task.IsCompletedSuccessfully)
            {
                var dispatcher = _dispatcherReady.Task.Result;
                if (!dispatcher.HasShutdownStarted)
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }

            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
