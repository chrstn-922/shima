using System.Windows;
using System.Windows.Controls;
using Shima.Core.Contracts;

namespace Shima.Widgets.NowPlaying.Views;

public partial class NowPlayingCompactView : UserControl, ICompactCompanionAware
{
    public NowPlayingCompactView(NowPlayingWidgetViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    public void SetCompanionActive(bool isActive)
    {
        PlayingAccentDot.Visibility = isActive ? Visibility.Collapsed : Visibility.Visible;
    }
}
