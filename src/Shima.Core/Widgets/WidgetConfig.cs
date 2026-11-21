namespace Shima.Core.Widgets;

public sealed record WidgetConfig
{
    public required string WidgetId { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int SortOrder { get; init; }
    public bool AllowPeekNotifications { get; init; } = true;
    public string SettingsJson { get; init; } = "{}";
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public interface IWidgetConfigRepository
{
    Task<IReadOnlyList<WidgetConfig>> GetAllAsync();

    Task SaveAsync(WidgetConfig config);

    Task UpdateOrderAsync(IReadOnlyList<string> orderedIds);
}
