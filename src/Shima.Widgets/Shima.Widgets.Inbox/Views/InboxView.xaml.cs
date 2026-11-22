using System.Windows;
using System.Windows.Controls;

namespace Shima.Widgets.Inbox.Views;

public partial class InboxView : UserControl
{
    private readonly InboxWidgetViewModel _viewModel;

    public InboxView(InboxWidgetViewModel viewModel)
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

    private void OnClearAllClicked(object sender, RoutedEventArgs e) =>
        _ = _viewModel.ClearAllAsync();

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e) =>
        _viewModel.OpenWindowsNotificationSettings();

    private void OnRequestAccessClicked(object sender, RoutedEventArgs e) =>
        _ = _viewModel.RequestNotificationAccessAsync();

    private void OnRetryClicked(object sender, RoutedEventArgs e) =>
        _ = _viewModel.RefreshAccessStateAsync();

    private void OnToggleGroupClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string appId })
        {
            _viewModel.ToggleGroupExpanded(appId);
        }
    }

    private void OnClearGroupClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string appId })
        {
            _ = _viewModel.ClearAppGroupAsync(appId);
        }
    }

    private void OnDismissItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string notificationId })
        {
            _ = _viewModel.DismissNotificationAsync(notificationId);
        }
    }
}
