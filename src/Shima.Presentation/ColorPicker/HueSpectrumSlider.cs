using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shima.Core.Color;

namespace Shima.Presentation.ColorPicker;

/// <summary>
/// Interactive vertical 0-360 degree HSB Hue spectrum strip (360° red at top to 0° red at bottom)
/// with high-contrast thumb indicator, mouse wheel, and keyboard stepping.
/// </summary>
public sealed class HueSpectrumSlider : FrameworkElement
{
    private const int StripHeight = 360;
    private const int StripWidth = 2;

    public static readonly DependencyProperty HueProperty =
        DependencyProperty.Register(
            nameof(Hue),
            typeof(double),
            typeof(HueSpectrumSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly WriteableBitmap _spectrumBitmap = new(StripWidth, StripHeight, 96, 96, PixelFormats.Bgra32, null);
    private bool _isDragging;

    public HueSpectrumSlider()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        Width = 26;
        MinHeight = 160;
        AutomationProperties.SetName(this, "Hue spectrum slider");
        AutomationProperties.SetAutomationId(this, "HueSpectrumSlider");
        RenderSpectrumBitmap();
    }

    public double Hue
    {
        get => (double)GetValue(HueProperty);
        set => SetValue(HueProperty, Math.Clamp(value, 0.0, 360.0));
    }

    public event EventHandler<HueChangedEventArgs>? HueChanged;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new FrameworkElementAutomationPeer(this);

    private void RenderSpectrumBitmap()
    {
        var buffer = new byte[StripWidth * StripHeight * 4];
        var idx = 0;
        for (var y = 0; y < StripHeight; y++)
        {
            // 360° at top (y = 0) down to 0° at bottom (y = StripHeight - 1) per color-picker-architecture.md
            var h = (1.0 - (y / (double)(StripHeight - 1))) * 360.0;
            var sector = (h >= 360.0 ? 0.0 : h) / 60.0;
            var f = sector - Math.Floor(sector);
            var (rNorm, gNorm, bNorm) = sector switch
            {
                < 1.0 => (1.0, f, 0.0),
                < 2.0 => (1.0 - f, 1.0, 0.0),
                < 3.0 => (0.0, 1.0, f),
                < 4.0 => (0.0, 1.0 - f, 1.0),
                < 5.0 => (f, 0.0, 1.0),
                _ => (1.0, 0.0, 1.0 - f)
            };

            var r = (byte)Math.Clamp((int)Math.Round(rNorm * 255.0, MidpointRounding.AwayFromZero), 0, 255);
            var g = (byte)Math.Clamp((int)Math.Round(gNorm * 255.0, MidpointRounding.AwayFromZero), 0, 255);
            var b = (byte)Math.Clamp((int)Math.Round(bNorm * 255.0, MidpointRounding.AwayFromZero), 0, 255);

            for (var x = 0; x < StripWidth; x++)
            {
                buffer[idx++] = b;
                buffer[idx++] = g;
                buffer[idx++] = r;
                buffer[idx++] = 255;
            }
        }

        _spectrumBitmap.WritePixels(new Int32Rect(0, 0, StripWidth, StripHeight), buffer, StripWidth * 4, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        dc.DrawImage(_spectrumBitmap, new Rect(2, 0, Math.Max(0, ActualWidth - 4), ActualHeight));

        var borderBrush = TryFindResource("BorderHairline") as Brush ?? Brushes.Gray;
        dc.DrawRectangle(null, new Pen(borderBrush, 1.0), new Rect(2.5, 0.5, Math.Max(0, ActualWidth - 5), Math.Max(0, ActualHeight - 1)));

        var yPos = (1.0 - Math.Clamp(Hue / 360.0, 0.0, 1.0)) * ActualHeight;
        yPos = Math.Clamp(yPos, 3.0, Math.Max(3.0, ActualHeight - 3.0));

        var outerBrush = TryFindResource("PickerReticleOuterBrush") as Brush ?? Brushes.White;
        var innerBrush = TryFindResource("PickerReticleInnerBrush") as Brush ?? Brushes.Black;
        var thumbRect = new Rect(0.5, yPos - 3.0, Math.Max(4.0, ActualWidth - 1.0), 6.0);
        dc.DrawRoundedRectangle(outerBrush, new Pen(innerBrush, 1.25), thumbRect, 2.0, 2.0);

        if (IsKeyboardFocused)
        {
            var focusBrush = TryFindResource("FocusRingBrush") as Brush ?? outerBrush;
            dc.DrawRectangle(null, new Pen(focusBrush, 2.0), new Rect(0, 0, ActualWidth, ActualHeight));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _isDragging = true;
        CaptureMouse();
        UpdateHueFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateHueFromPoint(e.GetPosition(this));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isDragging)
        {
            UpdateHueFromPoint(e.GetPosition(this));
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

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var delta = e.Delta > 0 ? 1.0 : -1.0;
        ApplyHueStep(Hue + delta);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var arrowStep = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 15.0 : 1.0;
        var nextHue = Hue;
        var handled = true;

        switch (e.Key)
        {
            case Key.Up:
            case Key.Right:
                nextHue += arrowStep;
                break;
            case Key.Down:
            case Key.Left:
                nextHue -= arrowStep;
                break;
            case Key.PageUp:
                nextHue += 15.0;
                break;
            case Key.PageDown:
                nextHue -= 15.0;
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            ApplyHueStep(nextHue);
            e.Handled = true;
        }
    }

    private void ApplyHueStep(double candidateHue)
    {
        var clamped = Math.Clamp(candidateHue, 0.0, 360.0);
        Hue = clamped;
        InvalidateVisual();
        HueChanged?.Invoke(this, new HueChangedEventArgs(clamped, ColorChangeSource.HueSlider));
    }

    private void UpdateHueFromPoint(Point point)
    {
        if (ActualHeight <= 0)
        {
            return;
        }

        var nextHue = Math.Clamp((1.0 - (point.Y / ActualHeight)) * 360.0, 0.0, 360.0);
        ApplyHueStep(nextHue);
    }
}

public sealed record HueChangedEventArgs(double Hue, ColorChangeSource Source);
