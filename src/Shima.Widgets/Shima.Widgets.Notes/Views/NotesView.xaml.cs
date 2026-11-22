using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Shima.Widgets.Notes.Views;

public partial class NotesView : UserControl
{
    private readonly NotesWidgetViewModel _viewModel;

    public NotesView(NotesWidgetViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        InitializeComponent();
        _viewModel.AttachUiDispatcher(action =>
        {
            if (Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.BeginInvoke(action);
            }
        });
    }

    private void OnInputGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _viewModel.NotifyInputFocusChanged(true);

    private void OnInputLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!NotesSearchBox.IsKeyboardFocusWithin && !NoteEditorBox.IsKeyboardFocusWithin)
        {
            _viewModel.NotifyInputFocusChanged(false);
        }
    }

    private void OnNewNoteClicked(object sender, RoutedEventArgs e) =>
        _ = _viewModel.CreateNewNoteAsync();

    private void OnNoteRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string noteId })
        {
            _ = _viewModel.SelectNoteAsync(noteId);
        }
    }

    private void OnPinActiveClicked(object sender, RoutedEventArgs e)
    {
        var activeId = _viewModel.ActiveNoteId;
        if (!string.IsNullOrWhiteSpace(activeId))
        {
            _ = _viewModel.TogglePinAsync(activeId);
        }
    }

    private void OnCopyActiveClicked(object sender, RoutedEventArgs e) =>
        _ = _viewModel.CopyActiveNoteAsync();

    private void OnDeleteActiveClicked(object sender, RoutedEventArgs e)
    {
        var activeId = _viewModel.ActiveNoteId;
        if (!string.IsNullOrWhiteSpace(activeId))
        {
            _ = _viewModel.DeleteNoteAsync(activeId);
        }
    }
}
