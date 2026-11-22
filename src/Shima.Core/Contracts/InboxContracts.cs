namespace Shima.Core.Contracts;

public enum NotificationAccessState
{
    Allowed,
    DeniedByUserOrPolicy,
    IdentityUnavailable,
    UnsupportedOs
}

public sealed record OsNotificationDto(
    uint OsToastId,
    string AppId,
    string AppName,
    string? Title,
    string? BodyText,
    byte[]? AppIconPngBytes,
    DateTimeOffset ReceivedAtUtc);

public sealed class OsNotificationEventArgs(OsNotificationDto notification) : EventArgs
{
    public OsNotificationDto Notification { get; } = notification ?? throw new ArgumentNullException(nameof(notification));
}

/// <summary>
/// Native Windows toast listener adapter strictly backed by UserNotificationListener without third-party UI scraping.
/// </summary>
public interface INotificationListenerAdapter : IDisposable
{
    Task<NotificationAccessState> GetAccessStateAsync(CancellationToken ct = default);

    Task<NotificationAccessState> RequestAccessAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OsNotificationDto>> GetCurrentToastSnapshotAsync(CancellationToken ct = default);

    Task<bool> TryRemoveFromActionCenterAsync(uint osToastId, CancellationToken ct = default);

    bool IsDoNotDisturbActive();

    Task StartListeningAsync(CancellationToken ct = default);

    void StopListening();

    event EventHandler<OsNotificationEventArgs>? NotificationArrived;

    event EventHandler<uint>? NotificationRemovedExternally;
}

/// <summary>
/// Persisted local toast notification stored in SQLite inbox_notifications per data-model.md.
/// </summary>
public sealed record InboxNotificationItem
{
    public const int MaxAppNameLength = 128;
    public const int MaxTitleLength = 512;
    public const int MaxBodyLength = 4096;
    public const int MaxStoredItems = 500;

    public required string NotificationId { get; init; }

    public uint? OsToastId { get; init; }

    public required string AppId { get; init; }

    public required string AppName { get; init; }

    public string? AppIconRelPath { get; init; }

    public string Title { get; init; } = string.Empty;

    public string BodyText { get; init; } = string.Empty;

    public bool IsDismissed { get; init; }

    public DateTimeOffset ReceivedAtUtc { get; init; }
}

public sealed record InboxAppGroup(
    string AppId,
    string AppName,
    string? AppIconRelPath,
    IReadOnlyList<InboxNotificationItem> Items);

/// <summary>
/// Local SQLite repository managing notification inbox grouping, dismissals, and retention pruning.
/// </summary>
public interface IInboxRepository
{
    Task<IReadOnlyList<InboxAppGroup>> GetGroupedByAppAsync(bool includeDismissed = false, CancellationToken ct = default);

    Task<IReadOnlyList<InboxNotificationItem>> GetAllActiveAsync(CancellationToken ct = default);

    Task<InboxNotificationItem> UpsertToastAsync(InboxNotificationItem item, CancellationToken ct = default);

    Task<InboxNotificationItem?> FindByOsToastIdAsync(uint osToastId, CancellationToken ct = default);

    Task MarkDismissedAsync(string notificationId, CancellationToken ct = default);

    Task MarkDismissedByOsToastIdAsync(uint osToastId, CancellationToken ct = default);

    Task<IReadOnlyList<uint>> ClearAppGroupAsync(string appId, CancellationToken ct = default);

    Task<IReadOnlyList<uint>> ClearAllAsync(CancellationToken ct = default);

    Task<int> PruneExpiredAsync(
        int retentionDays = 14,
        int maxStoredItems = InboxNotificationItem.MaxStoredItems,
        DateTimeOffset? utcNow = null,
        CancellationToken ct = default);

    Task<string?> SaveAppIconAssetAsync(string appId, byte[] pngBytes, CancellationToken ct = default);

    string? ResolveIconFullPath(string? relativePath);
}
