using System.Windows;
using System.Windows.Controls;

namespace Shima.Widgets.Notes.Views;

public partial class NotesSettingsView : UserControl
{
    private readonly NotesWidgetViewModel _viewModel;
    private bool _isLoading;

    public NotesSettingsView(NotesWidgetViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _isLoading = true;
        InitializeComponent();
        _isLoading = false;
    }

    private async void OnOptionToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await _viewModel.SetShowWordCountAsync(ShowWordCountCheck.IsChecked == true).ConfigureAwait(true);
    }

    private async void OnResetDefaultsClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResetToDefaultsAsync().ConfigureAwait(true);
        _isLoading = true;
        ShowWordCountCheck.IsChecked = _viewModel.ShowWordCount;
        _isLoading = false;
    }
}
