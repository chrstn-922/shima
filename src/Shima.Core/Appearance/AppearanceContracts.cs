using Shima.Core.Color;
using Shima.Core.Contracts;

namespace Shima.Core.Appearance;

/// <summary>
/// Persistence repository for %LOCALAPPDATA%\Shima\config\appearance.json.
/// </summary>
public interface IAppearanceConfigRepository
{
    Task<AppearanceConfiguration> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(AppearanceConfiguration configuration, CancellationToken ct = default);
}

/// <summary>
/// Persistence repository for %LOCALAPPDATA%\Shima\config\presets.json.
/// </summary>
public interface IPresetCatalogRepository
{
    Task<PresetCatalog> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(PresetCatalog catalog, CancellationToken ct = default);
}

/// <summary>
/// Manages the 6 immutable built-in presets, custom preset CRUD, named swatches, and 12-slot FIFO recent colors in presets.json.
/// </summary>
public interface IPresetManager
{
    IReadOnlyList<ColorPreset> BuiltInPresets { get; }

    IReadOnlyList<ColorPreset> CustomPresets { get; }

    IReadOnlyList<ColorPreset> AllPresets { get; }

    IReadOnlyList<SavedColorSwatch> SavedSwatches { get; }

    IReadOnlyList<string> RecentColors { get; }

    event EventHandler? CatalogChanged;

    Task InitializeAsync(CancellationToken ct = default);

    ColorPreset? FindById(string? presetId);

    Task<ColorPreset> CreateCustomPresetAsync(
        string name,
        IslandColorPalette collapsedTokens,
        IslandColorPalette? expandedTokens = null,
        CancellationToken ct = default);

    Task<ColorPreset> DuplicatePresetAsync(string sourcePresetId, string? newName = null, CancellationToken ct = default);

    Task<ColorPreset> RenameCustomPresetAsync(string presetId, string newName, CancellationToken ct = default);

    Task<ColorPreset> UpdateCustomPresetAsync(
        string presetId,
        IslandColorPalette collapsedTokens,
        IslandColorPalette? expandedTokens = null,
        CancellationToken ct = default);

    Task DeleteCustomPresetAsync(string presetId, CancellationToken ct = default);

    Task<SavedColorSwatch> SaveSwatchAsync(string label, string hex, CancellationToken ct = default);

    Task DeleteSwatchAsync(string swatchId, CancellationToken ct = default);

    Task RecordRecentColorAsync(string hex, CancellationToken ct = default);
}

/// <summary>
/// Isolated draft editing session driving live island preview and pinning without persisting until CommitAsync.
/// </summary>
public interface IAppearanceEditingSession : IDisposable
{
    Guid SessionId { get; }

    AppearanceConfiguration CommittedSnapshot { get; }

    AppearanceConfiguration DraftConfiguration { get; }

    bool IsLivePreviewPinned { get; }

    PreviewTargetState CurrentPreviewState { get; }

    ContrastAuditReport CurrentContrastReport { get; }

    bool IsCompleted { get; }

    void SetPreviewState(PreviewTargetState state);

    void UpdateToken(AppearanceTokenSlot slot, IslandStateTarget target, string hex);

    void PreviewWidgetAccent(string widgetId, string? hex);

    void PreviewPreset(string presetId);

    void PreviewLinkCollapsedAndExpanded(bool linked);

    void PreviewUsePerWidgetAccents(bool enabled);

    void PreviewGlowOpacity(double glowOpacity);

    void PreviewBorderThickness(double borderThicknessDip);

    void ApplyRecommendedContrastFix();

    string ResetSlotToDefault(AppearanceTokenSlot slot, IslandStateTarget target = IslandStateTarget.Both);

    Task<AppearanceConfiguration> CommitAsync(CancellationToken ct = default);

    void Cancel();
}

/// <summary>
/// Coordinates active appearance tokens, live preview sessions, WCAG contrast audits, and per-widget accent overrides.
/// </summary>
public interface IAppearanceManager : IThemeTokenProvider
{
    AppearanceConfiguration CurrentCommitted { get; }

    AppearanceConfiguration ActiveRendered { get; }

    ContrastAuditReport CurrentContrastReport { get; }

    IAppearanceEditingSession? ActiveSession { get; }

    event EventHandler<AppearanceConfiguration>? AppearanceChanged;

    event EventHandler<AppearanceConfiguration>? RenderedAppearanceChanged;

    Task InitializeAsync(CancellationToken ct = default);

    IAppearanceEditingSession BeginEditingSession(
        bool acquireLivePreviewPin = true,
        PreviewTargetState initialPreviewState = PreviewTargetState.Expanded);

    Task ApplyPresetAsync(string presetId, CancellationToken ct = default);

    Task SetLinkCollapsedAndExpandedAsync(bool linked, CancellationToken ct = default);

    Task SetUsePerWidgetAccentsAsync(bool enabled, CancellationToken ct = default);

    Task SetGlowOpacityAsync(double glowOpacity, CancellationToken ct = default);

    Task SetBorderThicknessAsync(double borderThicknessDip, CancellationToken ct = default);

    Task SetWidgetAccentAsync(string widgetId, string? hex, CancellationToken ct = default);

    Task<AppearanceConfiguration> ResetAllToDefaultsAsync(CancellationToken ct = default);
}
