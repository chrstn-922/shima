using System.Windows.Controls;

namespace Shima.Widgets.NowPlaying.Views;

public partial class NowPlayingCompactView : UserControl
{
    public NowPlayingCompactView(NowPlayingWidgetViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }
}
