namespace Shima.Core.Contracts;

public interface IWidgetContext
{
    string WidgetId { get; }

    IEventDispatcher Events { get; }

    IWidgetSettingsStore Settings { get; }

    IIslandAttentionRequester Island { get; }

    IThemeTokenProvider Theme { get; }

    ILogger Logger { get; }
}

public interface IWidgetSettingsStore
{
    Task<string?> ReadJsonAsync(CancellationToken ct = default);

    Task WriteJsonAsync(string settingsJson, CancellationToken ct = default);

    Task ResetAsync(CancellationToken ct = default);
}

public enum NotificationPriority
{
    Low,
    Normal,
    High,
    Critical
}

public interface IIslandAttentionRequester
{
    void RequestPeekNotification(NotificationPriority priority, TimeSpan ttl);

    void UpdateCompactPill(string? badgeText);
}

public interface IThemeTokenProvider
{
    string GetToken(string tokenName);

    string GetWidgetAccent(string widgetId);

    event EventHandler? ThemeChanged;
}
