namespace Shima.Core.Contracts;

/// <summary>
/// Event published when Windows system clock or timezone changes (WM_TIMECHANGE).
/// </summary>
public sealed record SystemTimeChangedEvent(DateTimeOffset ObservedUtc);

/// <summary>
/// Event published by widgets to open or focus SettingsWindow at a specific section tag.
/// </summary>
public sealed record OpenSettingsRequestedEvent(string? SectionTag = null);
