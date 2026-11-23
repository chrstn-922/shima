using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shima.Core.Appearance;
using Shima.Core.Color;
using Shima.Core.Contracts;
using Shima.Presentation.ColorPicker;
using Shima.Presentation.Theming;

namespace Shima.Presentation.Settings;

/// <summary>
/// 10-section Desktop Settings shell featuring General (startup, behavior, hotkeys, diagnostic logs),
/// Widgets management/reorder, Notifications, Clipboard, Focus Timer, Clock, Developer Integrations (DPAPI password),
/// Appearance &amp; Customization (live miniature island preview + Color Picker launcher), Updates, and License.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly IAppearanceManager _appearanceManager;
    private readonly IPresetManager _presetManager;
    private readonly IColorProcessingService _colorService;
    private readonly IAppPreferencesService _preferencesService;
    private readonly IDiagnosticLogService _diagnosticLogService;
    private readonly IDisplayTopologyMonitor? _displayMonitor;
    private readonly IStartupRegistrationAdapter? _startupAdapter;
    private readonly IWidgetSettingsCoordinator? _widgetCoordinator;
    private readonly IGlobalHotkeyService? _hotkeyService;
    private readonly ThemeRenderer? _themeRenderer;
    private readonly IUpdateService? _updateService;
    private readonly ILicenseService? _licenseService;

    private bool _isLoadingUi;

    public SettingsWindow(
        IAppearanceManager appearanceManager,
        IPresetManager presetManager,
        IColorProcessingService colorService,
        IAppPreferencesService preferencesService,
        IDiagnosticLogService diagnosticLogService,
        IDisplayTopologyMonitor? displayMonitor = null,
        IStartupRegistrationAdapter? startupAdapter = null,
        IWidgetSettingsCoordinator? widgetCoordinator = null,
        IGlobalHotkeyService? hotkeyService = null,
        ThemeRenderer? themeRenderer = null,
        IUpdateService? updateService = null,
        ILicenseService? licenseService = null)
    {
        _appearanceManager = appearanceManager ?? throw new ArgumentNullException(nameof(appearanceManager));
        _presetManager = presetManager ?? throw new ArgumentNullException(nameof(presetManager));
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        _diagnosticLogService = diagnosticLogService ?? throw new ArgumentNullException(nameof(diagnosticLogService));
        _displayMonitor = displayMonitor;
        _startupAdapter = startupAdapter;
        _widgetCoordinator = widgetCoordinator;
        _hotkeyService = hotkeyService;
        _themeRenderer = themeRenderer;
        _updateService = updateService;
        _licenseService = licenseService;

        InitializeComponent();
        BrandIconLoader.AttachWindowIcons(this);

        _themeRenderer?.AttachFrameworkElement(this);
        _appearanceManager.AppearanceChanged += OnAppearanceChanged;
        _appearanceManager.RenderedAppearanceChanged += OnAppearanceChanged;
        _presetManager.CatalogChanged += OnPresetCatalogChanged;
        Closed += OnSettingsClosed;

        NavigationRail.SelectedIndex = 0;
        Loaded += async (_, _) => await RefreshAllAsync().ConfigureAwait(true);
    }

    public async Task RefreshAllAsync()
    {
        _isLoadingUi = true;
        try
        {
            RefreshAppearancePanel();
            RefreshPreferencesPanels();
            RefreshDiagnosticsPanel();
        }
        finally
        {
            _isLoadingUi = false;
        }

        await RefreshWidgetsPanelAsync().ConfigureAwait(true);
        RefreshClockSettingsHost();
        RefreshFocusTimerSettingsHost();
        RefreshNotificationSettingsHost();
        RefreshClipboardSettingsHost();
        RefreshDeveloperSettingsHost();
    }

    private void RefreshDeveloperSettingsHost()
    {
        DeveloperSettingsHost.Content = _widgetCoordinator?.CreateSettingsView("shima.developer");
    }

    private void RefreshFocusTimerSettingsHost()
    {
        var focusSettingsView = _widgetCoordinator?.CreateSettingsView("shima.focus");
        FocusTimerSettingsHost.Content = focusSettingsView;
        FocusSettingsPlaceholderCard.Visibility = focusSettingsView is null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshClockSettingsHost()
    {
        var clockSettingsView = _widgetCoordinator?.CreateSettingsView("shima.clock");
        ClockSettingsHost.Content = clockSettingsView;
        ClockSettingsPlaceholderCard.Visibility = clockSettingsView is null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshNotificationSettingsHost()
    {
        var inboxSettingsView = _widgetCoordinator?.CreateSettingsView("shima.inbox");
        InboxSettingsHost.Content = inboxSettingsView;
        NotificationExclusionsPlaceholderPanel.Visibility = inboxSettingsView is null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshClipboardSettingsHost()
    {
        var clipboardSettingsView = _widgetCoordinator?.CreateSettingsView("shima.clipboard");
        ClipboardSettingsHost.Content = clipboardSettingsView;
    }

    public void SelectSection(string sectionTag)
    {
        foreach (var item in NavigationRail.Items.OfType<ListBoxItem>())
        {
            if (string.Equals(item.Tag as string, sectionTag, StringComparison.OrdinalIgnoreCase))
            {
                NavigationRail.SelectedItem = item;
                break;
            }
        }
    }

    private void OnNavigationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavigationRail.SelectedItem is not ListBoxItem { Tag: string tag })
        {
            return;
        }

        var panels = new (string Tag, FrameworkElement Panel)[]
        {
            ("General", PanelGeneral),
            ("Widgets", PanelWidgets),
            ("Notifications", PanelNotifications),
            ("Clipboard", PanelClipboard),
            ("FocusTimer", PanelFocusTimer),
            ("Clock", PanelClock),
            ("DeveloperIntegrations", PanelDeveloperIntegrations),
            ("Appearance", PanelAppearance),
            ("Updates", PanelUpdates),
            ("License", PanelLicense)
        };

        foreach (var (panelTag, panel) in panels)
        {
            panel.Visibility = string.Equals(panelTag, tag, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (string.Equals(tag, "Widgets", StringComparison.Ordinal))
        {
            _ = RefreshWidgetsPanelAsync();
        }
        else if (string.Equals(tag, "Clock", StringComparison.Ordinal))
        {
            RefreshClockSettingsHost();
        }
        else if (string.Equals(tag, "FocusTimer", StringComparison.Ordinal))
        {
            RefreshFocusTimerSettingsHost();
        }
        else if (string.Equals(tag, "Notifications", StringComparison.Ordinal))
        {
            RefreshNotificationSettingsHost();
        }
        else if (string.Equals(tag, "Clipboard", StringComparison.Ordinal))
        {
            RefreshClipboardSettingsHost();
        }
        else if (string.Equals(tag, "DeveloperIntegrations", StringComparison.Ordinal))
        {
            RefreshDeveloperSettingsHost();
        }
        else if (string.Equals(tag, "General", StringComparison.Ordinal))
        {
            RefreshDiagnosticsPanel();
        }
    }

    private void RefreshAppearancePanel()
    {
        var previousSync = _isLoadingUi;
        _isLoadingUi = true;
        try
        {
            var rendered = _appearanceManager.ActiveRendered;
            var targetState = GetSelectedEditTarget(rendered);
            var activePalette = targetState == IslandStateTarget.Collapsed
                ? rendered.CollapsedTokens
                : rendered.ExpandedTokens;

            LinkCollapsedExpandedCheck.IsChecked = rendered.LinkCollapsedAndExpanded;
            UsePerWidgetAccentsCheck.IsChecked = rendered.UsePerWidgetAccents;
            TargetSurfaceSelectorPanel.Visibility = rendered.LinkCollapsedAndExpanded
                ? Visibility.Collapsed
                : Visibility.Visible;

            GlowOpacitySlider.Value = rendered.GlowOpacity;
            BorderThicknessBox.Text = rendered.BorderThicknessDip.ToString("0.##", CultureInfo.InvariantCulture);

            var report = _appearanceManager.CurrentContrastReport;
            if (report.HasWarnings)
            {
                AppearanceContrastBadgeText.Text = $"Contrast advisory: {report.Issues.Count} warning(s)";
                AppearanceContrastBadgeText.Foreground = TryFindResource("WarningBannerText") as Brush ?? Brushes.Gold;
                var first = report.Issues[0];
                AppearanceContrastDetailText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} is {1:0.0}:1 (recommended >= {2:0.0}:1). Non-blocking advisory.",
                    first.ElementLabel,
                    first.Ratio,
                    first.RequiredMinimum);
            }
            else
            {
                AppearanceContrastBadgeText.Text = "Contrast audit: Pass";
                AppearanceContrastBadgeText.Foreground = TryFindResource("AgentStatusWorkingBrush") as Brush ?? Brushes.LightGreen;
                AppearanceContrastDetailText.Text = "All primary text (>= 4.5:1) and secondary/accent (>= 3.0:1) tokens pass WCAG 2.1.";
            }

            var presetViewModels = _presetManager.AllPresets
                .Select(p => new PresetTileViewModel(
                    p.PresetId,
                    string.Equals(p.PresetId, rendered.ActivePresetId, StringComparison.OrdinalIgnoreCase)
                        ? $"{p.Name} (Active)"
                        : p.Name,
                    $"Apply theme preset {p.Name}",
                    $"PresetTile_{p.PresetId}",
                    CreateFrozenBrush(p.ExpandedTokens.BackgroundHex),
                    CreateFrozenBrush(p.ExpandedTokens.AccentHex)))
                .ToArray();

            PresetsItemsControl.ItemsSource = presetViewModels;

            var activePreset = _presetManager.FindById(rendered.ActivePresetId);
            if (activePreset is not null && !activePreset.IsBuiltIn)
            {
                CustomPresetActionsPanel.Visibility = Visibility.Visible;
                CustomPresetRenameBox.Text = activePreset.Name;
            }
            else
            {
                CustomPresetActionsPanel.Visibility = Visibility.Collapsed;
                CustomPresetRenameBox.Text = string.Empty;
            }

            var slots = new PaletteSlotViewModel[]
            {
                CreateSlotVm(AppearanceTokenSlot.Background, "Surface background", activePalette.BackgroundHex),
                CreateSlotVm(AppearanceTokenSlot.Accent, "Accent highlight", activePalette.AccentHex),
                CreateSlotVm(AppearanceTokenSlot.Border, "Hairline border", activePalette.BorderHex),
                CreateSlotVm(AppearanceTokenSlot.Glow, "Ambient glow", activePalette.GlowHex),
                CreateSlotVm(AppearanceTokenSlot.PrimaryText, "Primary text", activePalette.PrimaryTextHex),
                CreateSlotVm(AppearanceTokenSlot.SecondaryIcon, "Secondary icon & muted text", activePalette.SecondaryIconHex)
            };

            PaletteSlotsItemsControl.ItemsSource = slots;
        }
        finally
        {
            _isLoadingUi = previousSync;
        }
    }

    private void RefreshPreferencesPanels()
    {
        var previousSync = _isLoadingUi;
        _isLoadingUi = true;
        try
        {
            var prefs = _preferencesService.Current;

            StartWithWindowsCheck.IsChecked = prefs.StartWithWindows;
            StartupModeStatusText.Text = _startupAdapter?.UsesPackagedStartupTask == true
                ? "Startup registration mode: Windows packaged StartupTask"
                : "Startup registration mode: Windows per-user Run key";

            foreach (var item in MonitorModeCombo.Items.OfType<ComboBoxItem>())
            {
                if (Enum.TryParse<MonitorSelectionMode>(item.Tag as string, out var mode) && mode == prefs.MonitorSelectionMode)
                {
                    MonitorModeCombo.SelectedItem = item;
                    break;
                }
            }

            if (_displayMonitor is not null)
            {
                var monitors = _displayMonitor.GetMonitors();
                SpecificMonitorCombo.ItemsSource = monitors.Select(m => m.DeviceName).ToArray();
                SpecificMonitorCombo.SelectedItem = prefs.SpecificMonitorDeviceId ?? monitors.FirstOrDefault()?.DeviceName;
            }

            SpecificMonitorCombo.IsEnabled = prefs.MonitorSelectionMode == MonitorSelectionMode.SpecificMonitorId;
            ExpandOnHoverCheck.IsChecked = prefs.ExpandOnHover;
            FullscreenBreakthroughCheck.IsChecked = prefs.FullscreenBreakthroughAlerts;
            HoverDelayBox.Text = prefs.HoverExpandDelayMs.ToString(CultureInfo.InvariantCulture);
            AutoCollapseDelayBox.Text = prefs.AutoCollapseDelayMs.ToString(CultureInfo.InvariantCulture);
            HotkeyBox.Text = prefs.GlobalToggleHotkey ?? "Ctrl+Alt+Space";

            SyncActionCenterCheck.IsChecked = prefs.SyncInboxDismissalWithActionCenter;
            InboxRetentionBox.Text = prefs.InboxRetentionDays.ToString(CultureInfo.InvariantCulture);

            ClipboardMaxItemsBox.Text = prefs.ClipboardMaxItems.ToString(CultureInfo.InvariantCulture);
            ClipboardRetentionBox.Text = prefs.ClipboardRetentionDays.ToString(CultureInfo.InvariantCulture);

            ClaudeCodeOptInCheck.IsChecked = prefs.ClaudeCodeOptInEnabled;
            OpenCodeOptInCheck.IsChecked = prefs.OpenCodeOptInEnabled;
            OpenCodeEndpointBox.Text = prefs.OpenCodeEndpointUrl ?? "http://127.0.0.1:4096";
            OpenCodeUrlValidationText.Visibility = Visibility.Collapsed;
            OpenCodePasswordBox.Password = string.Empty;
            OpenCodePasswordStateText.Text = prefs.HasOpenCodeServerPassword
                ? "Password configured (DPAPI encrypted; enter a new value to replace)"
                : "No password saved";
            ClearOpenCodePasswordButton.IsEnabled = prefs.HasOpenCodeServerPassword;

            UpdatesOfflineModeCheck.IsChecked = prefs.UpdatesOfflineMode;
            InstalledVersionText.Text = _updateService is not null
                ? $"Installed version: {_updateService.CurrentVersion} ({_updateService.CurrentArchitecture})"
                : "Installed version: 1.0.1 (local-first desktop shell)";
            UpdateStatusText.Text = prefs.UpdatesOfflineMode
                ? "Offline mode is enabled — update checks are disabled."
                : _updateService?.StagedUpdate is { } staged
                    ? $"Update {staged.Version} ({staged.Architecture}) staged cleanly."
                    : "Update checks are manual and respect offline mode.";

            LicenseKeyBox.Password = string.Empty;
            if (_licenseService is not null)
            {
                var status = _licenseService.GetCachedStatus();
                LicenseStateText.Text = status.Message;
                ClearLicenseKeyButton.IsEnabled = status.State != LicenseStateKind.CommunityOffline || prefs.HasLicenseKey;
            }
            else
            {
                LicenseStateText.Text = prefs.HasLicenseKey
                    ? "License key configured (DPAPI encrypted)"
                    : "Community offline edition — no license key stored";
                ClearLicenseKeyButton.IsEnabled = prefs.HasLicenseKey;
            }
        }
        finally
        {
            _isLoadingUi = previousSync;
        }
    }

    private async Task RefreshWidgetsPanelAsync()
    {
        if (_widgetCoordinator is null)
        {
            WidgetsItemsControl.ItemsSource = Array.Empty<WidgetRowViewModel>();
            NoWidgetsText.Visibility = Visibility.Visible;
            return;
        }

        var items = (await _widgetCoordinator.GetWidgetsAsync().ConfigureAwait(true))
            .OrderBy(i => i.SortOrder)
            .ToArray();

        var vms = items
            .Select((item, idx) => new WidgetRowViewModel(
                item.WidgetId,
                item.DisplayName,
                item.IsFaulted
                    ? "Faulted — reload or reset settings"
                    : item.IsEnabled ? "Enabled on Dynamic Island pager" : "Disabled",
                item.IsEnabled ? "Disable" : "Enable",
                idx > 0,
                idx < items.Length - 1,
                $"Move {item.DisplayName} up",
                $"Move {item.DisplayName} down"))
            .ToArray();

        WidgetsItemsControl.ItemsSource = vms;
        NoWidgetsText.Visibility = vms.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshDiagnosticsPanel()
    {
        LogDirectoryPathText.Text = $"Log directory: {_diagnosticLogService.LogDirectoryPath}";
        var lines = _diagnosticLogService.GetRecentSanitizedLines(200);
        DiagnosticsLogViewer.Text = lines.Count == 0
            ? "No log entries recorded in this session yet."
            : string.Join(Environment.NewLine, lines);
    }

    private async void OnGeneralCheckChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isLoadingUi)
        {
            return;
        }

        var startWithWindows = StartWithWindowsCheck.IsChecked == true;
        if (_startupAdapter is not null)
        {
            await _startupAdapter.SetEnabledAsync(startWithWindows).ConfigureAwait(true);
        }

        await SavePreferencesFromControlsAsync().ConfigureAwait(true);
    }

    private async void OnPresetTileClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string presetId })
        {
            return;
        }

        await _appearanceManager.ApplyPresetAsync(presetId).ConfigureAwait(true);
        StatusFooterText.Text = $"Applied preset '{_presetManager.FindById(presetId)?.Name ?? presetId}'.";
    }

    private async void OnDuplicatePresetClicked(object sender, RoutedEventArgs e)
    {
        var activeId = _appearanceManager.CurrentCommitted.ActivePresetId;
        var duplicated = await _presetManager.DuplicatePresetAsync(activeId).ConfigureAwait(true);
        await _appearanceManager.ApplyPresetAsync(duplicated.PresetId).ConfigureAwait(true);
        StatusFooterText.Text = $"Created custom preset '{duplicated.Name}'.";
    }

    private async void OnSaveNewPresetClicked(object sender, RoutedEventArgs e)
    {
        var current = _appearanceManager.CurrentCommitted;
        var created = await _presetManager.CreateCustomPresetAsync(
            "Custom Theme",
            current.CollapsedTokens,
            current.ExpandedTokens).ConfigureAwait(true);
        await _appearanceManager.ApplyPresetAsync(created.PresetId).ConfigureAwait(true);
        StatusFooterText.Text = $"Saved preset '{created.Name}'.";
    }

    private async void OnRenameCustomPresetClicked(object sender, RoutedEventArgs e)
    {
        var activeId = _appearanceManager.CurrentCommitted.ActivePresetId;
        var nextName = CustomPresetRenameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(nextName))
        {
            return;
        }

        try
        {
            var renamed = await _presetManager.RenameCustomPresetAsync(activeId, nextName).ConfigureAwait(true);
            RefreshAppearancePanel();
            StatusFooterText.Text = $"Renamed preset to '{renamed.Name}'.";
        }
        catch (InvalidOperationException ex)
        {
            StatusFooterText.Text = ex.Message;
        }
    }

    private async void OnDeleteCustomPresetClicked(object sender, RoutedEventArgs e)
    {
        var activeId = _appearanceManager.CurrentCommitted.ActivePresetId;
        try
        {
            await _presetManager.DeleteCustomPresetAsync(activeId).ConfigureAwait(true);
            await _appearanceManager.ApplyPresetAsync(AppearanceConfiguration.DefaultPresetId).ConfigureAwait(true);
            StatusFooterText.Text = "Deleted custom preset and restored Midnight.";
        }
        catch (InvalidOperationException ex)
        {
            StatusFooterText.Text = ex.Message;
        }
    }

    private async void OnLinkBackgroundsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isLoadingUi)
        {
            return;
        }

        await _appearanceManager.SetLinkCollapsedAndExpandedAsync(LinkCollapsedExpandedCheck.IsChecked == true).ConfigureAwait(true);
        StatusFooterText.Text = LinkCollapsedExpandedCheck.IsChecked == true
            ? "Collapsed and expanded surfaces linked."
            : "Collapsed and expanded surfaces unlinked.";
    }

    private async void OnPerWidgetAccentsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isLoadingUi)
        {
            return;
        }

        await _appearanceManager.SetUsePerWidgetAccentsAsync(UsePerWidgetAccentsCheck.IsChecked == true).ConfigureAwait(true);
    }

    private void OnTargetSurfaceChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_isLoadingUi)
        {
            RefreshAppearancePanel();
        }
    }

    private async void OnGlowOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _isLoadingUi)
        {
            return;
        }

        await _appearanceManager.SetGlowOpacityAsync(e.NewValue).ConfigureAwait(true);
    }

    private async void OnBorderThicknessLostFocus(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isLoadingUi)
        {
            return;
        }

        if (double.TryParse(BorderThicknessBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var thickness))
        {
            await _appearanceManager.SetBorderThicknessAsync(thickness).ConfigureAwait(true);
        }
    }

    private void OnEditPaletteSlotClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppearanceTokenSlot slot })
        {
            return;
        }

        var current = _appearanceManager.CurrentCommitted;
        var targetState = GetSelectedEditTarget(current);
        var palette = targetState == IslandStateTarget.Collapsed ? current.CollapsedTokens : current.ExpandedTokens;
        var initialHex = palette.GetSlotHex(slot);
        var initialPreview = targetState == IslandStateTarget.Collapsed
            ? PreviewTargetState.Collapsed
            : PreviewTargetState.Expanded;

        var request = new ColorPickerRequest(
            slot,
            targetState,
            GetDisplayNameForSlot(slot),
            initialHex,
            initialPreview);

        var dialog = new ColorPickerDialog(_appearanceManager, _colorService, request, _presetManager)
        {
            Owner = this
        };

        dialog.ShowDialog();
        RefreshAppearancePanel();
        if (dialog.Result?.Confirmed == true)
        {
            StatusFooterText.Text = $"Updated {request.DisplayName} to {dialog.Result.SelectedColor.Hex}.";
        }
        else
        {
            StatusFooterText.Text = $"Canceled edits to {request.DisplayName}.";
        }
    }

    private async void OnResetAllAppearanceClicked(object sender, RoutedEventArgs e)
    {
        await _appearanceManager.ResetAllToDefaultsAsync().ConfigureAwait(true);
        StatusFooterText.Text = "Restored all appearance tokens to Midnight defaults.";
    }

    private async void OnBehaviorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_isLoadingUi)
        {
            await SavePreferencesFromControlsAsync().ConfigureAwait(true);
        }
    }

    private async void OnBehaviorCheckChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_isLoadingUi)
        {
            await SavePreferencesFromControlsAsync().ConfigureAwait(true);
            if (ReferenceEquals(sender, UpdatesOfflineModeCheck))
            {
                UpdateStatusText.Text = UpdatesOfflineModeCheck.IsChecked == true
                    ? "Offline mode is enabled — update checks are disabled."
                    : "Update checks are manual and respect offline mode.";
            }
        }
    }

    private async void OnBehaviorTextLostFocus(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_isLoadingUi)
        {
            await SavePreferencesFromControlsAsync().ConfigureAwait(true);
        }
    }

    private async void OnSaveHotkeyClicked(object sender, RoutedEventArgs e)
    {
        await SavePreferencesFromControlsAsync().ConfigureAwait(true);
        StatusFooterText.Text = _hotkeyService is null
            ? "Saved global hotkey preference."
            : "Saved hotkey preference.";
    }

    private async void OnMoveWidgetUpClicked(object sender, RoutedEventArgs e) =>
        await MoveWidgetAsync(sender, delta: -1).ConfigureAwait(true);

    private async void OnMoveWidgetDownClicked(object sender, RoutedEventArgs e) =>
        await MoveWidgetAsync(sender, delta: +1).ConfigureAwait(true);

    private async Task MoveWidgetAsync(object sender, int delta)
    {
        if (_widgetCoordinator is null || sender is not Button { Tag: string widgetId })
        {
            return;
        }

        var ordered = (await _widgetCoordinator.GetWidgetsAsync().ConfigureAwait(true))
            .OrderBy(w => w.SortOrder)
            .Select(w => w.WidgetId)
            .ToList();

        var index = ordered.FindIndex(id => string.Equals(id, widgetId, StringComparison.Ordinal));
        var nextIndex = index + delta;
        if (index < 0 || nextIndex < 0 || nextIndex >= ordered.Count)
        {
            return;
        }

        (ordered[index], ordered[nextIndex]) = (ordered[nextIndex], ordered[index]);
        await _widgetCoordinator.UpdateOrderAsync(ordered).ConfigureAwait(true);
        await RefreshWidgetsPanelAsync().ConfigureAwait(true);
        StatusFooterText.Text = $"Reordered widget '{widgetId}'.";
    }

    private async void OnToggleWidgetClicked(object sender, RoutedEventArgs e)
    {
        if (_widgetCoordinator is null || sender is not Button { Tag: string widgetId })
        {
            return;
        }

        var current = (await _widgetCoordinator.GetWidgetsAsync().ConfigureAwait(true))
            .FirstOrDefault(w => string.Equals(w.WidgetId, widgetId, StringComparison.Ordinal));
        if (current is not null)
        {
            await _widgetCoordinator.SetEnabledAsync(widgetId, !current.IsEnabled).ConfigureAwait(true);
            await RefreshWidgetsPanelAsync().ConfigureAwait(true);
        }
    }

    private async void OnResetWidgetClicked(object sender, RoutedEventArgs e)
    {
        if (_widgetCoordinator is null || sender is not Button { Tag: string widgetId })
        {
            return;
        }

        await _widgetCoordinator.ResetSettingsAsync(widgetId).ConfigureAwait(true);
        await RefreshWidgetsPanelAsync().ConfigureAwait(true);
        StatusFooterText.Text = $"Reset widget settings for '{widgetId}'.";
    }

    private async void OnResetPreferencesClicked(object sender, RoutedEventArgs e)
    {
        await _preferencesService.ResetToDefaultsAsync().ConfigureAwait(true);
        RefreshPreferencesPanels();
        StatusFooterText.Text = "Reset preferences to defaults.";
    }

    private async void OnDeveloperChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && !_isLoadingUi)
        {
            await SavePreferencesFromControlsAsync().ConfigureAwait(true);
        }
    }

    private async void OnSaveDeveloperClicked(object sender, RoutedEventArgs e)
    {
        var rawUrl = OpenCodeEndpointBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(rawUrl) && !IsValidLoopbackEndpoint(rawUrl))
        {
            OpenCodeUrlValidationText.Text = "Endpoint URL must use http://localhost:* or http://127.0.0.1:* for local-only security.";
            OpenCodeUrlValidationText.Visibility = Visibility.Visible;
            return;
        }

        OpenCodeUrlValidationText.Visibility = Visibility.Collapsed;
        var newPassword = OpenCodePasswordBox.Password;
        await SavePreferencesFromControlsAsync(
            newOpenCodePassword: string.IsNullOrEmpty(newPassword) ? null : newPassword).ConfigureAwait(true);
        OpenCodePasswordBox.Password = string.Empty;
        RefreshPreferencesPanels();
        StatusFooterText.Text = string.IsNullOrEmpty(newPassword)
            ? "Saved developer integration settings."
            : "Saved developer settings with DPAPI-encrypted OpenCode password.";
    }

    private async void OnClearOpenCodePasswordClicked(object sender, RoutedEventArgs e)
    {
        await SavePreferencesFromControlsAsync(clearOpenCodePassword: true).ConfigureAwait(true);
        OpenCodePasswordBox.Password = string.Empty;
        RefreshPreferencesPanels();
        StatusFooterText.Text = "Cleared saved OpenCode server password.";
    }

    private async void OnCheckForUpdatesClicked(object sender, RoutedEventArgs e)
    {
        if (_updateService is null)
        {
            UpdateStatusText.Text = UpdatesOfflineModeCheck.IsChecked == true
                ? "Offline mode is enabled — update checks are disabled."
                : "No remote update feed configured.";
            return;
        }

        CheckForUpdatesButton.IsEnabled = false;
        try
        {
            var result = await _updateService.CheckForUpdatesAsync().ConfigureAwait(true);
            var statusMessage = string.IsNullOrWhiteSpace(result.Message)
                ? $"Update status: {result.Outcome}"
                : result.Message;
            UpdateStatusText.Text = statusMessage;
            StatusFooterText.Text = statusMessage;
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private async void OnSaveLicenseClicked(object sender, RoutedEventArgs e)
    {
        var key = LicenseKeyBox.Password?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (_licenseService is not null)
        {
            var status = await _licenseService.ActivateLicenseAsync(key).ConfigureAwait(true);
            if (status.IsValid)
            {
                LicenseKeyBox.Password = string.Empty;
            }

            RefreshPreferencesPanels();
            LicenseStateText.Text = status.Message;
            StatusFooterText.Text = status.Message;
            return;
        }

        await SavePreferencesFromControlsAsync(newLicenseKey: key).ConfigureAwait(true);
        LicenseKeyBox.Password = string.Empty;
        RefreshPreferencesPanels();
        StatusFooterText.Text = "Saved DPAPI-protected license key.";
    }

    private async void OnClearLicenseClicked(object sender, RoutedEventArgs e)
    {
        if (_licenseService is not null)
        {
            await _licenseService.DeactivateLocalLicenseAsync().ConfigureAwait(true);
            LicenseKeyBox.Password = string.Empty;
            RefreshPreferencesPanels();
            StatusFooterText.Text = _licenseService.GetCachedStatus().Message;
            return;
        }

        await SavePreferencesFromControlsAsync(clearLicenseKey: true).ConfigureAwait(true);
        LicenseKeyBox.Password = string.Empty;
        RefreshPreferencesPanels();
        StatusFooterText.Text = "Cleared license key.";
    }

    private void OnRefreshLogsClicked(object sender, RoutedEventArgs e)
    {
        RefreshDiagnosticsPanel();
        StatusFooterText.Text = "Refreshed sanitized diagnostic logs.";
    }

    private void OnClearLogsClicked(object sender, RoutedEventArgs e)
    {
        _diagnosticLogService.ClearLogs();
        RefreshDiagnosticsPanel();
        StatusFooterText.Text = "Cleared diagnostic log files.";
    }

    private void OnExportDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        var exportDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shima",
            "diagnostics");
        var bundlePath = _diagnosticLogService.ExportRedactedBundle(exportDir);
        StatusFooterText.Text = $"Exported sanitized bundle: {Path.GetFileName(bundlePath)}";
    }

    private async Task SavePreferencesFromControlsAsync(
        string? newOpenCodePassword = null,
        bool clearOpenCodePassword = false,
        string? newLicenseKey = null,
        bool clearLicenseKey = false)
    {
        var current = _preferencesService.Current;
        var mode = MonitorSelectionMode.Primary;
        if (MonitorModeCombo.SelectedItem is ComboBoxItem { Tag: string modeTag }
            && Enum.TryParse<MonitorSelectionMode>(modeTag, out var parsedMode))
        {
            mode = parsedMode;
        }

        var endpointText = OpenCodeEndpointBox.Text?.Trim();
        var safeEndpoint = string.IsNullOrWhiteSpace(endpointText)
            ? null
            : IsValidLoopbackEndpoint(endpointText) ? endpointText : current.OpenCodeEndpointUrl;

        var next = current with
        {
            StartWithWindows = StartWithWindowsCheck.IsChecked == true,
            MonitorSelectionMode = mode,
            SpecificMonitorDeviceId = SpecificMonitorCombo.SelectedItem as string ?? current.SpecificMonitorDeviceId,
            ExpandOnHover = ExpandOnHoverCheck.IsChecked == true,
            FullscreenBreakthroughAlerts = FullscreenBreakthroughCheck.IsChecked == true,
            HoverExpandDelayMs = ParseIntOrDefault(HoverDelayBox.Text, current.HoverExpandDelayMs),
            AutoCollapseDelayMs = ParseIntOrDefault(AutoCollapseDelayBox.Text, current.AutoCollapseDelayMs),
            SyncInboxDismissalWithActionCenter = SyncActionCenterCheck.IsChecked == true,
            InboxRetentionDays = ParseIntOrDefault(InboxRetentionBox.Text, current.InboxRetentionDays),
            GlobalToggleHotkey = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? current.GlobalToggleHotkey : HotkeyBox.Text.Trim(),
            ClipboardMaxItems = ParseIntOrDefault(ClipboardMaxItemsBox.Text, current.ClipboardMaxItems),
            ClipboardRetentionDays = ParseIntOrDefault(ClipboardRetentionBox.Text, current.ClipboardRetentionDays),
            ClaudeCodeOptInEnabled = ClaudeCodeOptInCheck.IsChecked == true,
            OpenCodeOptInEnabled = OpenCodeOptInCheck.IsChecked == true,
            OpenCodeEndpointUrl = safeEndpoint,
            UpdatesOfflineMode = UpdatesOfflineModeCheck.IsChecked == true
        };

        await _preferencesService.SaveAsync(
            next,
            newOpenCodeServerPassword: newOpenCodePassword,
            clearOpenCodeServerPassword: clearOpenCodePassword,
            newLicenseKey: newLicenseKey,
            clearLicenseKey: clearLicenseKey).ConfigureAwait(true);

        SpecificMonitorCombo.IsEnabled = mode == MonitorSelectionMode.SpecificMonitorId;
    }

    private IslandStateTarget GetSelectedEditTarget(AppearanceConfiguration config)
    {
        if (config.LinkCollapsedAndExpanded)
        {
            return IslandStateTarget.Both;
        }

        return EditCollapsedSurfaceRadio.IsChecked == true
            ? IslandStateTarget.Collapsed
            : IslandStateTarget.Expanded;
    }

    private static bool IsValidLoopbackEndpoint(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && uri.IsLoopback;

    private void OnAppearanceChanged(object? sender, AppearanceConfiguration e)
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshAppearancePanel();
        }
        else
        {
            Dispatcher.BeginInvoke(RefreshAppearancePanel);
        }
    }

    private void OnPresetCatalogChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshAppearancePanel();
        }
        else
        {
            Dispatcher.BeginInvoke(RefreshAppearancePanel);
        }
    }

    private void OnSettingsClosed(object? sender, EventArgs e)
    {
        FocusTimerSettingsHost.Content = null;
        ClockSettingsHost.Content = null;
        InboxSettingsHost.Content = null;
        ClipboardSettingsHost.Content = null;
        DeveloperSettingsHost.Content = null;
        _appearanceManager.AppearanceChanged -= OnAppearanceChanged;
        _appearanceManager.RenderedAppearanceChanged -= OnAppearanceChanged;
        _presetManager.CatalogChanged -= OnPresetCatalogChanged;
    }

    private static int ParseIntOrDefault(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static PaletteSlotViewModel CreateSlotVm(AppearanceTokenSlot slot, string displayName, string hex) =>
        new(
            slot,
            displayName,
            hex,
            CreateFrozenBrush(hex),
            $"Edit {displayName} color",
            $"EditColorButton_{slot}");

    private static string GetDisplayNameForSlot(AppearanceTokenSlot slot) => slot switch
    {
        AppearanceTokenSlot.Background => "Surface background",
        AppearanceTokenSlot.Accent => "Accent highlight",
        AppearanceTokenSlot.Border => "Hairline border",
        AppearanceTokenSlot.Glow => "Ambient glow",
        AppearanceTokenSlot.PrimaryText => "Primary text",
        AppearanceTokenSlot.SecondaryIcon => "Secondary icon & muted text",
        _ => slot.ToString()
    };

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is System.Windows.Media.Color parsed)
            {
                var brush = new SolidColorBrush(parsed);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
        }

        return Brushes.Black;
    }

    public sealed record PresetTileViewModel(
        string Id,
        string DisplayLabel,
        string ActionLabel,
        string AutomationId,
        Brush BackgroundSwatch,
        Brush AccentSwatch);

    public sealed record PaletteSlotViewModel(
        AppearanceTokenSlot Slot,
        string DisplayName,
        string HexValue,
        Brush SwatchBrush,
        string EditActionName,
        string EditButtonAutomationId);

    public sealed record WidgetRowViewModel(
        string WidgetId,
        string DisplayName,
        string StatusDescription,
        string ToggleActionLabel,
        bool CanMoveUp,
        bool CanMoveDown,
        string MoveUpLabel,
        string MoveDownLabel);
}
