using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Shima.Core.Contracts;

namespace Shima.Widgets.FocusTimer.Views;

public partial class FocusTimerSettingsView : UserControl
{
    private readonly FocusTimerWidgetViewModel _viewModel;
    private bool _syncing;

    public FocusTimerSettingsView(FocusTimerWidgetViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        InitializeComponent();
        SyncTextBoxesFromViewModel();
    }

    private void SyncTextBoxesFromViewModel()
    {
        _syncing = true;
        try
        {
            WorkMinutesBox.Text = _viewModel.WorkMinutes.ToString(CultureInfo.InvariantCulture);
            ShortBreakMinutesBox.Text = _viewModel.ShortBreakMinutes.ToString(CultureInfo.InvariantCulture);
            LongBreakMinutesBox.Text = _viewModel.LongBreakMinutes.ToString(CultureInfo.InvariantCulture);
            CyclesBox.Text = _viewModel.CyclesBeforeLongBreak.ToString(CultureInfo.InvariantCulture);
            AutoStartCheck.IsChecked = _viewModel.AutoStartNextPhase;
            GrayscaleCheck.IsChecked = _viewModel.IsGrayscaleEnabled;
            DimCheck.IsChecked = _viewModel.IsDimEnabled;
            PauseFullscreenCheck.IsChecked = _viewModel.PauseScreenEffectOnFullscreen;
        }
        finally
        {
            _syncing = false;
        }
    }

    private async Task CommitIntervalsAsync()
    {
        if (_syncing)
        {
            return;
        }

        var work = int.TryParse(WorkMinutesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            ? w
            : _viewModel.WorkMinutes;
        var shortBreak = int.TryParse(ShortBreakMinutesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sb)
            ? sb
            : _viewModel.ShortBreakMinutes;
        var longBreak = int.TryParse(LongBreakMinutesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lb)
            ? lb
            : _viewModel.LongBreakMinutes;
        var cycles = int.TryParse(CyclesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
            ? c
            : _viewModel.CyclesBeforeLongBreak;
        var autoStart = AutoStartCheck.IsChecked == true;

        await _viewModel.UpdateIntervalsAsync(work, shortBreak, longBreak, cycles, autoStart).ConfigureAwait(true);
        SyncTextBoxesFromViewModel();
    }

    private async void OnIntervalsLostFocus(object sender, RoutedEventArgs e) =>
        await CommitIntervalsAsync().ConfigureAwait(true);

    private async void OnBehaviorToggled(object sender, RoutedEventArgs e) =>
        await CommitIntervalsAsync().ConfigureAwait(true);

    private void OnNoiseSelected(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string raw } && Enum.TryParse<ZenNoiseType>(raw, out var noise))
        {
            _ = _viewModel.SetNoiseTypeAsync(noise);
        }
    }

    private async void OnDisplayOptionToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var gray = GrayscaleCheck.IsChecked == true;
        var dim = DimCheck.IsChecked == true;
        var pauseFullscreen = PauseFullscreenCheck.IsChecked == true;
        await _viewModel.SetDisplayOptionsAsync(gray, dim, _viewModel.DimOpacity, pauseFullscreen).ConfigureAwait(true);
    }

    private async void OnResetDefaultsClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResetToDefaultsAsync().ConfigureAwait(true);
        SyncTextBoxesFromViewModel();
    }

    private void OnVerifyRestoreDisplayClicked(object sender, RoutedEventArgs e) =>
        _viewModel.VerifyAndRestoreDisplay();
}
