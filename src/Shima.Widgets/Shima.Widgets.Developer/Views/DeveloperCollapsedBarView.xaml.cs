using System.Windows.Controls;

namespace Shima.Widgets.Developer.Views;

public partial class DeveloperCollapsedBarView : UserControl
{
    public DeveloperCollapsedBarView(DeveloperWidgetViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        viewModel.AttachUiDispatcher(DispatchToUi);
        DataContext = viewModel;
    }

    private void DispatchToUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.BeginInvoke(action);
        }
    }
}
