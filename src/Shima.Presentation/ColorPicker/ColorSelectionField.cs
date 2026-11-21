using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shima.Core.Color;

namespace Shima.Presentation.ColorPicker;

/// <summary>
/// Interactive 256x256 HSB Saturation (X) x Brightness (Y) WriteableBitmap surface with keyboard arrow support and crosshair reticle.
/// </summary>
public sealed class ColorSelectionField : FrameworkElement
{
    private const int BitmapResolution = 256;

    public static readonly DependencyProperty HueProperty =
        DependencyProperty.Register(
            nameof(Hue),
            typeof(double),
            typeof(ColorSelectionField),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnHueChanged));

    public static readonly DependencyProperty SaturationProperty =
        DependencyProperty.Register(
            nameof(Saturation),
            typeof(double),
            typeof(ColorSelectionField),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrightnessProperty =
        DependencyProperty.Register(
            nameof(Brightness),
            typeof(double),
            typeof(ColorSelectionField),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly WriteableBitmap _bitmap = new(BitmapResolution, BitmapResolution, 96, 96, PixelFormats.Bgra32, null);
    private readonly byte[] _pixelBuffer = new byte[BitmapResolution * BitmapResolution * 4];
    private double _renderedHue = double.NaN;
    private bool _isDragging;

    public ColorSelectionField()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        MinWidth = 180;
        MinHeight = 160;
        AutomationProperties.SetName(this, "Saturation and brightness selection field");
        AutomationProperties.SetAutomationId(this, "ColorSelectionField");
        UpdateHueBitmap();
    }

    public double Hue
    {
        get => (double)GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    public double Saturation
    {
        get => (double)GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, Math.Clamp(value, 0.0, 100.0));
    }

    public double Brightness
    {
        get => (double)GetValue(BrightnessProperty);
        set => SetValue(BrightnessProperty, Math.Clamp(value, 0.0, 100.0));
    }

    public event EventHandler<SaturationBrightnessChangedEventArgs>? SelectionChanged;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);

    private static void OnHueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColorSelectionField field)
        {
            field.UpdateHueBitmap();
        }
    }

    private void UpdateHueBitmap()
    {
        var hue = Hue;
        if (!double.IsNaN(_renderedHue) && Math.Abs(_renderedHue - hue) <= 0.01)
        {
            return;
        }

        _renderedHue = hue;
        var effectiveHue = hue >= 360.0 ? hue % 360.0 : (hue < 0 ? ((hue % 360.0) + 360.0) % 360.0 : hue);
        var sector = effectiveHue / 60.0;
        var f = sector - Math.Floor(sector);
        var (hueR, hueG, hueB) = sector switch
        {
            < 1.0 => (1.0, f, 0.0),
            < 2.0 => (1.0 - f, 1.0, 0.0),
            < 3.0 => (0.0, 1.0, f),
            < 4.0 => (0.0, 1.0 - f, 1.0),
            < 5.0 => (f, 0.0, 1.0),
            _ => (1.0, 0.0, 1.0 - f)
        };

        var idx = 0;
        for (var y = 0; y < BitmapResolution; y++)
        {
            var brightness = 1.0 - (y / (double)(BitmapResolution - 1));
            for (var x = 0; x < BitmapResolution; x++)
            {
                var sat = x / (double)(BitmapResolution - 1);
                var rNorm = brightness * ((1.0 - sat) + (sat * hueR));
                var gNorm = brightness * ((1.0 - sat) + (sat * hueG));
                var bNorm = brightness * ((1.0 - sat) + (sat * hueB));

                _pixelBuffer[idx++] = (byte)Math.Clamp((int)Math.Round(bNorm * 255.0, MidpointRounding.AwayFromZero), 0, 255);
                _pixelBuffer[idx++] = (byte)Math.Clamp((int)Math.Round(gNorm * 255.0, MidpointRounding.AwayFromZero), 0, 255);
                _pixelBuffer[idx++] = (byte)Math.Clamp((int)Math.Round(rNorm * 255.0, MidpointRounding.AwayFromZero), 0, 255);
                _pixelBuffer[idx++] = 255;
            }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, BitmapResolution, BitmapResolution), _pixelBuffer, BitmapResolution * 4, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        UpdateHueBitmap();
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawImage(_bitmap, rect);

        var borderBrush = TryFindResource("BorderHairline") as Brush ?? Brushes.Gray;
        dc.DrawRectangle(null, new Pen(borderBrush, 1.0), new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)));

        var cx = Math.Clamp(Saturation / 100.0, 0.0, 1.0) * ActualWidth;
        var cy = (1.0 - Math.Clamp(Brightness / 100.0, 0.0, 1.0)) * ActualHeight;
        var center = new Point(cx, cy);

        var outerBrush = TryFindResource("PickerReticleOuterBrush") as Brush ?? Brushes.White;
        var innerBrush = TryFindResource("PickerReticleInnerBrush") as Brush ?? Brushes.Black;
        dc.DrawEllipse(null, new Pen(outerBrush, 2.0), center, 7.0, 7.0);
        dc.DrawEllipse(null, new Pen(innerBrush, 1.25), center, 5.5, 5.5);

        if (IsKeyboardFocused)
        {
            var focusBrush = TryFindResource("FocusRingBrush") as Brush ?? outerBrush;
            dc.DrawRectangle(null, new Pen(focusBrush, 2.0), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _isDragging = true;
        CaptureMouse();
        UpdateFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateFromPoint(e.GetPosition(this));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDragging)
        {
            UpdateFromPoint(e.GetPosition(this));
            _isDragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _isDragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5.0 : 1.0;
        var sat = Saturation;
        var bri = Brightness;
        var handled = true;

        switch (e.Key)
        {
            case Key.Left:
                sat = Math.Clamp(sat - step, 0.0, 100.0);
                break;
            case Key.Right:
                sat = Math.Clamp(sat + step, 0.0, 100.0);
                break;
            case Key.Up:
                bri = Math.Clamp(bri + step, 0.0, 100.0);
                break;
            case Key.Down:
                bri = Math.Clamp(bri - step, 0.0, 100.0);
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            Saturation = sat;
            Brightness = bri;
            InvalidateVisual();
            SelectionChanged?.Invoke(this, new SaturationBrightnessChangedEventArgs(sat, bri, ColorChangeSource.GradientField));
            e.Handled = true;
        }
    }

    private void UpdateFromPoint(Point point)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var sat = Math.Clamp((point.X / ActualWidth) * 100.0, 0.0, 100.0);
        var bri = Math.Clamp((1.0 - (point.Y / ActualHeight)) * 100.0, 0.0, 100.0);
        Saturation = sat;
        Brightness = bri;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, new SaturationBrightnessChangedEventArgs(sat, bri, ColorChangeSource.GradientField));
    }
}

public sealed record SaturationBrightnessChangedEventArgs(
    double Saturation,
    double Brightness,
    ColorChangeSource Source);
