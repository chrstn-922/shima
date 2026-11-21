using System.Text.RegularExpressions;

namespace Shima.Core.Contracts;

public enum WidgetActivationReason
{
    UserSelection,
    NotificationPeek,
    Restore
}

public sealed record WidgetDescriptor
{
    private static readonly Regex HexColorPattern = new(
        "^#[0-9A-F]{6}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public required string WidgetId { get; init; }
    public required string DisplayName { get; init; }
    public int DefaultOrder { get; init; }
    public bool RequiresBackgroundSubscription { get; init; }
    public bool SupportsCompactActiveState { get; init; }
    public bool SupportsNotificationPeek { get; init; }
    public string DefaultAccentHex { get; init; } = "#FFFFFF";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WidgetId))
        {
            throw new ArgumentException("WidgetId is required.", nameof(WidgetId));
        }

        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 32)
        {
            throw new ArgumentException("DisplayName must be 1-32 characters.", nameof(DisplayName));
        }

        if (DefaultOrder is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultOrder), "DefaultOrder must be 0-7.");
        }

        if (!HexColorPattern.IsMatch(DefaultAccentHex))
        {
            throw new ArgumentException("DefaultAccentHex must match ^#[0-9A-F]{6}$.", nameof(DefaultAccentHex));
        }
    }
}

public interface IWidgetModule : IAsyncDisposable
{
    WidgetDescriptor Metadata { get; }

    Task InitializeAsync(IWidgetContext context, CancellationToken ct);

    Task ActivateAsync(WidgetActivationReason reason);

    Task DeactivateAsync();

    void OnShellStateChanged(IslandShellState newState);

    void OnPowerProfileChanged(PowerProfile profile);

    object CreateExpandedView();

    object? CreateCompactActiveView();

    object CreateSettingsView();

    Task ResetSettingsAsync();
}
