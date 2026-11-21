using Shima.Core.Contracts;

namespace Shima.Core.Shell;

public sealed class DefaultThemeTokenProvider : IThemeTokenProvider
{
    private static readonly IReadOnlyDictionary<string, string> DefaultTokens =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SurfaceCollapsed"] = "#000000",
            ["SurfaceExpanded"] = "#000000",
            ["SurfaceElevated"] = "#141414",
            ["AccentBrush"] = "#3B82F6",
            ["BorderHairline"] = "#262626",
            ["GlowBrush"] = "#3B82F6",
            ["TextPrimary"] = "#FFFFFF",
            ["TextMuted"] = "#A1A1AA"
        };

    public event EventHandler? ThemeChanged
    {
        add { }
        remove { }
    }

    public string GetToken(string tokenName) =>
        DefaultTokens.TryGetValue(tokenName, out var hex) ? hex : "#FFFFFF";

    public string GetWidgetAccent(string widgetId) => DefaultTokens["AccentBrush"];
}
