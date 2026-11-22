namespace Shima.Widgets.Clipboard;

/// <summary>
/// Clipboard widget preferences stored in widget_configs.settings_json for shima.clipboard.
/// </summary>
public sealed record ClipboardSettings
{
    public bool CaptureImages { get; init; } = true;

    public bool IsPaused { get; init; }
}
