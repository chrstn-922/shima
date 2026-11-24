using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Shima.Widgets.VolumeMixer.Views;

public partial class VolumeMixerView : UserControl
{
    private readonly VolumeMixerWidgetViewModel _viewModel;

    public VolumeMixerView(VolumeMixerWidgetViewModel viewModel)
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
            else
            {
                _ = Dispatcher.BeginInvoke(action);
            }
        });
    }

    private async void OnMasterMuteClicked(object sender, RoutedEventArgs e) =>
        await _viewModel.ToggleMasterMuteAsync().ConfigureAwait(true);

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _viewModel.OpenSettings();
    }

    private void OnMasterSliderMouseDown(object sender, MouseButtonEventArgs e) =>
        _viewModel.IsMasterDragging = true;

    private async void OnMasterSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        _viewModel.IsMasterDragging = false;
        await _viewModel.SetMasterVolumePercentAsync(MasterVolumeSlider.Value).ConfigureAwait(true);
    }

    private void OnMasterSliderLostCapture(object sender, MouseEventArgs e) =>
        _viewModel.IsMasterDragging = false;

    private async void OnMasterSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (Math.Abs(e.NewValue - _viewModel.MasterVolumePercent) >= 0.5)
        {
            await _viewModel.SetMasterVolumePercentAsync(e.NewValue).ConfigureAwait(true);
        }
    }

    private async void OnSessionMuteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sessionKey })
        {
            await _viewModel.ToggleSessionMuteAsync(sessionKey).ConfigureAwait(true);
        }
    }

    private void OnSessionSliderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { DataContext: AudioSessionRowViewModel row })
        {
            row.IsUserDragging = true;
        }
    }

    private async void OnSessionSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { DataContext: AudioSessionRowViewModel row } slider)
        {
            row.IsUserDragging = false;
            await _viewModel.SetSessionVolumePercentAsync(row.SessionKey, slider.Value).ConfigureAwait(true);
        }
    }

    private void OnSessionSliderLostCapture(object sender, MouseEventArgs e)
    {
        if (sender is Slider { DataContext: AudioSessionRowViewModel row })
        {
            row.IsUserDragging = false;
        }
    }

    private async void OnSessionSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || sender is not Slider { DataContext: AudioSessionRowViewModel row })
        {
            return;
        }

        if (Math.Abs(e.NewValue - row.VolumePercent) >= 0.5)
        {
            await _viewModel.SetSessionVolumePercentAsync(row.SessionKey, e.NewValue).ConfigureAwait(true);
        }
    }
}
