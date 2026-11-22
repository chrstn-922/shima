using Shima.Core.Contracts;

namespace Shima.Core.Shell;

public sealed class IslandAttentionRequester(IIslandStateManager stateManager) : IIslandAttentionRequester
{
    private readonly IIslandStateManager _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));

    public void RequestPeekNotification(NotificationPriority priority, TimeSpan ttl) =>
        _stateManager.RequestNotificationPeek(priority, ttl);

    public void UpdateCompactPill(string? badgeText) =>
        _stateManager.SetCompactActive(!string.IsNullOrWhiteSpace(badgeText), badgeText);

    public void UpdateCompactPill(string? widgetId, string? badgeText) =>
        _stateManager.SetCompactActive(widgetId, !string.IsNullOrWhiteSpace(badgeText), badgeText);

    public void UpdateCompactPill(string? widgetId, string? badgeText, CompactPillPriority priority) =>
        _stateManager.SetCompactActive(widgetId, !string.IsNullOrWhiteSpace(badgeText), badgeText, priority);
}
