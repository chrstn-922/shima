using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shima.Core.Color;

namespace Shima.Presentation.ColorPicker;

/// <summary>
/// Synchronized Hex, HSB, and RGB numeric inputs with clamping and shorthand expansion.
/// </summary>
public partial class ColorNumericInputs : UserControl
{
    private IColorProcessingService _colorService = new CoreColorProcessingEngine();
    private ShimaColor? _currentColor;
    private bool _isSyncing;

    public ColorNumericInputs()
    {
        InitializeComponent();
    }

    public event EventHandler<NumericColorEditedEventArgs>? ColorEdited;

    public void ConfigureColorService(IColorProcessingService colorService) =>
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));

    public void UpdateFromColor(ShimaColor color)
    {
        ArgumentNullException.ThrowIfNull(color);
        _currentColor = color;
        _isSyncing = true;
        try
        {
            HexInputBox.Text = color.Hex;
            HueInputBox.Text = Math.Round(color.Hue, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
            SaturationInputBox.Text = Math.Round(color.Saturation, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
            BrightnessInputBox.Text = Math.Round(color.Brightness, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
            RedInputBox.Text = color.R.ToString(CultureInfo.InvariantCulture);
            GreenInputBox.Text = color.G.ToString(CultureInfo.InvariantCulture);
            BlueInputBox.Text = color.B.ToString(CultureInfo.InvariantCulture);
            ClearValidationHint();
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (ReferenceEquals(sender, HexInputBox))
        {
            TryCommitHex(revertIfInvalid: false);
        }
        else if (ReferenceEquals(sender, HueInputBox) || ReferenceEquals(sender, SaturationInputBox) || ReferenceEquals(sender, BrightnessInputBox))
        {
            TryCommitHsb();
        }
        else
        {
            TryCommitRgb();
        }

        e.Handled = true;
    }

    private void OnHexTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncing)
        {
            return;
        }

        var raw = HexInputBox.Text?.Trim() ?? string.Empty;
        var digitCount = raw.StartsWith('#') ? raw.Length - 1 : raw.Length;
        if (digitCount == 6 && _colorService.TryParseHex(raw, out var parsed, _currentColor?.Hue) && parsed is not null)
        {
            ClearValidationHint();
            ColorEdited?.Invoke(this, new NumericColorEditedEventArgs(parsed, ColorChangeSource.HexInput));
        }
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isSyncing)
        {
            TryCommitHex(revertIfInvalid: true);
        }
    }

    private void OnHsbLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isSyncing)
        {
            TryCommitHsb();
        }
    }

    private void OnRgbLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isSyncing)
        {
            TryCommitRgb();
        }
    }

    private void TryCommitHex(bool revertIfInvalid)
    {
        if (_colorService.TryParseHex(HexInputBox.Text, out var parsed, _currentColor?.Hue) && parsed is not null)
        {
            ClearValidationHint();
            ColorEdited?.Invoke(this, new NumericColorEditedEventArgs(parsed, ColorChangeSource.HexInput));
            return;
        }

        ShowValidationHint("Enter a valid #RGB or #RRGGBB hex value.");
        if (revertIfInvalid && _currentColor is not null)
        {
            _isSyncing = true;
            HexInputBox.Text = _currentColor.Hex;
            _isSyncing = false;
        }
    }

    private void TryCommitHsb()
    {
        if (double.TryParse(HueInputBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            && double.TryParse(SaturationInputBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            && double.TryParse(BrightnessInputBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
        {
            var clampedH = Math.Clamp(h, 0.0, 360.0);
            var clampedS = Math.Clamp(s, 0.0, 100.0);
            var clampedB = Math.Clamp(b, 0.0, 100.0);
            var next = _colorService.FromHsb(clampedH, clampedS, clampedB);
            ClearValidationHint();
            ColorEdited?.Invoke(this, new NumericColorEditedEventArgs(next, ColorChangeSource.HsbInput));
            return;
        }

        ShowValidationHint("HSB channels accept numbers: H (0-360), S (0-100), B (0-100).");
    }

    private void TryCommitRgb()
    {
        if (int.TryParse(RedInputBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
            && int.TryParse(GreenInputBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)
            && int.TryParse(BlueInputBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            var next = _colorService.FromRgbClamped(r, g, b, _currentColor?.Hue);
            ClearValidationHint();
            ColorEdited?.Invoke(this, new NumericColorEditedEventArgs(next, ColorChangeSource.RgbInput));
            return;
        }

        ShowValidationHint("RGB channels accept integers clamped to 0-255.");
    }

    private void ShowValidationHint(string message)
    {
        ValidationHintText.Text = message;
        ValidationHintText.Visibility = Visibility.Visible;
    }

    private void ClearValidationHint()
    {
        ValidationHintText.Text = string.Empty;
        ValidationHintText.Visibility = Visibility.Collapsed;
    }
}

public sealed record NumericColorEditedEventArgs(ShimaColor Color, ColorChangeSource Source);
