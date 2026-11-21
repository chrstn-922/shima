using Shima.Core.Appearance;
using Shima.Infrastructure.Storage;

namespace Shima.Infrastructure.Appearance;

/// <summary>
/// Atomic JSON repository for %LOCALAPPDATA%\Shima\config\appearance.json.
/// </summary>
public sealed class AtomicAppearanceConfigRepository : IAppearanceConfigRepository
{
    public const string RelativePath = "config/appearance.json";

    private readonly IAtomicConfigStore<AppearanceConfiguration> _store;

    public AtomicAppearanceConfigRepository(IAtomicConfigStore<AppearanceConfiguration> store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<AppearanceConfiguration> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _store.LoadOrRecoverAsync(RelativePath, static () => AppearanceConfiguration.Default);
    }

    public Task SaveAsync(AppearanceConfiguration configuration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ct.ThrowIfCancellationRequested();
        return _store.SaveAtomicAsync(RelativePath, configuration);
    }
}

/// <summary>
/// Atomic JSON repository for %LOCALAPPDATA%\Shima\config\presets.json.
/// </summary>
public sealed class AtomicPresetCatalogRepository : IPresetCatalogRepository
{
    public const string RelativePath = "config/presets.json";

    private readonly IAtomicConfigStore<PresetCatalog> _store;

    public AtomicPresetCatalogRepository(IAtomicConfigStore<PresetCatalog> store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<PresetCatalog> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _store.LoadOrRecoverAsync(RelativePath, static () => PresetCatalog.Empty);
    }

    public Task SaveAsync(PresetCatalog catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ct.ThrowIfCancellationRequested();
        return _store.SaveAtomicAsync(RelativePath, catalog);
    }
}
