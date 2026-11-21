using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Shima.Presentation.ColorPicker;

/// <summary>
/// Displays clickable Original (snap-back) vs live Preview swatches and the 12-slot recent color FIFO selector.
/// </summary>
public partial class ColorPreviewPanel : UserControl
{
    public static readonly DependencyProperty OriginalBrushProperty =
        DependencyProperty.Register(nameof(OriginalBrush), typeof(Brush), typeof(ColorPreviewPanel), new PropertyMetadata(Brushes.Black));

    public static readonly DependencyProperty OriginalHexProperty =
        DependencyProperty.Register(nameof(OriginalHex), typeof(string), typeof(ColorPreviewPanel), new PropertyMetadata("#000000"));

    public ColorPreviewPanel()
    {
        InitializeComponent();
    }

    public Brush OriginalBrush
    {
        get => (Brush)GetValue(OriginalBrushProperty);
        set => SetValue(OriginalBrushProperty, value);
    }

    public string OriginalHex
    {
        get => (string)GetValue(OriginalHexProperty);
        set => SetValue(OriginalHexProperty, value);
    }

    public event EventHandler<string>? RecentColorSelected;

    public event EventHandler<string>? OriginalColorClicked;

    public void SetComparisonColors(string originalHex, string currentHex)
    {
        OriginalHex = originalHex;
        OriginalBrush = CreateFrozenBrush(originalHex);
        CurrentHexLabel.Text = currentHex;
        CurrentSwatchBorder.Background = CreateFrozenBrush(currentHex);
    }

    public void SetRecentColors(IReadOnlyList<string> recentHexes)
    {
        var items = (recentHexes ?? Array.Empty<string>())
            .Select((hex, index) => new RecentSwatchViewModel(
                hex,
                CreateFrozenBrush(hex),
                $"Apply recent color {hex}",
                $"RecentColorSwatch_{index}"))
            .ToArray();

        RecentColorsItemsControl.ItemsSource = items;
        NoRecentsText.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOriginalSwatchClicked(object sender, RoutedEventArgs e)
    {
        OriginalColorClicked?.Invoke(this, OriginalHex);
    }

    private void OnRecentSwatchClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            RecentColorSelected?.Invoke(this, hex);
        }
    }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color parsed)
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

    public sealed record RecentSwatchViewModel(
        string Hex,
        Brush SwatchBrush,
        string AccessibleActionName,
        string AutomationId);
}
