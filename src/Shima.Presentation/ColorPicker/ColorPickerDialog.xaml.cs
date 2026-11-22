using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Shima.Core.Appearance;
using Shima.Core.Color;
using Shima.Core.Contracts;
using Shima.Presentation.Theming;

namespace Shima.Presentation.ColorPicker;

/// <summary>
/// Studio Advanced Color Picker modal supporting FR-PICKER-006 live preview pinning,
/// Collapsed/Expanded state switching, non-blocking WCAG contrast advisories with 1-click solver,
/// slot-scoped default reset, original-swatch revert, and transactional Cancel rollback.
/// </summary>
public partial class ColorPickerDialog : Window
{
    private readonly IAppearanceManager _appearanceManager;
    private readonly IColorProcessingService _colorService;
    private readonly ColorPickerRequest _request;
    private readonly IAppearanceEditingSession _session;
    private readonly TaskCompletionSource<ColorPickerResult> _completionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ShimaColor _currentColor;
    private ColorChangeSource _lastSource = ColorChangeSource.Reset;
    private bool _wasReset;
    private bool _isCommitted;
    private bool _isClosingCleanly;
    private bool _syncingStateToggles;

    public ColorPickerDialog(
        IAppearanceManager appearanceManager,
        IColorProcessingService colorService,
        ColorPickerRequest request,
        IPresetManager? presetManager = null,
        IAppearanceEditingSession? existingSession = null)
    {
        _appearanceManager = appearanceManager ?? throw new ArgumentNullException(nameof(appearanceManager));
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));
        _request = request ?? throw new ArgumentNullException(nameof(request));

        _session = existingSession ?? _appearanceManager.BeginEditingSession(
            acquireLivePreviewPin: true,
            initialPreviewState: _request.InitialPreviewState);

        InitializeComponent();
        BrandIconLoader.AttachWindowIcons(this);
        NumericInputs.ConfigureColorService(_colorService);

        _currentColor = _colorService.TryParseHex(_request.InitialHex, out var initial) && initial is not null
            ? initial
            : _colorService.FromHex(IslandColorPalette.MidnightAccent);

        TokenHeaderTitle.Text = $"Edit {_request.DisplayName}";
        PinStateIndicator.Text = _session.IsLivePreviewPinned
            ? "Live preview pinned on desktop"
            : "Live preview active";

        PreviewPanel.SetRecentColors(presetManager?.RecentColors ?? Array.Empty<string>());
        UpdatePreviewStateRadios(_session.CurrentPreviewState);
        WireChildEvents();
        ApplyColorToUiAndSession(_currentColor, ColorChangeSource.Reset, propagateToSession: false);
        EvaluateContrastAdvisory();
    }

    public ColorPickerResult? Result { get; private set; }

    public Task<ColorPickerResult> CompletionTask => _completionTcs.Task;

    private void WireChildEvents()
    {
        SelectionField.SelectionChanged += (_, e) =>
        {
            var next = _colorService.FromHsb(HueSlider.Hue, e.Saturation, e.Brightness);
            ApplyColorToUiAndSession(next, e.Source, propagateToSession: true);
        };

        HueSlider.HueChanged += (_, e) =>
        {
            var next = _colorService.FromHsb(e.Hue, SelectionField.Saturation, SelectionField.Brightness);
            ApplyColorToUiAndSession(next, e.Source, propagateToSession: true);
        };

        NumericInputs.ColorEdited += (_, e) =>
        {
            ApplyColorToUiAndSession(e.Color, e.Source, propagateToSession: true);
        };

        PreviewPanel.RecentColorSelected += (_, hex) =>
        {
            var parsed = _colorService.FromHex(hex, _currentColor.Hue);
            ApplyColorToUiAndSession(parsed, ColorChangeSource.RecentColorSwatch, propagateToSession: true);
        };

        PreviewPanel.OriginalColorClicked += (_, hex) =>
        {
            var parsed = _colorService.FromHex(hex, _currentColor.Hue);
            ApplyColorToUiAndSession(parsed, ColorChangeSource.OriginalSwatchRevert, propagateToSession: true);
        };
    }

    private void ApplyColorToUiAndSession(ShimaColor color, ColorChangeSource source, bool propagateToSession)
    {
        _currentColor = color;
        _lastSource = source;

        SelectionField.Hue = color.Hue;
        SelectionField.Saturation = color.Saturation;
        SelectionField.Brightness = color.Brightness;
        HueSlider.Hue = color.Hue;

        NumericInputs.UpdateFromColor(color);
        PreviewPanel.SetComparisonColors(_request.InitialHex, color.Hex);

        if (propagateToSession && !_session.IsCompleted)
        {
            _session.UpdateToken(_request.TargetSlot, _request.TargetState, color.Hex);
            EvaluateContrastAdvisory();
        }
    }

    private void EvaluateContrastAdvisory()
    {
        if (_session.IsCompleted)
        {
            return;
        }

        var report = _session.CurrentContrastReport;
        if (!report.HasWarnings)
        {
            ContrastWarningBanner.Visibility = Visibility.Collapsed;
            return;
        }

        var primaryIssue = report.Issues.FirstOrDefault(i => i.Slot == _request.TargetSlot)
            ?? report.Issues[0];

        ContrastWarningText.Text = string.Format(
            CultureInfo.InvariantCulture,
            "Low contrast advisory for {0} ({1:0.0}:1 vs {2:0.0}:1 recommended). You may still apply this color or restore readable contrast.",
            primaryIssue.ElementLabel,
            primaryIssue.Ratio,
            primaryIssue.RequiredMinimum);

        ContrastWarningBanner.Visibility = Visibility.Visible;
        // FR-PICKER-006 / WCAG advisory rule: Apply stays ALWAYS enabled even on low contrast.
        ApplyButton.IsEnabled = true;
    }

    private void UpdatePreviewStateRadios(PreviewTargetState state)
    {
        _syncingStateToggles = true;
        try
        {
            PreviewCollapsedRadio.IsChecked = state == PreviewTargetState.Collapsed;
            PreviewExpandedRadio.IsChecked = state == PreviewTargetState.Expanded;
        }
        finally
        {
            _syncingStateToggles = false;
        }
    }

    private void OnPreviewStateChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingStateToggles || _session.IsCompleted)
        {
            return;
        }

        var target = ReferenceEquals(sender, PreviewCollapsedRadio)
            ? PreviewTargetState.Collapsed
            : PreviewTargetState.Expanded;

        _session.SetPreviewState(target);
    }

    private void OnRestoreContrastClicked(object sender, RoutedEventArgs e)
    {
        if (_session.IsCompleted)
        {
            return;
        }

        _session.ApplyRecommendedContrastFix();

        var updatedPalette = _request.TargetState == IslandStateTarget.Collapsed
            ? _session.DraftConfiguration.CollapsedTokens
            : _session.DraftConfiguration.ExpandedTokens;

        var repairedHex = updatedPalette.GetSlotHex(_request.TargetSlot);
        var repairedColor = _colorService.FromHex(repairedHex, _currentColor.Hue);
        ApplyColorToUiAndSession(repairedColor, ColorChangeSource.Preset, propagateToSession: false);
        EvaluateContrastAdvisory();
    }

    private void OnResetSlotClicked(object sender, RoutedEventArgs e)
    {
        if (_session.IsCompleted)
        {
            return;
        }

        var resetHex = _session.ResetSlotToDefault(_request.TargetSlot, _request.TargetState);
        _wasReset = true;
        var resetColor = _colorService.FromHex(resetHex, _currentColor.Hue);
        ApplyColorToUiAndSession(resetColor, ColorChangeSource.Reset, propagateToSession: false);
        EvaluateContrastAdvisory();
    }

    private async void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        if (_isClosingCleanly || _session.IsCompleted)
        {
            return;
        }

        ApplyButton.IsEnabled = false;
        CancelButton.IsEnabled = false;

        try
        {
            await _session.CommitAsync().ConfigureAwait(true);
            _isCommitted = true;
            _isClosingCleanly = true;
            Result = new ColorPickerResult(true, _currentColor, _lastSource, _wasReset);
            _completionTcs.TrySetResult(Result);

            if (DialogResult is null && IsLoaded)
            {
                try
                {
                    DialogResult = true;
                }
                catch (InvalidOperationException)
                {
                    Close();
                }
            }
            else
            {
                Close();
            }
        }
        catch
        {
            CancelButton.IsEnabled = true;
            ApplyButton.IsEnabled = true;
            throw;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        CancelAndClose();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelAndClose();
            e.Handled = true;
        }
    }

    private void CancelAndClose()
    {
        if (_isClosingCleanly)
        {
            return;
        }

        _isClosingCleanly = true;
        try
        {
            _session.Cancel();
        }
        finally
        {
            Result = new ColorPickerResult(false, _currentColor, _lastSource, _wasReset);
            _completionTcs.TrySetResult(Result);
            if (DialogResult is null && IsLoaded)
            {
                try
                {
                    DialogResult = false;
                }
                catch (InvalidOperationException)
                {
                    Close();
                }
            }
            else
            {
                Close();
            }
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_isCommitted && !_session.IsCompleted)
        {
            _session.Cancel();
        }

        Result ??= new ColorPickerResult(false, _currentColor, _lastSource, _wasReset);
        _completionTcs.TrySetResult(Result);
    }
}
