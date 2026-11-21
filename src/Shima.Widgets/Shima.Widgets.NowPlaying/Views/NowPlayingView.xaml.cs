using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Shima.Widgets.NowPlaying.Views;

public partial class NowPlayingView : UserControl
{
    private readonly NowPlayingWidgetViewModel _viewModel;

    public NowPlayingView(NowPlayingWidgetViewModel viewModel)
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

    private async void OnPreviousClicked(object sender, RoutedEventArgs e) =>
        await _viewModel.PreviousTrackAsync().ConfigureAwait(true);

    private async void OnPlayPauseClicked(object sender, RoutedEventArgs e) =>
        await _viewModel.TogglePlayPauseAsync().ConfigureAwait(true);

    private async void OnNextClicked(object sender, RoutedEventArgs e) =>
        await _viewModel.NextTrackAsync().ConfigureAwait(true);

    private void OnSeekPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.CanSeek)
        {
            _viewModel.BeginScrub();
        }
    }

    private async void OnSeekPreviewMouseUp(object sender, MouseButtonEventArgs e) =>
        await CommitCurrentSeekAsync().ConfigureAwait(true);

    private async void OnSeekLostMouseCapture(object sender, MouseEventArgs e) =>
        await CommitCurrentSeekAsync().ConfigureAwait(true);

    private async void OnSeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || !_viewModel.CanSeek)
        {
            return;
        }

        if (_viewModel.IsUserScrubbing)
        {
            _viewModel.PreviewScrubSeconds(e.NewValue);
            return;
        }

        if (SeekSlider.IsKeyboardFocusWithin && Math.Abs(e.NewValue - _viewModel.ProgressSeconds) >= 0.5)
        {
            await _viewModel.CommitScrubAsync(e.NewValue).ConfigureAwait(true);
        }
    }

    private async Task CommitCurrentSeekAsync()
    {
        if (_viewModel.IsUserScrubbing)
        {
            await _viewModel.CommitScrubAsync(SeekSlider.Value).ConfigureAwait(true);
        }
    }
}
