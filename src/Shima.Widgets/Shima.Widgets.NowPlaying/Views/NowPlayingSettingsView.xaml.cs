using System.Windows;
using System.Windows.Controls;

namespace Shima.Widgets.NowPlaying.Views;

public partial class NowPlayingSettingsView : UserControl
{
    private readonly NowPlayingWidgetViewModel _viewModel;
    private bool _syncing;

    public NowPlayingSettingsView(NowPlayingWidgetViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += (_, _) => SyncCheckBoxes();
    }

    private void SyncCheckBoxes()
    {
        _syncing = true;
        try
        {
            ShowCompactPillCheck.IsChecked = _viewModel.ShowCompactActivePill;
            ShowCompactArtCheck.IsChecked = _viewModel.ShowAlbumArtInCompactPill;
        }
        finally
        {
            _syncing = false;
        }
    }

    private async void OnOptionToggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncing)
        {
            return;
        }

        if (ReferenceEquals(sender, ShowCompactPillCheck))
        {
            await _viewModel.SetShowCompactActivePillAsync(ShowCompactPillCheck.IsChecked == true).ConfigureAwait(true);
        }
        else if (ReferenceEquals(sender, ShowCompactArtCheck))
        {
            await _viewModel.SetShowAlbumArtInCompactPillAsync(ShowCompactArtCheck.IsChecked == true).ConfigureAwait(true);
        }
    }

    private async void OnResetDefaultsClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResetToDefaultsAsync().ConfigureAwait(true);
        SyncCheckBoxes();
    }
}
