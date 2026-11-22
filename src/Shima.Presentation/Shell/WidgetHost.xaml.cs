using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Shima.Presentation.Shell;

public sealed record WidgetSlotItem(
    string WidgetId,
    string DisplayName,
    object? Content = null,
    Func<object?>? ContentFactory = null);

public sealed record WidgetDotViewModel(
    string WidgetId,
    string DisplayName,
    bool IsSelected,
    double DotOpacity,
    string SwitchActionLabel,
    string DotAutomationId);

public partial class WidgetHost : UserControl
{
    public const int MaxVisibleDots = 8;

    private readonly List<WidgetSlotItem> _slots = [];
    private readonly Dictionary<string, object?> _resolvedContents = new(StringComparer.Ordinal);
    private string? _selectedWidgetId;

    public WidgetHost()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshView();
            }
        };
    }

    public string? SelectedWidgetId => _selectedWidgetId;

    public event EventHandler<string>? SelectedWidgetChanged;

    public void SetSlots(IReadOnlyList<WidgetSlotItem> slots, string? selectedWidgetId = null)
    {
        ArgumentNullException.ThrowIfNull(slots);

        _slots.Clear();
        _resolvedContents.Clear();
        for (var i = 0; i < Math.Min(slots.Count, MaxVisibleDots); i++)
        {
            _slots.Add(slots[i]);
        }

        if (_slots.Count == 0)
        {
            _selectedWidgetId = null;
            RefreshView();
            return;
        }

        var nextId = selectedWidgetId;
        if (string.IsNullOrWhiteSpace(nextId)
            || !_slots.Any(slot => string.Equals(slot.WidgetId, nextId, StringComparison.Ordinal)))
        {
            nextId = _slots.Any(slot => string.Equals(slot.WidgetId, _selectedWidgetId, StringComparison.Ordinal))
                ? _selectedWidgetId
                : _slots[0].WidgetId;
        }

        _selectedWidgetId = nextId;
        RefreshView();
    }

    public void SelectWidget(string widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return;
        }

        if (!_slots.Any(slot => string.Equals(slot.WidgetId, widgetId, StringComparison.Ordinal)))
        {
            return;
        }

        if (string.Equals(_selectedWidgetId, widgetId, StringComparison.Ordinal))
        {
            return;
        }

        _selectedWidgetId = widgetId;
        RefreshView();
        SelectedWidgetChanged?.Invoke(this, widgetId);
    }

    public void StepSelection(int delta)
    {
        if (_slots.Count <= 1 || delta == 0)
        {
            return;
        }

        var currentIndex = _slots.FindIndex(
            slot => string.Equals(slot.WidgetId, _selectedWidgetId, StringComparison.Ordinal));
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var nextIndex = (currentIndex + delta) % _slots.Count;
        if (nextIndex < 0)
        {
            nextIndex += _slots.Count;
        }

        SelectWidget(_slots[nextIndex].WidgetId);
    }

    private void RefreshView()
    {
        var activeSlot = _slots.FirstOrDefault(
            slot => string.Equals(slot.WidgetId, _selectedWidgetId, StringComparison.Ordinal));

        if (activeSlot is null)
        {
            ActiveWidgetContentPresenter.Content = null;
            ActiveWidgetContentPresenter.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Visible;
            ActiveWidgetStatusText.Text = "No active widget modules loaded";
            AutomationProperties.SetName(
                ActiveWidgetStatusText,
                "Active widget status: No active widget modules loaded");
            PagerItemsControl.ItemsSource = Array.Empty<WidgetDotViewModel>();
            PagerItemsControl.Visibility = Visibility.Collapsed;
            return;
        }

        object? content = activeSlot.Content;
        if (content is null && activeSlot.ContentFactory is not null)
        {
            if (_resolvedContents.TryGetValue(activeSlot.WidgetId, out var cached))
            {
                content = cached;
            }
            else if (IsVisible)
            {
                content = activeSlot.ContentFactory();
                _resolvedContents[activeSlot.WidgetId] = content;
            }
        }

        if (content is not null)
        {
            ActiveWidgetContentPresenter.Content = content;
            ActiveWidgetContentPresenter.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ActiveWidgetContentPresenter.Content = null;
            ActiveWidgetContentPresenter.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Visible;
            ActiveWidgetStatusText.Text = $"Active widget: {activeSlot.DisplayName}";
            AutomationProperties.SetName(ActiveWidgetStatusText, $"Active widget: {activeSlot.DisplayName}");
        }

        var dots = _slots
            .Select(slot =>
            {
                var isSelected = string.Equals(slot.WidgetId, activeSlot.WidgetId, StringComparison.Ordinal);
                return new WidgetDotViewModel(
                    slot.WidgetId,
                    slot.DisplayName,
                    isSelected,
                    isSelected ? 1.0 : 0.30,
                    $"Switch to {slot.DisplayName} widget",
                    $"WidgetDot_{slot.WidgetId}");
            })
            .ToArray();

        PagerItemsControl.ItemsSource = dots;
        PagerItemsControl.Visibility = Visibility.Visible;
    }

    private void OnDotClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string widgetId })
        {
            SelectWidget(widgetId);
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_slots.Count <= 1 || e.Delta == 0 || ShouldSuppressWheelWidgetSwitch(e.OriginalSource))
        {
            return;
        }

        StepSelection(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTextInputFocused(e.OriginalSource))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Right:
            case Key.Down:
            case Key.PageDown:
                StepSelection(1);
                e.Handled = true;
                break;

            case Key.Left:
            case Key.Up:
            case Key.PageUp:
                StepSelection(-1);
                e.Handled = true;
                break;
        }
    }

    private static bool IsTextInputFocused(object? originalSource)
    {
        if (Keyboard.FocusedElement is TextBoxBase)
        {
            return true;
        }

        for (var current = originalSource as DependencyObject; current is not null;)
        {
            if (current is TextBoxBase)
            {
                return true;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool ShouldSuppressWheelWidgetSwitch(object? originalSource)
    {
        for (var current = originalSource as DependencyObject; current is not null;)
        {
            if (ReferenceEquals(current, PagerItemsControl))
            {
                return false;
            }

            if (current is ScrollViewer { ScrollableHeight: > 0 })
            {
                return true;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
