using System.Windows;
using System.Windows.Controls;
using Shima.Core.Contracts;

namespace Shima.Widgets.FocusTimer.Views;

public partial class FocusTimerView : UserControl
{
    private readonly FocusTimerWidgetViewModel _viewModel;

    public FocusTimerView(FocusTimerWidgetViewModel viewModel)
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

    private void OnPhaseSelected(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string raw } && Enum.TryParse<FocusPhaseType>(raw, out var phase))
        {
            _viewModel.SelectPhase(phase);
        }
    }

    private void OnStartPauseClicked(object sender, RoutedEventArgs e) =>
        _viewModel.StartOrTogglePause();

    private void OnResetClicked(object sender, RoutedEventArgs e) =>
        _viewModel.StopAndReset();

    private void OnSkipClicked(object sender, RoutedEventArgs e) =>
        _viewModel.SkipToNextPhase();

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e) =>
        _viewModel.OpenSettings();

    private void OnNoiseSelected(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string raw } && Enum.TryParse<ZenNoiseType>(raw, out var noise))
        {
            _ = _viewModel.SetNoiseTypeAsync(noise);
        }
    }

    private void OnToggleGrayscaleClicked(object sender, RoutedEventArgs e) =>
        _ = _viewModel.ToggleGrayscaleAsync();

    private void OnToggleDimClicked(object sender, RoutedEventArgs e) =>
        _ = _viewModel.ToggleScreenDimAsync();
}
