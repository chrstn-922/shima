using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Shima.Core.Contracts;
using Shima.Core.Shell;

namespace Shima.Widgets.Notes;

public sealed record NoteSummaryViewModel(
    string NoteId,
    string Title,
    string PreviewSnippet,
    bool IsPinned,
    bool IsSelected,
    string UpdatedLabel,
    string AccessibleState,
    string SelectActionLabel,
    string PinActionLabel);

/// <summary>
/// Observable view-model managing local scratchpad notes, 500ms debounced auto-save,
/// 200KB cap enforcement, keyword search, and focus-safe island collapse locking.
/// </summary>
public sealed class NotesWidgetViewModel : INotifyPropertyChanged, IDisposable
{
    public static readonly TimeSpan AutoSaveDebounceDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan CopyFeedbackDuration = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan SaveFeedbackDuration = TimeSpan.FromMilliseconds(1500);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly INoteRepository _repository;
    private readonly INotesDebounceTimer _debounceTimer;
    private readonly IClipboardMonitorAdapter? _clipboardAdapter;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly Func<string, CancellationToken, Task<bool>>? _copyTextOverride;
    private readonly IOneShotTimer _copyFeedbackTimer;
    private readonly bool _ownsCopyFeedbackTimer;
    private readonly IOneShotTimer _saveFeedbackTimer;
    private readonly bool _ownsSaveFeedbackTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _sync = new();

    private IWidgetContext? _context;
    private Action<Action>? _uiDispatcher;
    private NotesSettings _settings = new();
    private List<NoteEntry> _allNotes = [];

    private string? _activeNoteId;
    private string _activeTitle = "Quick notes";
    private bool _activeIsPinned;
    private DateTimeOffset _activeCreatedAtUtc;
    private string _editorContent = string.Empty;
    private string _searchQuery = string.Empty;
    private string _saveStatusLabel = "Saved";
    private string _wordCountLabel = "0 words";
    private string _noteCountLabel = "0 notes";
    private string _selectedPositionLabel = "0/0";
    private bool _hasNotes;
    private bool _canNavigateNotes;
    private bool _isDirty;
    private bool _hasUnsavedChanges;
    private bool _isSavedFeedbackActive;
    private bool _isCopiedFeedbackActive;
    private bool _isCopyError;
    private bool _suppressEditorDirtyTracking;
    private bool _isInputFocused;
    private bool _disposed;

    public NotesWidgetViewModel(
        INoteRepository repository,
        INotesDebounceTimer debounceTimer,
        IClipboardMonitorAdapter? clipboardAdapter = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        Func<string, CancellationToken, Task<bool>>? copyTextOverride = null,
        IOneShotTimer? copyFeedbackTimer = null,
        IOneShotTimer? saveFeedbackTimer = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _debounceTimer = debounceTimer ?? throw new ArgumentNullException(nameof(debounceTimer));
        _clipboardAdapter = clipboardAdapter;
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _copyTextOverride = copyTextOverride;
        _ownsCopyFeedbackTimer = copyFeedbackTimer is null;
        _copyFeedbackTimer = copyFeedbackTimer ?? new OneShotTimer();
        _ownsSaveFeedbackTimer = saveFeedbackTimer is null;
        _saveFeedbackTimer = saveFeedbackTimer ?? new OneShotTimer();
    }

    public ObservableCollection<NoteSummaryViewModel> Notes { get; } = [];

    public string? ActiveNoteId
    {
        get => _activeNoteId;
        private set
        {
            if (SetField(ref _activeNoteId, value))
            {
                OnPropertyChanged(nameof(HasActiveNote));
                OnPropertyChanged(nameof(PinActiveActionLabel));
                OnPropertyChanged(nameof(PinButtonLabel));
                OnPropertyChanged(nameof(SaveActiveActionLabel));
            }
        }
    }

    public bool HasActiveNote => !string.IsNullOrWhiteSpace(_activeNoteId);

    public string ActiveTitle
    {
        get => _activeTitle;
        private set => SetField(ref _activeTitle, value);
    }

    public bool ActiveIsPinned
    {
        get => _activeIsPinned;
        private set
        {
            if (SetField(ref _activeIsPinned, value))
            {
                OnPropertyChanged(nameof(PinActiveActionLabel));
                OnPropertyChanged(nameof(PinButtonLabel));
            }
        }
    }

    public string PinActiveActionLabel => ActiveIsPinned ? "Unpin note" : "Pin note";

    public string PinButtonLabel => ActiveIsPinned ? "Unpin" : "Pin";

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (SetField(ref _hasUnsavedChanges, value))
            {
                OnPropertyChanged(nameof(SaveButtonLabel));
                OnPropertyChanged(nameof(SaveActiveActionLabel));
            }
        }
    }

    public bool IsSavedFeedbackActive
    {
        get => _isSavedFeedbackActive;
        private set
        {
            if (SetField(ref _isSavedFeedbackActive, value))
            {
                OnPropertyChanged(nameof(SaveButtonLabel));
                OnPropertyChanged(nameof(SaveActiveActionLabel));
            }
        }
    }

    public string SaveButtonLabel => _isSavedFeedbackActive ? "Saved" : "Save";

    public string SaveActiveActionLabel =>
        _isSavedFeedbackActive
            ? "Note saved"
            : _hasUnsavedChanges
                ? "Save unsaved note changes"
                : "Save note";

    internal bool IsSaveFeedbackTimerPending => _saveFeedbackTimer.IsPending;

    public string NoteCountLabel
    {
        get => _noteCountLabel;
        private set => SetField(ref _noteCountLabel, value);
    }

    public string SelectedPositionLabel
    {
        get => _selectedPositionLabel;
        private set => SetField(ref _selectedPositionLabel, value);
    }

    public bool CanNavigateNotes
    {
        get => _canNavigateNotes;
        private set => SetField(ref _canNavigateNotes, value);
    }

    public bool HasNotes
    {
        get => _hasNotes;
        private set
        {
            if (SetField(ref _hasNotes, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public bool ShowEmptyState => !_hasNotes;

    public string EditorContent
    {
        get => _editorContent;
        set
        {
            var incoming = value ?? string.Empty;
            var (normalized, wasTruncated) = ClampToMaxBytes(incoming);

            if (!SetField(ref _editorContent, normalized))
            {
                if (wasTruncated)
                {
                    SaveStatusLabel = "200 KB limit reached";
                }

                return;
            }

            WordCountLabel = BuildWordCountLabel(normalized);

            if (_suppressEditorDirtyTracking)
            {
                return;
            }

            lock (_sync)
            {
                _isDirty = true;
                if (string.IsNullOrWhiteSpace(_activeNoteId))
                {
                    _activeNoteId = Guid.NewGuid().ToString("N");
                    _activeCreatedAtUtc = _utcNowProvider().ToUniversalTime();
                }
            }

            ActiveTitle = NoteEntry.DeriveTitle(null, normalized);
            OnPropertyChanged(nameof(ActiveNoteId));
            OnPropertyChanged(nameof(HasActiveNote));
            _copyFeedbackTimer.Cancel();
            _saveFeedbackTimer.Cancel();
            IsCopiedFeedbackActive = false;
            IsCopyError = false;
            IsSavedFeedbackActive = false;
            HasUnsavedChanges = true;
            SaveStatusLabel = wasTruncated ? "200 KB limit reached" : "Unsaved changes";
            _debounceTimer.Schedule(AutoSaveDebounceDelay, OnDebounceTimerElapsed);
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            var next = value ?? string.Empty;
            if (SetField(ref _searchQuery, next))
            {
                _ = ReloadFilteredNotesSafeAsync(next);
            }
        }
    }

    public string SaveStatusLabel
    {
        get => _saveStatusLabel;
        private set => SetField(ref _saveStatusLabel, value);
    }

    public bool IsCopiedFeedbackActive
    {
        get => _isCopiedFeedbackActive;
        private set
        {
            if (SetField(ref _isCopiedFeedbackActive, value))
            {
                OnPropertyChanged(nameof(CopyButtonLabel));
            }
        }
    }

    public string CopyButtonLabel => _isCopiedFeedbackActive ? "Copied" : "Copy";

    public bool IsCopyError
    {
        get => _isCopyError;
        private set => SetField(ref _isCopyError, value);
    }

    internal bool IsCopyFeedbackTimerPending => _copyFeedbackTimer.IsPending;

    public string WordCountLabel
    {
        get => _wordCountLabel;
        private set => SetField(ref _wordCountLabel, value);
    }

    public bool ShowWordCount
    {
        get
        {
            lock (_sync)
            {
                return _settings.ShowWordCount;
            }
        }
    }

    public bool IsInputFocused => _isInputFocused;

    public string AccessibleStatus =>
        HasActiveNote
            ? $"Notes: {ActiveTitle}, {SaveStatusLabel}"
            : "Notes: no notes created";

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SettingsChanged;

    public void AttachUiDispatcher(Action<Action> dispatch) =>
        _uiDispatcher = dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    public void OpenSettings() =>
        _context?.Events.Publish(new OpenSettingsRequestedEvent("Widgets:shima.notes"));

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var rawJson = await context.Settings.ReadJsonAsync(ct).ConfigureAwait(false);
        var loadedSettings = ParseSettingsOrDefault(rawJson);
        lock (_sync)
        {
            _settings = loadedSettings;
        }

        await RefreshFromRepositoryAsync(selectFirstIfMissing: true, ct).ConfigureAwait(false);
        DispatchToUi(() => OnPropertyChanged(nameof(ShowWordCount)));
    }

    public void NotifyInputFocusChanged(bool isFocused)
    {
        if (_isInputFocused == isFocused)
        {
            return;
        }

        _isInputFocused = isFocused;
        _context?.Events.Publish(new IslandInputFocusRequestedEvent(isFocused));
    }

    public async Task CreateNewNoteAsync(CancellationToken ct = default)
    {
        await FlushPendingSaveAsync(ct).ConfigureAwait(false);

        var now = _utcNowProvider().ToUniversalTime();
        var created = await _repository.UpsertAsync(
            new NoteEntry
            {
                NoteId = Guid.NewGuid().ToString("N"),
                Title = "Untitled note",
                ContentText = string.Empty,
                IsPinned = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            },
            ct).ConfigureAwait(false);

        lock (_sync)
        {
            _searchQuery = string.Empty;
        }

        DispatchToUi(() => OnPropertyChanged(nameof(SearchQuery)));
        LoadNoteIntoEditor(created);
        await RefreshFromRepositoryAsync(selectFirstIfMissing: false, ct).ConfigureAwait(false);
        DispatchToUi(() => SaveStatusLabel = "Saved");
    }

    public async Task SelectNoteAsync(string noteId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        if (string.Equals(ActiveNoteId, noteId, StringComparison.Ordinal))
        {
            return;
        }

        await FlushPendingSaveAsync(ct).ConfigureAwait(false);

        NoteEntry? target;
        lock (_sync)
        {
            target = _allNotes.FirstOrDefault(n => string.Equals(n.NoteId, noteId, StringComparison.Ordinal));
        }

        if (target is null)
        {
            await RefreshFromRepositoryAsync(selectFirstIfMissing: false, ct).ConfigureAwait(false);
            lock (_sync)
            {
                target = _allNotes.FirstOrDefault(n => string.Equals(n.NoteId, noteId, StringComparison.Ordinal));
            }
        }

        if (target is not null)
        {
            LoadNoteIntoEditor(target);
            SyncNoteSummaries();
        }
    }

    public Task SelectPreviousNoteAsync(CancellationToken ct = default) =>
        StepSelectedNoteAsync(-1, ct);

    public Task SelectNextNoteAsync(CancellationToken ct = default) =>
        StepSelectedNoteAsync(1, ct);

    public async Task StepSelectedNoteAsync(int delta, CancellationToken ct = default)
    {
        await FlushPendingSaveAsync(ct).ConfigureAwait(false);

        NoteEntry? target = null;
        lock (_sync)
        {
            if (_allNotes.Count == 0)
            {
                return;
            }

            var currentIndex = string.IsNullOrWhiteSpace(_activeNoteId)
                ? -1
                : _allNotes.FindIndex(n => string.Equals(n.NoteId, _activeNoteId, StringComparison.Ordinal));
            var nextIndex = currentIndex < 0
                ? 0
                : ((currentIndex + delta) % _allNotes.Count + _allNotes.Count) % _allNotes.Count;
            target = _allNotes[nextIndex];
        }

        LoadNoteIntoEditor(target);
        SyncNoteSummaries();
    }

    public async Task<bool> SaveActiveNoteAsync(CancellationToken ct = default)
    {
        _debounceTimer.Cancel();

        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? noteId;
            string content;
            bool isPinned;
            DateTimeOffset createdAt;

            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(_activeNoteId))
                {
                    return false;
                }

                noteId = _activeNoteId;
                content = _editorContent;
                isPinned = _activeIsPinned;
                createdAt = _activeCreatedAtUtc == default ? _utcNowProvider().ToUniversalTime() : _activeCreatedAtUtc;
                _isDirty = false;
            }

            var now = _utcNowProvider().ToUniversalTime();
            var saved = await _repository.UpsertAsync(
                new NoteEntry
                {
                    NoteId = noteId,
                    Title = NoteEntry.DeriveTitle(null, content),
                    ContentText = content,
                    IsPinned = isPinned,
                    CreatedAtUtc = createdAt,
                    UpdatedAtUtc = now
                },
                ct).ConfigureAwait(false);

            await RefreshFromRepositoryAsync(selectFirstIfMissing: false, ct).ConfigureAwait(false);
            if (!_disposed)
            {
                _saveFeedbackTimer.Schedule(SaveFeedbackDuration, ClearSaveFeedback);
            }

            DispatchToUi(() =>
            {
                ActiveTitle = saved.Title;
                HasUnsavedChanges = false;
                IsCopiedFeedbackActive = false;
                IsCopyError = false;
                IsSavedFeedbackActive = true;
                SaveStatusLabel = "Saved";
                OnPropertyChanged(nameof(AccessibleStatus));
            });

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context?.Logger.Error("Failed to save note to SQLite repository.", ex);
            DispatchToUi(() => SaveStatusLabel = "Save error");
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void ClearSaveFeedback()
    {
        _saveFeedbackTimer.Cancel();
        if (_disposed)
        {
            return;
        }

        DispatchToUi(() => IsSavedFeedbackActive = false);
    }

    public async Task DeleteNoteAsync(string noteId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);

        if (string.Equals(ActiveNoteId, noteId, StringComparison.Ordinal))
        {
            _debounceTimer.Cancel();
            lock (_sync)
            {
                _isDirty = false;
            }
        }
        else
        {
            await FlushPendingSaveAsync(ct).ConfigureAwait(false);
        }

        await _repository.DeleteAsync(noteId, ct).ConfigureAwait(false);
        if (string.Equals(ActiveNoteId, noteId, StringComparison.Ordinal))
        {
            ClearEditor();
        }

        await RefreshFromRepositoryAsync(selectFirstIfMissing: true, ct).ConfigureAwait(false);
    }

    public async Task TogglePinAsync(string noteId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        await FlushPendingSaveAsync(ct).ConfigureAwait(false);

        bool nextPinned;
        lock (_sync)
        {
            var current = _allNotes.FirstOrDefault(n => string.Equals(n.NoteId, noteId, StringComparison.Ordinal));
            if (current is null)
            {
                return;
            }

            nextPinned = !current.IsPinned;
        }

        await _repository.SetPinnedAsync(noteId, nextPinned, ct).ConfigureAwait(false);
        if (string.Equals(ActiveNoteId, noteId, StringComparison.Ordinal))
        {
            DispatchToUi(() => ActiveIsPinned = nextPinned);
        }

        await RefreshFromRepositoryAsync(selectFirstIfMissing: false, ct).ConfigureAwait(false);
    }

    public async Task<bool> CopyActiveNoteAsync(CancellationToken ct = default)
    {
        await FlushPendingSaveAsync(ct).ConfigureAwait(false);
        var content = EditorContent;
        if (string.IsNullOrEmpty(content))
        {
            ApplyCopyFailureFeedback("Nothing to copy");
            return false;
        }

        bool copied;
        try
        {
            if (_copyTextOverride is not null)
            {
                copied = await _copyTextOverride(content, ct).ConfigureAwait(false);
            }
            else if (_clipboardAdapter is not null)
            {
                copied = await _clipboardAdapter.TrySetTextAsync(content, ct).ConfigureAwait(false);
            }
            else
            {
                System.Windows.Clipboard.SetText(content);
                copied = true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _context?.Logger.Warn("Failed to copy note contents to clipboard.", ex);
            copied = false;
        }

        if (copied)
        {
            ApplyCopySuccessFeedback();
        }
        else
        {
            ApplyCopyFailureFeedback("Copy failed");
        }

        return copied;
    }

    public void ClearCopyFeedback()
    {
        _copyFeedbackTimer.Cancel();
        if (_disposed)
        {
            return;
        }

        bool isDirty;
        lock (_sync)
        {
            isDirty = _isDirty;
        }

        DispatchToUi(() =>
        {
            IsCopiedFeedbackActive = false;
            IsCopyError = false;
            if (SaveStatusLabel is "Copied" or "Copy failed" or "Nothing to copy")
            {
                SaveStatusLabel = isDirty ? "Unsaved changes" : "Saved";
            }
        });
    }

    public async Task SetSearchQueryAsync(string? query, CancellationToken ct = default)
    {
        var next = query ?? string.Empty;
        DispatchToUi(() => SetField(ref _searchQuery, next, nameof(SearchQuery)));
        var results = string.IsNullOrWhiteSpace(next)
            ? await _repository.GetAllAsync(ct).ConfigureAwait(false)
            : await _repository.SearchAsync(next, ct).ConfigureAwait(false);

        lock (_sync)
        {
            _allNotes = results.ToList();
        }

        SyncNoteSummaries();
    }

    public async Task FlushPendingSaveAsync(CancellationToken ct = default)
    {
        _debounceTimer.Cancel();

        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? noteId;
            string content;
            bool isPinned;
            DateTimeOffset createdAt;

            lock (_sync)
            {
                if (!_isDirty || string.IsNullOrWhiteSpace(_activeNoteId))
                {
                    return;
                }

                noteId = _activeNoteId;
                content = _editorContent;
                isPinned = _activeIsPinned;
                createdAt = _activeCreatedAtUtc == default ? _utcNowProvider().ToUniversalTime() : _activeCreatedAtUtc;
                _isDirty = false;
            }

            var now = _utcNowProvider().ToUniversalTime();
            var saved = await _repository.UpsertAsync(
                new NoteEntry
                {
                    NoteId = noteId,
                    Title = NoteEntry.DeriveTitle(null, content),
                    ContentText = content,
                    IsPinned = isPinned,
                    CreatedAtUtc = createdAt,
                    UpdatedAtUtc = now
                },
                ct).ConfigureAwait(false);

            await RefreshFromRepositoryAsync(selectFirstIfMissing: false, ct).ConfigureAwait(false);
            DispatchToUi(() =>
            {
                ActiveTitle = saved.Title;
                HasUnsavedChanges = false;
                IsCopiedFeedbackActive = false;
                IsCopyError = false;
                SaveStatusLabel = "Saved";
                OnPropertyChanged(nameof(AccessibleStatus));
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context?.Logger.Error("Failed to auto-save note to SQLite repository.", ex);
            DispatchToUi(() => SaveStatusLabel = "Save error");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task SetShowWordCountAsync(bool show, CancellationToken ct = default)
    {
        var updated = _settings with { ShowWordCount = show };
        lock (_sync)
        {
            _settings = updated;
        }

        if (_context is not null)
        {
            await _context.Settings.WriteJsonAsync(JsonSerializer.Serialize(updated, JsonOptions), ct)
                .ConfigureAwait(false);
        }

        DispatchToUi(() => OnPropertyChanged(nameof(ShowWordCount)));
        DispatchToUi(() => SettingsChanged?.Invoke(this, EventArgs.Empty));
    }

    public async Task ResetToDefaultsAsync(CancellationToken ct = default)
    {
        var defaults = new NotesSettings();
        lock (_sync)
        {
            _settings = defaults;
        }

        if (_context is not null)
        {
            await _context.Settings.WriteJsonAsync(JsonSerializer.Serialize(defaults, JsonOptions), ct)
                .ConfigureAwait(false);
        }

        DispatchToUi(() => OnPropertyChanged(nameof(ShowWordCount)));
        DispatchToUi(() => SettingsChanged?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _copyFeedbackTimer.Cancel();
        if (_ownsCopyFeedbackTimer)
        {
            _copyFeedbackTimer.Dispose();
        }

        _saveFeedbackTimer.Cancel();
        if (_ownsSaveFeedbackTimer)
        {
            _saveFeedbackTimer.Dispose();
        }

        NotifyInputFocusChanged(false);
        _debounceTimer.Dispose();
        _saveGate.Dispose();
    }

    private async void OnDebounceTimerElapsed()
    {
        if (_disposed)
        {
            return;
        }

        await FlushPendingSaveAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ReloadFilteredNotesSafeAsync(string query)
    {
        try
        {
            var items = string.IsNullOrWhiteSpace(query)
                ? await _repository.GetAllAsync(CancellationToken.None).ConfigureAwait(false)
                : await _repository.SearchAsync(query, CancellationToken.None).ConfigureAwait(false);

            lock (_sync)
            {
                _allNotes = items.ToList();
            }

            SyncNoteSummaries();
        }
        catch (Exception ex)
        {
            _context?.Logger.Warn("Failed while searching local notes.", ex);
        }
    }

    private async Task RefreshFromRepositoryAsync(bool selectFirstIfMissing, CancellationToken ct)
    {
        string query;
        lock (_sync)
        {
            query = _searchQuery;
        }

        var items = string.IsNullOrWhiteSpace(query)
            ? await _repository.GetAllAsync(ct).ConfigureAwait(false)
            : await _repository.SearchAsync(query, ct).ConfigureAwait(false);

        NoteEntry? selected = null;
        bool shouldLoadSelected = false;
        lock (_sync)
        {
            _allNotes = items.ToList();
            if (!string.IsNullOrWhiteSpace(_activeNoteId))
            {
                selected = _allNotes.FirstOrDefault(n => string.Equals(n.NoteId, _activeNoteId, StringComparison.Ordinal));
            }

            if (selected is null && selectFirstIfMissing && _allNotes.Count > 0)
            {
                selected = _allNotes[0];
                shouldLoadSelected = true;
            }
        }

        if (selected is not null && shouldLoadSelected)
        {
            LoadNoteIntoEditor(selected);
        }

        SyncNoteSummaries();
    }

    private void LoadNoteIntoEditor(NoteEntry note)
    {
        lock (_sync)
        {
            _activeNoteId = note.NoteId;
            _activeCreatedAtUtc = note.CreatedAtUtc;
            _isDirty = false;
        }

        _copyFeedbackTimer.Cancel();
        _saveFeedbackTimer.Cancel();

        DispatchToUi(() =>
        {
            _suppressEditorDirtyTracking = true;
            try
            {
                IsCopiedFeedbackActive = false;
                IsCopyError = false;
                IsSavedFeedbackActive = false;
                HasUnsavedChanges = false;
                ActiveIsPinned = note.IsPinned;
                ActiveTitle = note.Title;
                EditorContent = note.ContentText;
                SaveStatusLabel = "Saved";
                WordCountLabel = BuildWordCountLabel(note.ContentText);
                OnPropertyChanged(nameof(ActiveNoteId));
                OnPropertyChanged(nameof(HasActiveNote));
                OnPropertyChanged(nameof(PinActiveActionLabel));
                OnPropertyChanged(nameof(PinButtonLabel));
                OnPropertyChanged(nameof(SaveActiveActionLabel));
                OnPropertyChanged(nameof(AccessibleStatus));
            }
            finally
            {
                _suppressEditorDirtyTracking = false;
            }
        });
    }

    private void ClearEditor()
    {
        lock (_sync)
        {
            _activeNoteId = null;
            _activeCreatedAtUtc = default;
            _isDirty = false;
        }

        _copyFeedbackTimer.Cancel();
        _saveFeedbackTimer.Cancel();

        DispatchToUi(() =>
        {
            _suppressEditorDirtyTracking = true;
            try
            {
                IsCopiedFeedbackActive = false;
                IsCopyError = false;
                IsSavedFeedbackActive = false;
                HasUnsavedChanges = false;
                ActiveIsPinned = false;
                ActiveTitle = "Quick notes";
                EditorContent = string.Empty;
                SaveStatusLabel = "Saved";
                WordCountLabel = "0 words";
                OnPropertyChanged(nameof(ActiveNoteId));
                OnPropertyChanged(nameof(HasActiveNote));
                OnPropertyChanged(nameof(PinActiveActionLabel));
                OnPropertyChanged(nameof(PinButtonLabel));
                OnPropertyChanged(nameof(SaveActiveActionLabel));
                OnPropertyChanged(nameof(AccessibleStatus));
            }
            finally
            {
                _suppressEditorDirtyTracking = false;
            }
        });
    }

    private void ApplyCopySuccessFeedback()
    {
        if (!_disposed)
        {
            _copyFeedbackTimer.Schedule(CopyFeedbackDuration, ClearCopyFeedback);
        }

        DispatchToUi(() =>
        {
            IsCopyError = false;
            IsCopiedFeedbackActive = true;
            SaveStatusLabel = "Copied";
        });
    }

    private void ApplyCopyFailureFeedback(string errorMessage)
    {
        if (!_disposed)
        {
            _copyFeedbackTimer.Schedule(CopyFeedbackDuration, ClearCopyFeedback);
        }

        DispatchToUi(() =>
        {
            IsCopiedFeedbackActive = false;
            IsCopyError = true;
            SaveStatusLabel = errorMessage;
        });
    }

    private void SyncNoteSummaries()
    {
        NoteEntry[] snapshot;
        string? activeId;
        lock (_sync)
        {
            snapshot = _allNotes.ToArray();
            activeId = _activeNoteId;
        }

        var selectedIndex = string.IsNullOrWhiteSpace(activeId)
            ? -1
            : Array.FindIndex(snapshot, n => string.Equals(n.NoteId, activeId, StringComparison.Ordinal));

        DispatchToUi(() =>
        {
            Notes.Clear();
            foreach (var note in snapshot)
            {
                var snippet = BuildSnippet(note.ContentText);
                var updated = note.UpdatedAtUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
                Notes.Add(new NoteSummaryViewModel(
                    note.NoteId,
                    note.Title,
                    snippet,
                    note.IsPinned,
                    string.Equals(note.NoteId, activeId, StringComparison.Ordinal),
                    updated,
                    $"Note {note.Title}, {(note.IsPinned ? "pinned" : "unpinned")}",
                    $"Open note {note.Title}",
                    note.IsPinned ? $"Unpin note {note.Title}" : $"Pin note {note.Title}"));
            }

            var count = Notes.Count;
            HasNotes = count > 0;
            CanNavigateNotes = count > 1;
            NoteCountLabel = count == 1 ? "1 note" : $"{count} notes";
            SelectedPositionLabel = count == 0
                ? "0/0"
                : $"{(selectedIndex >= 0 ? selectedIndex + 1 : 1)}/{count}";
        });
    }

    internal static (string Value, bool Truncated) ClampToMaxBytes(string input)
    {
        if (input.Length <= NoteEntry.MaxContentLength
            && Encoding.UTF8.GetByteCount(input) <= NoteEntry.MaxContentLength)
        {
            return (input, false);
        }

        var low = 0;
        var high = Math.Min(input.Length, NoteEntry.MaxContentLength);
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Encoding.UTF8.GetByteCount(input.AsSpan(0, mid)) <= NoteEntry.MaxContentLength)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (low > 0 && char.IsHighSurrogate(input[low - 1]))
        {
            low--;
        }

        return (input[..low], true);
    }

    private static string BuildSnippet(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "Empty note";
        }

        var singleLine = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length > 60 ? singleLine[..60] + "..." : singleLine;
    }

    private static string BuildWordCountLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "0 words";
        }

        var words = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                words++;
            }
        }

        return words == 1 ? $"1 word · {text.Length} chars" : $"{words} words · {text.Length} chars";
    }

    private static NotesSettings ParseSettingsOrDefault(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || string.Equals(rawJson.Trim(), "{}", StringComparison.Ordinal))
        {
            return new NotesSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<NotesSettings>(rawJson, JsonOptions) ?? new NotesSettings();
        }
        catch (JsonException)
        {
            return new NotesSettings();
        }
    }

    private void DispatchToUi(Action action)
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher is not null)
        {
            dispatcher(action);
        }
        else
        {
            action();
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
