namespace Shima.Widgets.Notes;

/// <summary>
/// Notes widget preferences stored in widget_configs.settings_json for shima.notes.
/// </summary>
public sealed record NotesSettings
{
    public bool ShowWordCount { get; init; } = true;
}
