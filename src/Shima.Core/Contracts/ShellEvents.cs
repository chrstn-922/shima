namespace Shima.Core.Contracts;

/// <summary>
/// Event published when Windows system clock or timezone changes (WM_TIMECHANGE).
/// </summary>
public sealed record SystemTimeChangedEvent(DateTimeOffset ObservedUtc);

/// <summary>
/// Event published by widgets to open or focus SettingsWindow at a specific section tag.
/// </summary>
public sealed record OpenSettingsRequestedEvent(string? SectionTag = null);

/// <summary>
/// Event published by widgets when an inner text input acquires or releases keyboard focus.
/// </summary>
public sealed record IslandInputFocusRequestedEvent(bool IsActive);

/// <summary>
/// Action button descriptor rendered inside transient Notification peeks when supported by the source widget.
/// </summary>
public sealed record NotificationPeekAction(
    string Label,
    string AutomationName,
    bool IsPrimary,
    Func<Task> ExecuteAsync);

/// <summary>
/// Event published by widgets requesting transient Notification peek banner copy and target widget routing.
/// </summary>
public sealed record NotificationPeekContentEvent(
    string WidgetId,
    string Title,
    string Body,
    string? ItemId = null,
    NotificationPeekAction? PrimaryAction = null,
    NotificationPeekAction? SecondaryAction = null);

/// <summary>
/// Event published by widgets to dismiss a previously surfaced transient Notification peek (e.g., terminal sync).
/// </summary>
public sealed record NotificationPeekDismissRequestedEvent(string WidgetId, string? ItemId = null);

/// <summary>
/// Event published when a widget activates or clears an inline Collapsed-notch status bar indicator.
/// </summary>
public sealed record CollapsedStatusIndicatorChangedEvent(string WidgetId, bool IsVisible);

/// <summary>
/// Event published by the island shell when the user hovers the Collapsed/Active notch.
/// </summary>
public sealed record IslandCollapsedHoveredEvent(DateTimeOffset ObservedUtc);
