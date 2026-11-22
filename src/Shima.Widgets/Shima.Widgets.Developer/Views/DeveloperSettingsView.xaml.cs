using System.Windows;
using System.Windows.Controls;

namespace Shima.Widgets.Developer.Views;

public partial class DeveloperSettingsView : UserControl
{
    private readonly DeveloperWidgetViewModel _viewModel;

    public DeveloperSettingsView(DeveloperWidgetViewModel viewModel)
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

    private async void OnInstallClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string adapterId })
        {
            await _viewModel.InstallOrReinstallHooksAsync(adapterId).ConfigureAwait(true);
        }
    }

    private async void OnUninstallClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string adapterId })
        {
            await _viewModel.UninstallHooksAsync(adapterId).ConfigureAwait(true);
        }
    }

    private async void OnCheckHealthClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string adapterId })
        {
            await _viewModel.CheckAdapterHealthAsync(adapterId).ConfigureAwait(true);
        }
    }
}
