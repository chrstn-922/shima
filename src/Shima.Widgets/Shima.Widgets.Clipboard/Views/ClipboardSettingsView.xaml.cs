using System.Windows;
using System.Windows.Controls;

namespace Shima.Widgets.Clipboard.Views;

public partial class ClipboardSettingsView : UserControl
{
    private readonly ClipboardWidgetViewModel _viewModel;
    private bool _isLoading;

    public ClipboardSettingsView(ClipboardWidgetViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _isLoading = true;
        InitializeComponent();
        _isLoading = false;
        _viewModel.SettingsChanged += (_, _) =>
        {
            void Apply()
            {
                _isLoading = true;
                CaptureImagesCheck.IsChecked = _viewModel.CaptureImages;
                PauseClipboardCheck.IsChecked = _viewModel.IsPaused;
                _isLoading = false;
            }

            if (Dispatcher.CheckAccess())
            {
                Apply();
            }
            else
            {
                Dispatcher.BeginInvoke(Apply);
            }
        };
    }

    private async void OnCaptureImagesToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await _viewModel.SetCaptureImagesAsync(CaptureImagesCheck.IsChecked == true).ConfigureAwait(true);
    }

    private async void OnPauseToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await _viewModel.SetPausedAsync(PauseClipboardCheck.IsChecked == true).ConfigureAwait(true);
    }

    private async void OnClearUnpinnedClicked(object sender, RoutedEventArgs e) =>
        await _viewModel.ClearUnpinnedAsync().ConfigureAwait(true);

    private async void OnResetDefaultsClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResetToDefaultsAsync().ConfigureAwait(true);
        _isLoading = true;
        CaptureImagesCheck.IsChecked = _viewModel.CaptureImages;
        PauseClipboardCheck.IsChecked = _viewModel.IsPaused;
        _isLoading = false;
    }
}
