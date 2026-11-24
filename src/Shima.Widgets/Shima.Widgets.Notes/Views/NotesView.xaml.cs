using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

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

    private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (_viewModel.HasActiveNote)
            {
                e.Handled = true;
                _ = _viewModel.SaveActiveNoteAsync();
            }
        }
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

    private async void OnNewNoteClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.CreateNewNoteAsync().ConfigureAwait(true);
        BringSelectedNoteIntoView();
    }

    private void OnNoteRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string noteId })
        {
            _ = _viewModel.SelectNoteAsync(noteId);
        }
    }

    private async void OnPreviousNoteClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.SelectPreviousNoteAsync().ConfigureAwait(true);
        BringSelectedNoteIntoView();
    }

    private async void OnNextNoteClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.SelectNextNoteAsync().ConfigureAwait(true);
        BringSelectedNoteIntoView();
    }

    private void OnSaveActiveClicked(object sender, RoutedEventArgs e) =>
        _ = _viewModel.SaveActiveNoteAsync();

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

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _viewModel.OpenSettings();
    }

    private void BringSelectedNoteIntoView()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                var selectedItem = _viewModel.Notes.FirstOrDefault(n => n.IsSelected);
                if (selectedItem is null)
                {
                    return;
                }

                if (NotesItemsControl.ItemContainerGenerator.ContainerFromItem(selectedItem) is FrameworkElement container)
                {
                    container.BringIntoView();
                }
            }));
    }
}
