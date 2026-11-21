namespace Shima.Core.Contracts;

/// <summary>
/// Persisted world clock city entry stored in SQLite world_clock_locations per data-model.md.
/// </summary>
public sealed record WorldClockLocation
{
    public required string LocationId { get; init; }

    public required string CityName { get; init; }

    public string CountryName { get; init; } = string.Empty;

    public required string WindowsTzId { get; init; }

    public required string IanaTzId { get; init; }

    public int SortOrder { get; init; }

    public bool IsPrimary { get; init; }
}

/// <summary>
/// Clock display preferences stored in widget_configs.settings_json for shima.clock.
/// </summary>
public sealed record ClockSettings
{
    public bool Use24HourFormat { get; init; }

    public bool ShowSeconds { get; init; }
}

/// <summary>
/// Local SQLite repository managing world clock locations, ordering, and active primary city.
/// </summary>
public interface IClockRepository
{
    Task<IReadOnlyList<WorldClockLocation>> GetAllAsync(CancellationToken ct = default);

    Task UpsertAsync(WorldClockLocation location, CancellationToken ct = default);

    Task DeleteAsync(string locationId, CancellationToken ct = default);

    Task SetPrimaryAsync(string locationId, CancellationToken ct = default);

    Task UpdateOrderAsync(IReadOnlyList<string> orderedLocationIds, CancellationToken ct = default);

    Task ReplaceAllAsync(IReadOnlyList<WorldClockLocation> locations, CancellationToken ct = default);
}
