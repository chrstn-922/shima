using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Shima.Widgets.Inbox.Views;

public partial class InboxSettingsView : UserControl
{
    private readonly InboxWidgetViewModel _viewModel;
    private bool _isLoading;

    public InboxSettingsView(InboxWidgetViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _isLoading = true;
        InitializeComponent();
        SyncControlsFromViewModel();
        _isLoading = false;
        _viewModel.SettingsChanged += (_, _) =>
        {
            void Apply()
            {
                _isLoading = true;
                SyncControlsFromViewModel();
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

    private void SyncControlsFromViewModel()
    {
        MaskBodyCheck.IsChecked = _viewModel.MaskNotificationBodyInPopups;
        PreviewDurationBox.Text = _viewModel.PreviewDurationSeconds.ToString(CultureInfo.InvariantCulture);
        ExcludedAppsBox.Text = _viewModel.ExcludedAppsInputText;
    }

    private void OnOpenWindowsSettingsClicked(object sender, RoutedEventArgs e) =>
        _viewModel.OpenWindowsNotificationSettings();

    private async void OnRequestAccessClicked(object sender, RoutedEventArgs e) =>
        await _viewModel.RequestNotificationAccessAsync().ConfigureAwait(true);

    private async void OnMaskBodyToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await _viewModel.SetMaskNotificationBodyAsync(MaskBodyCheck.IsChecked == true).ConfigureAwait(true);
    }

    private async void OnPreviewDurationLostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        if (int.TryParse(PreviewDurationBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            await _viewModel.SetPreviewDurationSecondsAsync(seconds).ConfigureAwait(true);
        }

        PreviewDurationBox.Text = _viewModel.PreviewDurationSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private async void OnSaveExclusionsClicked(object sender, RoutedEventArgs e) =>
        await _viewModel.SetExcludedAppsFromTextAsync(ExcludedAppsBox.Text).ConfigureAwait(true);

    private async void OnResetDefaultsClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResetToDefaultsAsync().ConfigureAwait(true);
        _isLoading = true;
        SyncControlsFromViewModel();
        _isLoading = false;
    }
}
