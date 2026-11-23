namespace Shima.Core.Contracts;

public enum IslandShellState
{
    Collapsed,
    Active,
    Notification,
    Expanded,
    Fullscreen,
    Suspended
}

public enum PowerProfile
{
    AC,
    Battery,
    EnergySaver,
    DisplayOff
}

public enum CompactPillPriority
{
    Background = 0,
    Normal = 1,
    Urgent = 2
}

public sealed record IslandStateChangedEventArgs(IslandShellState PreviousState, IslandShellState NewState);

public interface IIslandStateManager
{
    IslandShellState CurrentState { get; }

    VisibilityPriority CurrentPriority { get; }

    bool FullscreenBreakthroughAlerts { get; set; }

    bool IsNotificationsPaused { get; set; }

    string? CompactPillBadgeText { get; }

    string? CompactActiveWidgetId { get; }

    string? CompactCompanionWidgetId => null;

    TimeSpan CurrentNotificationTtl { get; }

    event EventHandler<IslandStateChangedEventArgs>? StateChanged;

    event EventHandler? CompactPillUpdated;

    Task TransitionToAsync(IslandShellState newState, CancellationToken ct = default);

    void SetSystemSuspended(bool suspended);

    void SetFullscreenOnHostMonitor(bool isFullscreen);

    void SetCompactActive(bool isActive, string? badgeText = null);

    void SetCompactActive(string? widgetId, bool isActive, string? badgeText = null);

    void SetCompactActive(string? widgetId, bool isActive, string? badgeText, CompactPillPriority priority) =>
        SetCompactActive(widgetId, isActive, badgeText);

    void RequestNotificationPeek(NotificationPriority priority, TimeSpan ttl);

    void ClearNotificationPeek();

    void RequestUserExpand();

    void RequestUserCollapse(bool force = false);

    void ToggleHotkeySummon();

    void DismissHotkeySummon();

    void SetInputFocusActive(bool isFocused);

    void Reevaluate();
}
