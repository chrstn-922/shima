using System.Windows;
using System.Windows.Controls;

namespace Shima.Widgets.Clock.Views;

/// <summary>
/// Clock settings panel embedded in SettingsWindow allowing 12h/24h, seconds, city catalog search, and ordering.
/// </summary>
public partial class ClockSettingsView : UserControl
{
    private readonly ClockWidgetViewModel _viewModel;
    private bool _syncing;

    public ClockSettingsView(ClockWidgetViewModel viewModel)
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
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ClockWidgetViewModel.Use24HourFormat) or nameof(ClockWidgetViewModel.ShowSeconds))
            {
                SyncCheckBoxes();
            }
        };
        SyncCheckBoxes();
    }

    private void SyncCheckBoxes()
    {
        _syncing = true;
        try
        {
            Use24HourCheck.IsChecked = _viewModel.Use24HourFormat;
            ShowSecondsCheck.IsChecked = _viewModel.ShowSeconds;
        }
        finally
        {
            _syncing = false;
        }
    }

    private async void OnFormatToggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncing)
        {
            return;
        }

        if (ReferenceEquals(sender, Use24HourCheck))
        {
            await _viewModel.SetUse24HourFormatAsync(Use24HourCheck.IsChecked == true).ConfigureAwait(true);
        }
        else if (ReferenceEquals(sender, ShowSecondsCheck))
        {
            await _viewModel.SetShowSecondsAsync(ShowSecondsCheck.IsChecked == true).ConfigureAwait(true);
        }
    }

    private async void OnSetPrimaryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            await _viewModel.SelectCityAsync(id).ConfigureAwait(true);
        }
    }

    private async void OnMoveUpClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            await _viewModel.MoveCityAsync(id, -1).ConfigureAwait(true);
        }
    }

    private async void OnMoveDownClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            await _viewModel.MoveCityAsync(id, +1).ConfigureAwait(true);
        }
    }

    private async void OnRemoveCityClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            await _viewModel.RemoveCityAsync(id).ConfigureAwait(true);
        }
    }

    private async void OnAddCatalogCityClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CatalogCityEntry entry })
        {
            await _viewModel.AddCityAsync(entry).ConfigureAwait(true);
        }
    }

    private async void OnResetDefaultsClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResetToDefaultsAsync().ConfigureAwait(true);
        SyncCheckBoxes();
    }
}
