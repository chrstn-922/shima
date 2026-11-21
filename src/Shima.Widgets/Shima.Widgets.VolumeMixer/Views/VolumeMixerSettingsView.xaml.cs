using System.Windows;
using System.Windows.Controls;

namespace Shima.Widgets.VolumeMixer.Views;

public partial class VolumeMixerSettingsView : UserControl
{
    private readonly VolumeMixerWidgetViewModel _viewModel;
    private bool _syncing;

    public VolumeMixerSettingsView(VolumeMixerWidgetViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += (_, _) => SyncCheckBox();
    }

    private void SyncCheckBox()
    {
        _syncing = true;
        try
        {
            ShowSystemSoundsCheck.IsChecked = _viewModel.ShowSystemSounds;
        }
        finally
        {
            _syncing = false;
        }
    }

    private async void OnShowSystemSoundsToggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncing)
        {
            return;
        }

        await _viewModel.SetShowSystemSoundsAsync(ShowSystemSoundsCheck.IsChecked == true).ConfigureAwait(true);
    }

    private async void OnResetDefaultsClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResetToDefaultsAsync().ConfigureAwait(true);
        SyncCheckBox();
    }
}
