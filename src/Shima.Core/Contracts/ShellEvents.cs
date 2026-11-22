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
/// Event published by widgets requesting transient Notification peek banner copy and target widget routing.
/// </summary>
public sealed record NotificationPeekContentEvent(string WidgetId, string Title, string Body, string? ItemId = null);
