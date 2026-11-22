using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Shima.Widgets.Clipboard.Views;

public partial class ClipboardView : UserControl
{
    private readonly ClipboardWidgetViewModel _viewModel;

    public ClipboardView(ClipboardWidgetViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        InitializeComponent();
        _viewModel.AttachUiDispatcher(action =>
        {
            if (Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.BeginInvoke(action);
            }
        });
    }

    private void OnSearchGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _viewModel.NotifyInputFocusChanged(true);

    private void OnSearchLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _viewModel.NotifyInputFocusChanged(false);

    private void OnPauseToggleClicked(object sender, RoutedEventArgs e) =>
        _ = _viewModel.SetPausedAsync(!_viewModel.IsPaused);

    private void OnClearUnpinnedClicked(object sender, RoutedEventArgs e) =>
        _ = _viewModel.ClearUnpinnedAsync();

    private void OnRecopyClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string entryId })
        {
            _ = _viewModel.RecopyEntryAsync(entryId);
        }
    }

    private void OnPinEntryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string entryId })
        {
            _ = _viewModel.TogglePinAsync(entryId);
        }
    }

    private void OnDeleteEntryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string entryId })
        {
            _ = _viewModel.DeleteEntryAsync(entryId);
        }
    }
}
