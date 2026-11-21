using System.Windows;
using System.Windows.Controls;

namespace Shima.Widgets.Clock.Views;

/// <summary>
/// Two-column expanded Clock view matching the Dynamic Island reference layout.
/// </summary>
public partial class ClockView : UserControl
{
    private readonly ClockWidgetViewModel _viewModel;

    public ClockView(ClockWidgetViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.AttachUiDispatcher(action =>
        {
            if (Dispatcher.CheckAccess())
            {
                action();
            }
            else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvoke(action);
            }
        });
    }

    private async void OnCityClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string locationId })
        {
            await _viewModel.SelectCityAsync(locationId).ConfigureAwait(true);
        }
    }

    private void OnSettingsGearClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenSettings();
    }
}
