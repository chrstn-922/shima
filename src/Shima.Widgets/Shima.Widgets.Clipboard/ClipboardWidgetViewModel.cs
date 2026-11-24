using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Shima.Core.Contracts;
using Shima.Core.Shell;

namespace Shima.Widgets.Clipboard;

public sealed class ClipboardEntryRowViewModel : INotifyPropertyChanged
{
    private bool _isCopiedFeedbackActive;

    public ClipboardEntryRowViewModel(
        string entryId,
        ClipboardContentType contentType,
        string previewText,
        string metaLabel,
        bool isPinned,
        bool isImage,
        bool hasThumbnail,
        BitmapSource? thumbnailBitmap,
        string pinActionLabel,
        string recopyActionLabel,
        string deleteActionLabel,
        string rowAccessibleName,
        bool isCopiedFeedbackActive = false)
    {
        EntryId = entryId;
        ContentType = contentType;
        PreviewText = previewText;
        MetaLabel = metaLabel;
        IsPinned = isPinned;
        IsImage = isImage;
        HasThumbnail = hasThumbnail;
        ThumbnailBitmap = thumbnailBitmap;
        PinActionLabel = pinActionLabel;
        RecopyActionLabel = recopyActionLabel;
        DeleteActionLabel = deleteActionLabel;
        RowAccessibleName = rowAccessibleName;
        _isCopiedFeedbackActive = isCopiedFeedbackActive;
    }

    public string EntryId { get; }

    public ClipboardContentType ContentType { get; }

    public string PreviewText { get; }

    public string MetaLabel { get; }

    public bool IsPinned { get; }

    public string PinButtonLabel => IsPinned ? "Unpin" : "Pin";

    public string PinStateLabel => IsPinned ? "Pinned" : "Not pinned";

    public bool IsImage { get; }

    public bool HasThumbnail { get; }

    public BitmapSource? ThumbnailBitmap { get; }

    public string PinActionLabel { get; }

    public string RecopyActionLabel { get; }

    public string DeleteActionLabel { get; }

    public string RowAccessibleName { get; }

    public bool IsCopiedFeedbackActive
    {
        get => _isCopiedFeedbackActive;
        internal set
        {
            if (_isCopiedFeedbackActive != value)
            {
                _isCopiedFeedbackActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CopyButtonLabel));
            }
        }
    }

    public string CopyButtonLabel => _isCopiedFeedbackActive ? "Copied" : "Copy";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Observable view-model backing event-driven local clipboard history, thumbnail loading,
/// recopy loop suppression, search, pinning, pause/resume, and unpinned clear.
/// </summary>
public sealed class ClipboardWidgetViewModel : INotifyPropertyChanged, IDisposable
{
    public static readonly TimeSpan CopyFeedbackDuration = TimeSpan.FromMilliseconds(1500);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SecondarySelfCopyWindow = TimeSpan.FromSeconds(2);

    private readonly IClipboardMonitorAdapter _clipboardAdapter;
    private readonly IClipboardRepository _repository;
    private readonly IAppPreferencesService? _preferencesService;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly IOneShotTimer _copyFeedbackTimer;
    private readonly bool _ownsCopyFeedbackTimer;
    private readonly SemaphoreSlim _workGate = new(1, 1);
    private readonly object _sync = new();

    private IWidgetContext? _context;
    private Action<Action>? _uiDispatcher;
    private ClipboardSettings _settings = new();
    private List<ClipboardEntry> _loadedEntries = [];
    private Task _pendingCaptureTask = Task.CompletedTask;

    private string _searchQuery = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _hasEntries;
    private bool _isCopySuccessFeedbackActive;
    private bool _isStatusError;
    private string? _copiedEntryId;
    private bool _isInputFocused;
    private string? _suppressedRecopyHash;
    private DateTimeOffset _suppressedRecopyUntilUtc;
    private bool _disposed;

    public ClipboardWidgetViewModel(
        IClipboardMonitorAdapter clipboardAdapter,
        IClipboardRepository repository,
        IAppPreferencesService? preferencesService = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        IOneShotTimer? copyFeedbackTimer = null)
    {
        _clipboardAdapter = clipboardAdapter ?? throw new ArgumentNullException(nameof(clipboardAdapter));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _preferencesService = preferencesService;
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _ownsCopyFeedbackTimer = copyFeedbackTimer is null;
        _copyFeedbackTimer = copyFeedbackTimer ?? new OneShotTimer();
    }

    public ObservableCollection<ClipboardEntryRowViewModel> Entries { get; } = [];

    public int MaxUnpinnedItems => Math.Clamp(_preferencesService?.Current.ClipboardMaxItems ?? 100, 20, 1000);

    public int RetentionDays => Math.Clamp(_preferencesService?.Current.ClipboardRetentionDays ?? 30, 1, 365);

    public bool CaptureImages
    {
        get
        {
            lock (_sync)
            {
                return _settings.CaptureImages;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _settings.IsPaused;
            }
        }
    }

    public string PauseToggleLabel => IsPaused ? "Resume" : "Pause";

    public string PauseActionLabel => IsPaused ? "Resume clipboard collection" : "Pause clipboard collection";

    public bool HasEntries
    {
        get => _hasEntries;
        private set
        {
            if (SetField(ref _hasEntries, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(EmptyStateMessage));
                OnPropertyChanged(nameof(AccessibleStatus));
            }
        }
    }

    public bool ShowEmptyState => !_hasEntries;

    public string EmptyStateMessage => IsPaused
        ? "Clipboard collection is paused"
        : "No clipboard items captured yet";

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            var next = value ?? string.Empty;
            if (SetField(ref _searchQuery, next))
            {
                EnqueueWork(() => RefreshHistoryCoreAsync(CancellationToken.None));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetField(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(_statusMessage);

    public bool IsCopySuccessFeedbackActive
    {
        get => _isCopySuccessFeedbackActive;
        private set => SetField(ref _isCopySuccessFeedbackActive, value);
    }

    public bool IsStatusError
    {
        get => _isStatusError;
        private set => SetField(ref _isStatusError, value);
    }

    internal bool IsCopyFeedbackTimerPending => _copyFeedbackTimer.IsPending;

    public bool IsInputFocused => _isInputFocused;

    public string AccessibleStatus => IsPaused
        ? $"Clipboard: paused, {Entries.Count} saved items"
        : $"Clipboard: monitoring, {Entries.Count} saved items";

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SettingsChanged;

    public void AttachUiDispatcher(Action<Action> dispatch) =>
        _uiDispatcher = dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    public void OpenSettings() =>
        _context?.Events.Publish(new OpenSettingsRequestedEvent("Clipboard"));

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var rawJson = await context.Settings.ReadJsonAsync(ct).ConfigureAwait(false);
        var loaded = ParseSettingsOrDefault(rawJson);

        lock (_sync)
        {
            _settings = loaded;
        }

        _clipboardAdapter.ClipboardChanged -= OnAdapterClipboardChanged;
        _clipboardAdapter.ClipboardChanged += OnAdapterClipboardChanged;

        if (_preferencesService is not null)
        {
            _preferencesService.PreferencesChanged -= OnPreferencesChanged;
            _preferencesService.PreferencesChanged += OnPreferencesChanged;
        }

        await _repository.PruneAsync(MaxUnpinnedItems, RetentionDays, _utcNowProvider(), ct).ConfigureAwait(false);

        if (!loaded.IsPaused)
        {
            _clipboardAdapter.Start();
        }
        else
        {
            _clipboardAdapter.Stop();
        }

        await RefreshHistoryCoreAsync(ct).ConfigureAwait(false);
        RaiseSettingsProperties();
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

    public async Task<bool> RecopyEntryAsync(string entryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        ClipboardEntry? entry;
        lock (_sync)
        {
            entry = _loadedEntries.FirstOrDefault(e => string.Equals(e.EntryId, entryId, StringComparison.Ordinal));
        }

        if (entry is null)
        {
            ApplyRecopyFailureFeedback();
            return false;
        }

        ArmSecondaryRecopySuppression(entry.ContentHash);

        bool succeeded;
        try
        {
            if (entry.ContentType == ClipboardContentType.Text)
            {
                succeeded = await _clipboardAdapter.TrySetTextAsync(entry.TextContent ?? string.Empty, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var fullPath = _repository.ResolveImageFullPath(entry.ImageRelPath);
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
                {
                    succeeded = false;
                }
                else
                {
                    var pngBytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
                    var dibFallback = TryDecodePngToBgraWithWpf(pngBytes);
                    succeeded = await _clipboardAdapter.TrySetImagePngAsync(pngBytes, dibFallback, ct)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            ClearSecondaryRecopySuppressionIfMatches(entry.ContentHash);
            throw;
        }
        catch (Exception ex)
        {
            _context?.Logger.Warn("Failed to restore clipboard entry.", ex);
            succeeded = false;
        }

        if (succeeded)
        {
            ApplyRecopySuccessFeedback(entry.EntryId);
        }
        else
        {
            ClearSecondaryRecopySuppressionIfMatches(entry.ContentHash);
            ApplyRecopyFailureFeedback();
        }

        return succeeded;
    }

    public void ClearCopyFeedback()
    {
        _copyFeedbackTimer.Cancel();
        lock (_sync)
        {
            _copiedEntryId = null;
        }

        if (_disposed)
        {
            return;
        }

        DispatchToUi(() =>
        {
            foreach (var row in Entries)
            {
                row.IsCopiedFeedbackActive = false;
            }

            IsCopySuccessFeedbackActive = false;
            IsStatusError = false;
            if (StatusMessage is "Copied" or "Copy failed")
            {
                StatusMessage = string.Empty;
            }
        });
    }

    public async Task TogglePinAsync(string entryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        ClipboardEntry? entry;
        lock (_sync)
        {
            entry = _loadedEntries.FirstOrDefault(e => string.Equals(e.EntryId, entryId, StringComparison.Ordinal));
        }

        if (entry is null)
        {
            return;
        }

        await _repository.SetPinnedAsync(entryId, !entry.IsPinned, ct).ConfigureAwait(false);
        await RefreshHistoryCoreAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteEntryAsync(string entryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        await _repository.DeleteAsync(entryId, ct).ConfigureAwait(false);
        await RefreshHistoryCoreAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearUnpinnedAsync(CancellationToken ct = default)
    {
        _copyFeedbackTimer.Cancel();
        lock (_sync)
        {
            _copiedEntryId = null;
        }

        await _repository.ClearUnpinnedAsync(ct).ConfigureAwait(false);
        await RefreshHistoryCoreAsync(ct).ConfigureAwait(false);
        DispatchToUi(() =>
        {
            foreach (var row in Entries)
            {
                row.IsCopiedFeedbackActive = false;
            }

            IsCopySuccessFeedbackActive = false;
            IsStatusError = false;
            StatusMessage = "Cleared unpinned items";
        });
    }

    public async Task SetPausedAsync(bool isPaused, CancellationToken ct = default)
    {
        _copyFeedbackTimer.Cancel();
        lock (_sync)
        {
            _copiedEntryId = null;
        }

        await UpdateSettingsAsync(s => s with { IsPaused = isPaused }, ct).ConfigureAwait(false);
        if (isPaused)
        {
            _clipboardAdapter.Stop();
        }
        else
        {
            _clipboardAdapter.Start();
        }

        RaiseSettingsProperties();
        DispatchToUi(() =>
        {
            foreach (var row in Entries)
            {
                row.IsCopiedFeedbackActive = false;
            }

            IsCopySuccessFeedbackActive = false;
            IsStatusError = false;
            StatusMessage = isPaused ? "Collection paused" : "Collection active";
            OnPropertyChanged(nameof(EmptyStateMessage));
        });
    }

    public async Task SetCaptureImagesAsync(bool captureImages, CancellationToken ct = default)
    {
        await UpdateSettingsAsync(s => s with { CaptureImages = captureImages }, ct).ConfigureAwait(false);
        RaiseSettingsProperties();
    }

    public async Task SetSearchQueryAsync(string? query, CancellationToken ct = default)
    {
        var next = query ?? string.Empty;
        DispatchToUi(() => SetField(ref _searchQuery, next, nameof(SearchQuery)));
        await RefreshHistoryCoreAsync(ct).ConfigureAwait(false);
    }

    public async Task ResetToDefaultsAsync(CancellationToken ct = default)
    {
        var defaults = new ClipboardSettings();
        lock (_sync)
        {
            _settings = defaults;
        }

        if (_context is not null)
        {
            await _context.Settings.WriteJsonAsync(JsonSerializer.Serialize(defaults, JsonOptions), ct)
                .ConfigureAwait(false);
        }

        if (!defaults.IsPaused)
        {
            _clipboardAdapter.Start();
        }

        RaiseSettingsProperties();
        DispatchToUi(() => SettingsChanged?.Invoke(this, EventArgs.Empty));
    }

    internal async Task WaitForPendingWorkAsync()
    {
        Task pending;
        lock (_sync)
        {
            pending = _pendingCaptureTask;
        }

        await pending.ConfigureAwait(false);
    }

    internal async Task HandleClipboardCaptureAsync(ClipboardCaptureEventArgs e, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_disposed || IsPaused)
        {
            return;
        }

        if (e.ContentType == ClipboardContentType.Image && !CaptureImages)
        {
            return;
        }

        if (TryConsumeSecondaryRecopySuppression(e.ContentHash))
        {
            return;
        }

        await _workGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || IsPaused)
            {
                return;
            }

            var entryId = Guid.NewGuid().ToString("N");
            string? imageRelPath = null;
            if (e.ContentType == ClipboardContentType.Image && e.ImagePngBytes is { Length: > 0 })
            {
                imageRelPath = await _repository.SaveImageAssetAsync(entryId, e.ImagePngBytes, ct).ConfigureAwait(false);
            }

            var copiedAt = e.CapturedAtUtc == default ? _utcNowProvider().ToUniversalTime() : e.CapturedAtUtc.ToUniversalTime();
            var entry = new ClipboardEntry
            {
                EntryId = entryId,
                ContentType = e.ContentType,
                TextContent = e.ContentType == ClipboardContentType.Text ? e.TextContent : null,
                ImageRelPath = imageRelPath,
                ContentHash = e.ContentHash,
                SourceAppName = e.SourceAppName,
                ByteSize = e.ByteSize,
                IsPinned = false,
                CopiedAtUtc = copiedAt
            };

            var addResult = await _repository
                .TryAddEntryAsync(entry, MaxUnpinnedItems, RetentionDays, forcePrune: false, ct)
                .ConfigureAwait(false);

            if (addResult.Deduplicated && !string.IsNullOrEmpty(imageRelPath))
            {
                var orphanPath = _repository.ResolveImageFullPath(imageRelPath);
                if (!string.IsNullOrEmpty(orphanPath) && File.Exists(orphanPath))
                {
                    try
                    {
                        File.Delete(orphanPath);
                    }
                    catch
                    {
                    }
                }
            }

            await RefreshHistoryCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context?.Logger.Warn("Failed while recording clipboard history item.", ex);
        }
        finally
        {
            _workGate.Release();
        }
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

        NotifyInputFocusChanged(false);
        _clipboardAdapter.ClipboardChanged -= OnAdapterClipboardChanged;
        if (_preferencesService is not null)
        {
            _preferencesService.PreferencesChanged -= OnPreferencesChanged;
        }

        _workGate.Dispose();
    }

    private void OnAdapterClipboardChanged(object? sender, ClipboardCaptureEventArgs e)
    {
        if (_disposed || IsPaused)
        {
            return;
        }

        EnqueueWork(() => HandleClipboardCaptureAsync(e, CancellationToken.None));
    }

    private void OnPreferencesChanged(object? sender, AppPreferencesSnapshot e)
    {
        if (_disposed)
        {
            return;
        }

        EnqueueWork(async () =>
        {
            await _repository.PruneAsync(MaxUnpinnedItems, RetentionDays, _utcNowProvider(), CancellationToken.None)
                .ConfigureAwait(false);
            await RefreshHistoryCoreAsync(CancellationToken.None).ConfigureAwait(false);
        });
    }

    private void EnqueueWork(Func<Task> work)
    {
        lock (_sync)
        {
            _pendingCaptureTask = _pendingCaptureTask.ContinueWith(
                async _ =>
                {
                    if (!_disposed)
                    {
                        await work().ConfigureAwait(false);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task RefreshHistoryCoreAsync(CancellationToken ct)
    {
        var query = SearchQuery;
        var items = await _repository.GetHistoryAsync(query, MaxUnpinnedItems + 50, ct).ConfigureAwait(false);

        lock (_sync)
        {
            _loadedEntries = items.ToList();
        }

        var rows = items.Select(BuildRowViewModel).ToArray();
        DispatchToUi(() =>
        {
            Entries.Clear();
            foreach (var row in rows)
            {
                Entries.Add(row);
            }

            HasEntries = Entries.Count > 0;
            OnPropertyChanged(nameof(AccessibleStatus));
        });
    }

    private ClipboardEntryRowViewModel BuildRowViewModel(ClipboardEntry entry)
    {
        var isImage = entry.ContentType == ClipboardContentType.Image;
        var preview = isImage
            ? $"Image · {FormatByteSize(entry.ByteSize)}"
            : NormalizePreviewText(entry.TextContent);

        var source = string.IsNullOrWhiteSpace(entry.SourceAppName) ? "Clipboard" : entry.SourceAppName;
        var time = entry.CopiedAtUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        var meta = $"{source} · {time}";
        var thumbnail = isImage ? TryLoadThumbnailBitmap(entry.ImageRelPath) : null;
        bool isCopied;
        lock (_sync)
        {
            isCopied = string.Equals(_copiedEntryId, entry.EntryId, StringComparison.Ordinal);
        }

        var rowAccessibleName = entry.IsPinned
            ? (isImage ? $"Pinned image · {FormatByteSize(entry.ByteSize)}" : $"Pinned clipboard item: {preview}")
            : (isImage ? preview : $"Clipboard item: {preview}");

        return new ClipboardEntryRowViewModel(
            entry.EntryId,
            entry.ContentType,
            preview,
            meta,
            entry.IsPinned,
            isImage,
            thumbnail is not null,
            thumbnail,
            entry.IsPinned ? "Unpin clipboard item" : "Pin clipboard item",
            isImage ? "Copy image back to clipboard" : $"Copy text back to clipboard: {preview}",
            "Delete clipboard item",
            rowAccessibleName,
            isCopied);
    }

    private static RawBgraImage? TryDecodePngToBgraWithWpf(byte[] pngBytes)
    {
        if (pngBytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(pngBytes, writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != System.Windows.Media.PixelFormats.Bgra32)
            {
                frame = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            }

            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var stride = width * 4;
            var pixels = new byte[stride * height];
            frame.CopyPixels(pixels, stride, 0);
            return new RawBgraImage(width, height, pixels);
        }
        catch
        {
            return null;
        }
    }

    private void ApplyRecopySuccessFeedback(string entryId)
    {
        lock (_sync)
        {
            _copiedEntryId = entryId;
        }

        if (!_disposed)
        {
            _copyFeedbackTimer.Schedule(CopyFeedbackDuration, ClearCopyFeedback);
        }

        DispatchToUi(() =>
        {
            foreach (var row in Entries)
            {
                row.IsCopiedFeedbackActive = string.Equals(row.EntryId, entryId, StringComparison.Ordinal);
            }

            IsStatusError = false;
            IsCopySuccessFeedbackActive = true;
            StatusMessage = "Copied";
        });
    }

    private void ApplyRecopyFailureFeedback()
    {
        lock (_sync)
        {
            _copiedEntryId = null;
        }

        if (!_disposed)
        {
            _copyFeedbackTimer.Schedule(CopyFeedbackDuration, ClearCopyFeedback);
        }

        DispatchToUi(() =>
        {
            foreach (var row in Entries)
            {
                row.IsCopiedFeedbackActive = false;
            }

            IsCopySuccessFeedbackActive = false;
            IsStatusError = true;
            StatusMessage = "Copy failed";
        });
    }

    private BitmapSource? TryLoadThumbnailBitmap(string? imageRelPath)
    {
        var fullPath = _repository.ResolveImageFullPath(imageRelPath);
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            if (bytes.Length == 0)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 72;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void ArmSecondaryRecopySuppression(string contentHash)
    {
        lock (_sync)
        {
            _suppressedRecopyHash = contentHash;
            _suppressedRecopyUntilUtc = _utcNowProvider().ToUniversalTime().Add(SecondarySelfCopyWindow);
        }
    }

    private void ClearSecondaryRecopySuppressionIfMatches(string contentHash)
    {
        lock (_sync)
        {
            if (string.Equals(_suppressedRecopyHash, contentHash, StringComparison.OrdinalIgnoreCase))
            {
                _suppressedRecopyHash = null;
                _suppressedRecopyUntilUtc = default;
            }
        }
    }

    private bool TryConsumeSecondaryRecopySuppression(string contentHash)
    {
        lock (_sync)
        {
            if (string.IsNullOrEmpty(_suppressedRecopyHash))
            {
                return false;
            }

            var now = _utcNowProvider().ToUniversalTime();
            var match = now <= _suppressedRecopyUntilUtc
                && string.Equals(_suppressedRecopyHash, contentHash, StringComparison.OrdinalIgnoreCase);
            if (match || now > _suppressedRecopyUntilUtc)
            {
                _suppressedRecopyHash = null;
                _suppressedRecopyUntilUtc = default;
            }

            return match;
        }
    }

    private async Task UpdateSettingsAsync(
        Func<ClipboardSettings, ClipboardSettings> transform,
        CancellationToken ct)
    {
        ClipboardSettings updated;
        lock (_sync)
        {
            updated = transform(_settings);
            _settings = updated;
        }

        if (_context is not null)
        {
            await _context.Settings.WriteJsonAsync(JsonSerializer.Serialize(updated, JsonOptions), ct)
                .ConfigureAwait(false);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseSettingsProperties()
    {
        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(CaptureImages));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PauseToggleLabel));
            OnPropertyChanged(nameof(PauseActionLabel));
            OnPropertyChanged(nameof(EmptyStateMessage));
            OnPropertyChanged(nameof(AccessibleStatus));
        });
    }

    private static string NormalizePreviewText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "(Whitespace text)";
        }

        var singleLine = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length > 140 ? singleLine[..140] + "..." : singleLine;
    }

    private static string FormatByteSize(int bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):0.##} MB";
    }

    private static ClipboardSettings ParseSettingsOrDefault(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || string.Equals(rawJson.Trim(), "{}", StringComparison.Ordinal))
        {
            return new ClipboardSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<ClipboardSettings>(rawJson, JsonOptions) ?? new ClipboardSettings();
        }
        catch (JsonException)
        {
            return new ClipboardSettings();
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
