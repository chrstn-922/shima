namespace Shima.Core.Contracts;

public enum ClipboardContentType
{
    Text,
    Image
}

/// <summary>
/// Persisted local clipboard item stored in SQLite clipboard_entries per data-model.md.
/// </summary>
public sealed record ClipboardEntry
{
    public const int MaxTextLength = 1_048_576;
    public const int MaxImageBytes = 10 * 1024 * 1024;
    public const int MaxSourceAppNameLength = 128;

    public required string EntryId { get; init; }

    public ClipboardContentType ContentType { get; init; }

    public string? TextContent { get; init; }

    public string? ImageRelPath { get; init; }

    public required string ContentHash { get; init; }

    public string? SourceAppName { get; init; }

    public int ByteSize { get; init; }

    public bool IsPinned { get; init; }

    public DateTimeOffset CopiedAtUtc { get; init; }
}

public sealed class ClipboardCaptureEventArgs : EventArgs
{
    public ClipboardContentType ContentType { get; init; }

    public string? TextContent { get; init; }

    public byte[]? ImagePngBytes { get; init; }

    public RawBgraImage? ImageBgra { get; init; }

    public required string ContentHash { get; init; }

    public string? SourceAppName { get; init; }

    public int ByteSize { get; init; }

    public DateTimeOffset CapturedAtUtc { get; init; }
}

public sealed record ClipboardAddResult(
    bool Inserted,
    ClipboardEntry? Entry,
    bool Deduplicated,
    int PrunedCount);

/// <summary>
/// Local SQLite repository managing clipboard entries, hash deduplication, retention pruning, and cached PNG images.
/// </summary>
public interface IClipboardRepository
{
    Task<IReadOnlyList<ClipboardEntry>> GetHistoryAsync(string? query = null, int limit = 100, CancellationToken ct = default);

    Task<ClipboardAddResult> TryAddEntryAsync(
        ClipboardEntry entry,
        int maxUnpinnedItems = 100,
        int retentionDays = 30,
        bool forcePrune = false,
        CancellationToken ct = default);

    Task SetPinnedAsync(string entryId, bool isPinned, CancellationToken ct = default);

    Task DeleteAsync(string entryId, CancellationToken ct = default);

    Task<int> ClearUnpinnedAsync(CancellationToken ct = default);

    Task<int> PruneAsync(
        int maxUnpinnedItems,
        int retentionDays,
        DateTimeOffset? utcNow = null,
        CancellationToken ct = default);

    Task<string?> SaveImageAssetAsync(string entryId, byte[] pngBytes, CancellationToken ct = default);

    string? ResolveImageFullPath(string? relativePath);
}

/// <summary>
/// Event-driven Windows clipboard monitor enforcing password-manager exclusion formats and self-copy loop suppression.
/// </summary>
public interface IClipboardMonitorAdapter : IDisposable
{
    bool IsMonitoring { get; }

    event EventHandler<ClipboardCaptureEventArgs>? ClipboardChanged;

    void AttachHostWindow(nint hostWindowHandle);

    void Start();

    void Start(nint hostWindowHandle);

    void Stop();

    bool HandleWindowMessage(int msg, nint wParam, nint lParam);

    Task<bool> TrySetTextAsync(string text, CancellationToken ct = default);

    Task<bool> TrySetImagePngAsync(byte[] pngBytes, RawBgraImage? dibFallback = null, CancellationToken ct = default);
}
