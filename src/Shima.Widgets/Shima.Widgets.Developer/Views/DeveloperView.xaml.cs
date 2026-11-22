using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Shima.Widgets.Developer.Views;

public partial class DeveloperView : UserControl
{
    private readonly DeveloperWidgetViewModel _viewModel;

    public DeveloperView(DeveloperWidgetViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        _viewModel.AttachUiDispatcher(DispatchToUi);
        DataContext = _viewModel;
    }

    private void DispatchToUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.BeginInvoke(action);
        }
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _viewModel.OpenSettings();
    }

    private async void OnAllowClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: AgentSessionRowViewModel row }
            && !string.IsNullOrWhiteSpace(row.PendingRequestId))
        {
            await _viewModel.ApproveSessionAsync(row.SessionId, row.PendingRequestId).ConfigureAwait(true);
        }
    }

    private async void OnDenyClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: AgentSessionRowViewModel row }
            && !string.IsNullOrWhiteSpace(row.PendingRequestId))
        {
            await _viewModel.DenySessionAsync(row.SessionId, row.PendingRequestId).ConfigureAwait(true);
        }
    }

    private void OnDismissSessionClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: string sessionId })
        {
            _viewModel.DismissSession(sessionId);
        }
    }

    private async void OnSessionRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AgentSessionRowViewModel row }
            && row.IsNonApprovalRowClickable)
        {
            e.Handled = true;
            await _viewModel.FocusSessionTerminalAsync(row.SessionId).ConfigureAwait(true);
        }
    }
}
