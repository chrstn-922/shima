using System.Windows.Controls;

namespace Shima.Widgets.FocusTimer.Views;

public partial class FocusTimerCompactView : UserControl
{
    public FocusTimerCompactView(FocusTimerWidgetViewModel viewModel)
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }
}
