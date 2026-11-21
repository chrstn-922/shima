using Shima.Core.Color;

namespace Shima.Core.Appearance;

/// <summary>
/// Manages the 6 immutable built-in presets, custom presets, saved swatches, and 12-slot FIFO recent colors in presets.json.
/// </summary>
public sealed class PresetManager : IPresetManager
{
    private const int MaxPresetNameLength = 40;
    private const int MaxSwatchLabelLength = 32;

    private static readonly IReadOnlyList<ColorPreset> BuiltInList = CreateBuiltInPresets();

    private readonly IPresetCatalogRepository _catalogRepository;
    private readonly IColorProcessingService _colorService;
    private readonly object _sync = new();

    private List<ColorPreset> _customPresets = [];
    private List<SavedColorSwatch> _savedSwatches = [];
    private List<string> _recentColors = [];
    private bool _initialized;

    public PresetManager(IPresetCatalogRepository catalogRepository, IColorProcessingService colorService)
    {
        _catalogRepository = catalogRepository ?? throw new ArgumentNullException(nameof(catalogRepository));
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));
    }

    public IReadOnlyList<ColorPreset> BuiltInPresets => BuiltInList;

    public IReadOnlyList<ColorPreset> CustomPresets
    {
        get
        {
            lock (_sync)
            {
                return _customPresets.ToArray();
            }
        }
    }

    public IReadOnlyList<ColorPreset> AllPresets
    {
        get
        {
            lock (_sync)
            {
                return BuiltInList.Concat(_customPresets).ToArray();
            }
        }
    }

    public IReadOnlyList<SavedColorSwatch> SavedSwatches
    {
        get
        {
            lock (_sync)
            {
                return _savedSwatches.ToArray();
            }
        }
    }

    public IReadOnlyList<string> RecentColors
    {
        get
        {
            lock (_sync)
            {
                return _recentColors.ToArray();
            }
        }
    }

    public event EventHandler? CatalogChanged;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var catalog = await _catalogRepository.LoadAsync(ct).ConfigureAwait(false);

        var custom = (catalog.CustomPresets ?? Array.Empty<ColorPreset>())
            .Where(p => p is not null && !p.IsBuiltIn && !IsBuiltInId(p.PresetId))
            .Select(SanitizeCustomPreset)
            .ToList();

        var swatches = (catalog.SavedSwatches ?? Array.Empty<SavedColorSwatch>())
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.SwatchId) && _colorService.TryParseHex(s.Hex, out _))
            .Select(s => s with
            {
                Label = TruncateTrimmed(s.Label, MaxSwatchLabelLength, "Swatch"),
                Hex = _colorService.FromHex(s.Hex).Hex
            })
            .ToList();

        var recents = new List<string>(PresetCatalog.MaxRecentColors);
        if (catalog.RecentColors is not null)
        {
            foreach (var rawHex in catalog.RecentColors)
            {
                if (_colorService.TryParseHex(rawHex, out var parsed)
                    && parsed is not null
                    && !recents.Contains(parsed.Hex, StringComparer.OrdinalIgnoreCase))
                {
                    recents.Add(parsed.Hex);
                    if (recents.Count >= PresetCatalog.MaxRecentColors)
                    {
                        break;
                    }
                }
            }
        }

        lock (_sync)
        {
            _customPresets = custom;
            _savedSwatches = swatches;
            _recentColors = recents;
            _initialized = true;
        }
    }

    public ColorPreset? FindById(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return null;
        }

        var builtIn = BuiltInList.FirstOrDefault(p => string.Equals(p.PresetId, presetId, StringComparison.OrdinalIgnoreCase));
        if (builtIn is not null)
        {
            return builtIn;
        }

        lock (_sync)
        {
            return _customPresets.FirstOrDefault(p => string.Equals(p.PresetId, presetId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task<ColorPreset> CreateCustomPresetAsync(
        string name,
        IslandColorPalette collapsedTokens,
        IslandColorPalette? expandedTokens = null,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(collapsedTokens);

        var safeName = NormalizePresetName(name);
        var now = DateTimeOffset.UtcNow;
        var normCollapsed = NormalizePalette(collapsedTokens);
        var normExpanded = expandedTokens is not null ? NormalizePalette(expandedTokens) : normCollapsed;

        var created = new ColorPreset
        {
            PresetId = $"custom.{Guid.NewGuid():N}",
            Name = EnsureUniqueCustomName(safeName),
            IsBuiltIn = false,
            CollapsedTokens = normCollapsed,
            ExpandedTokens = normExpanded,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        PresetCatalog snapshot;
        lock (_sync)
        {
            _customPresets.Add(created);
            snapshot = SnapshotCatalogLocked();
        }

        await _catalogRepository.SaveAsync(snapshot, ct).ConfigureAwait(false);
        CatalogChanged?.Invoke(this, EventArgs.Empty);
        return created;
    }

    public Task<ColorPreset> DuplicatePresetAsync(
        string sourcePresetId,
        string? newName = null,
        CancellationToken ct = default)
    {
        var source = FindById(sourcePresetId)
            ?? throw new KeyNotFoundException($"Preset '{sourcePresetId}' was not found.");

        var candidateName = string.IsNullOrWhiteSpace(newName)
            ? $"{source.Name} Copy"
            : newName;

        return CreateCustomPresetAsync(candidateName, source.CollapsedTokens, source.ExpandedTokens, ct);
    }

    public async Task<ColorPreset> RenameCustomPresetAsync(
        string presetId,
        string newName,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        EnsureMutablePresetId(presetId);

        var normalizedName = NormalizePresetName(newName);
        PresetCatalog snapshot;
        ColorPreset updated;

        lock (_sync)
        {
            var index = FindCustomIndexLocked(presetId);
            var existing = _customPresets[index];
            updated = existing with
            {
                Name = EnsureUniqueCustomName(normalizedName, excludePresetId: existing.PresetId),
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            _customPresets[index] = updated;
            snapshot = SnapshotCatalogLocked();
        }

        await _catalogRepository.SaveAsync(snapshot, ct).ConfigureAwait(false);
        CatalogChanged?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public async Task<ColorPreset> UpdateCustomPresetAsync(
        string presetId,
        IslandColorPalette collapsedTokens,
        IslandColorPalette? expandedTokens = null,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(collapsedTokens);
        EnsureMutablePresetId(presetId);

        var normCollapsed = NormalizePalette(collapsedTokens);
        var normExpanded = expandedTokens is not null ? NormalizePalette(expandedTokens) : normCollapsed;

        PresetCatalog snapshot;
        ColorPreset updated;

        lock (_sync)
        {
            var index = FindCustomIndexLocked(presetId);
            updated = _customPresets[index] with
            {
                CollapsedTokens = normCollapsed,
                ExpandedTokens = normExpanded,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            _customPresets[index] = updated;
            snapshot = SnapshotCatalogLocked();
        }

        await _catalogRepository.SaveAsync(snapshot, ct).ConfigureAwait(false);
        CatalogChanged?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public async Task DeleteCustomPresetAsync(string presetId, CancellationToken ct = default)
    {
        EnsureInitialized();
        EnsureMutablePresetId(presetId);

        PresetCatalog snapshot;
        lock (_sync)
        {
            var index = FindCustomIndexLocked(presetId);
            _customPresets.RemoveAt(index);
            snapshot = SnapshotCatalogLocked();
        }

        await _catalogRepository.SaveAsync(snapshot, ct).ConfigureAwait(false);
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SavedColorSwatch> SaveSwatchAsync(string label, string hex, CancellationToken ct = default)
    {
        EnsureInitialized();
        var cleanLabel = TruncateTrimmed(label, MaxSwatchLabelLength, "Custom Swatch");
        var normalizedHex = _colorService.FromHex(hex).Hex;

        var swatch = new SavedColorSwatch
        {
            SwatchId = $"swatch.{Guid.NewGuid():N}",
            Label = cleanLabel,
            Hex = normalizedHex
        };

        PresetCatalog snapshot;
        lock (_sync)
        {
            _savedSwatches.Add(swatch);
            snapshot = SnapshotCatalogLocked();
        }

        await _catalogRepository.SaveAsync(snapshot, ct).ConfigureAwait(false);
        CatalogChanged?.Invoke(this, EventArgs.Empty);
        return swatch;
    }

    public async Task DeleteSwatchAsync(string swatchId, CancellationToken ct = default)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(swatchId);

        PresetCatalog snapshot;
        lock (_sync)
        {
            var removed = _savedSwatches.RemoveAll(s => string.Equals(s.SwatchId, swatchId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                throw new KeyNotFoundException($"Saved swatch '{swatchId}' was not found.");
            }

            snapshot = SnapshotCatalogLocked();
        }

        await _catalogRepository.SaveAsync(snapshot, ct).ConfigureAwait(false);
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RecordRecentColorAsync(string hex, CancellationToken ct = default)
    {
        EnsureInitialized();
        var normalizedHex = _colorService.FromHex(hex).Hex;

        PresetCatalog snapshot;
        lock (_sync)
        {
            _recentColors.RemoveAll(c => string.Equals(c, normalizedHex, StringComparison.OrdinalIgnoreCase));
            _recentColors.Insert(0, normalizedHex);
            if (_recentColors.Count > PresetCatalog.MaxRecentColors)
            {
                _recentColors.RemoveRange(PresetCatalog.MaxRecentColors, _recentColors.Count - PresetCatalog.MaxRecentColors);
            }

            snapshot = SnapshotCatalogLocked();
        }

        await _catalogRepository.SaveAsync(snapshot, ct).ConfigureAwait(false);
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }

    private PresetCatalog SnapshotCatalogLocked() => new()
    {
        SchemaVersion = 1,
        CustomPresets = _customPresets.ToArray(),
        SavedSwatches = _savedSwatches.ToArray(),
        RecentColors = _recentColors.ToArray()
    };

    private void EnsureInitialized()
    {
        lock (_sync)
        {
            if (!_initialized)
            {
                _customPresets = [];
                _savedSwatches = [];
                _recentColors = [];
                _initialized = true;
            }
        }
    }

    private void EnsureMutablePresetId(string presetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        if (IsBuiltInId(presetId))
        {
            throw new InvalidOperationException($"Built-in preset '{presetId}' is immutable and cannot be modified or deleted.");
        }
    }

    private int FindCustomIndexLocked(string presetId)
    {
        var index = _customPresets.FindIndex(p => string.Equals(p.PresetId, presetId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new KeyNotFoundException($"Custom preset '{presetId}' was not found.");
        }

        return index;
    }

    private string EnsureUniqueCustomName(string baseName, string? excludePresetId = null)
    {
        var existingNames = AllPresets
            .Where(p => excludePresetId is null || !string.Equals(p.PresetId, excludePresetId, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existingNames.Contains(baseName))
        {
            return baseName;
        }

        var suffix = 2;
        while (existingNames.Contains(TruncateTrimmed($"{baseName} ({suffix})", MaxPresetNameLength, baseName)))
        {
            suffix++;
        }

        return TruncateTrimmed($"{baseName} ({suffix})", MaxPresetNameLength, baseName);
    }

    private ColorPreset SanitizeCustomPreset(ColorPreset preset) => preset with
    {
        IsBuiltIn = false,
        Name = TruncateTrimmed(preset.Name, MaxPresetNameLength, "Custom Theme"),
        CollapsedTokens = NormalizePalette(preset.CollapsedTokens),
        ExpandedTokens = NormalizePalette(preset.ExpandedTokens)
    };

    private IslandColorPalette NormalizePalette(IslandColorPalette? palette)
    {
        var p = palette ?? IslandColorPalette.Midnight;
        return new IslandColorPalette
        {
            BackgroundHex = NormalizeHex(p.BackgroundHex, IslandColorPalette.MidnightBackground),
            AccentHex = NormalizeHex(p.AccentHex, IslandColorPalette.MidnightAccent),
            BorderHex = NormalizeHex(p.BorderHex, IslandColorPalette.MidnightBorder),
            GlowHex = NormalizeHex(p.GlowHex, IslandColorPalette.MidnightGlow),
            PrimaryTextHex = NormalizeHex(p.PrimaryTextHex, IslandColorPalette.MidnightPrimaryText),
            SecondaryIconHex = NormalizeHex(p.SecondaryIconHex, IslandColorPalette.MidnightSecondaryIcon)
        };
    }

    private string NormalizeHex(string? candidate, string fallback) =>
        _colorService.TryParseHex(candidate, out var parsed) && parsed is not null
            ? parsed.Hex
            : fallback;

    private static string NormalizePresetName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return TruncateTrimmed(name, MaxPresetNameLength, "Custom Theme");
    }

    private static string TruncateTrimmed(string? text, int maxLength, string fallback)
    {
        var trimmed = string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static bool IsBuiltInId(string presetId) =>
        BuiltInList.Any(p => string.Equals(p.PresetId, presetId, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ColorPreset> CreateBuiltInPresets()
    {
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return
        [
            CreateBuiltIn("builtin.midnight", "Midnight", IslandColorPalette.Midnight, epoch),
            CreateBuiltIn(
                "builtin.snow",
                "Snow",
                new IslandColorPalette
                {
                    BackgroundHex = "#F8FAFC",
                    AccentHex = "#0F172A",
                    BorderHex = "#CBD5E1",
                    GlowHex = "#94A3B8",
                    PrimaryTextHex = "#0F172A",
                    SecondaryIconHex = "#475569"
                },
                epoch),
            CreateBuiltIn(
                "builtin.ocean",
                "Ocean",
                new IslandColorPalette
                {
                    BackgroundHex = "#071324",
                    AccentHex = "#38BDF8",
                    BorderHex = "#1E3A5F",
                    GlowHex = "#0284C7",
                    PrimaryTextHex = "#F0F9FF",
                    SecondaryIconHex = "#7DD3FC"
                },
                epoch),
            CreateBuiltIn(
                "builtin.lavender",
                "Lavender",
                new IslandColorPalette
                {
                    BackgroundHex = "#130F24",
                    AccentHex = "#C084FC",
                    BorderHex = "#2E2352",
                    GlowHex = "#9333EA",
                    PrimaryTextHex = "#FAF5FF",
                    SecondaryIconHex = "#D8B4FE"
                },
                epoch),
            CreateBuiltIn(
                "builtin.emerald",
                "Emerald",
                new IslandColorPalette
                {
                    BackgroundHex = "#061C14",
                    AccentHex = "#34D399",
                    BorderHex = "#134E3A",
                    GlowHex = "#059669",
                    PrimaryTextHex = "#ECFDF5",
                    SecondaryIconHex = "#6EE7B7"
                },
                epoch),
            CreateBuiltIn(
                "builtin.sunset",
                "Sunset",
                new IslandColorPalette
                {
                    BackgroundHex = "#1F1013",
                    AccentHex = "#FB923C",
                    BorderHex = "#4C1D24",
                    GlowHex = "#EA580C",
                    PrimaryTextHex = "#FFF7ED",
                    SecondaryIconHex = "#FDBA74"
                },
                epoch)
        ];
    }

    private static ColorPreset CreateBuiltIn(
        string id,
        string name,
        IslandColorPalette palette,
        DateTimeOffset epoch) => new()
    {
        PresetId = id,
        Name = name,
        IsBuiltIn = true,
        CollapsedTokens = palette,
        ExpandedTokens = palette,
        CreatedAtUtc = epoch,
        UpdatedAtUtc = epoch
    };
}
