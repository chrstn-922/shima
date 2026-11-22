namespace Shima.Widgets.Inbox;

/// <summary>
/// Notification Inbox widget preferences stored in widget_configs.settings_json for shima.inbox.
/// </summary>
public sealed record InboxSettings
{
    public bool MaskNotificationBodyInPopups { get; init; }

    public int PreviewDurationSeconds { get; init; } = 4;

    public IReadOnlyList<string> ExcludedApps { get; init; } = Array.Empty<string>();
}
