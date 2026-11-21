using Microsoft.Data.Sqlite;
using Shima.Core.Contracts;

namespace Shima.Infrastructure.Repositories;

/// <summary>
/// Parameterized SQLite repository persisting world clock cities in world_clock_locations.
/// </summary>
public sealed class ClockRepository : IClockRepository
{
    private readonly string _connectionString;

    public ClockRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task<IReadOnlyList<WorldClockLocation>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT location_id, city_name, country_name, windows_tz_id, iana_tz_id, sort_order, is_primary
            FROM world_clock_locations
            ORDER BY sort_order, location_id;
            """;

        var items = new List<WorldClockLocation>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new WorldClockLocation
            {
                LocationId = reader.GetString(0),
                CityName = reader.GetString(1),
                CountryName = reader.GetString(2),
                WindowsTzId = reader.GetString(3),
                IanaTzId = reader.GetString(4),
                SortOrder = reader.GetInt32(5),
                IsPrimary = reader.GetInt64(6) != 0
            });
        }

        return items;
    }

    public async Task UpsertAsync(WorldClockLocation location, CancellationToken ct = default)
    {
        var validated = ValidateAndNormalize(location);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await UpsertCoreAsync(connection, transaction: null, validated, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string locationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM world_clock_locations WHERE location_id = $location_id;";
        command.Parameters.AddWithValue("$location_id", locationId.Trim());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetPrimaryAsync(string locationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        var trimmedId = locationId.Trim();

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        await using (var clearCmd = connection.CreateCommand())
        {
            clearCmd.Transaction = transaction;
            clearCmd.CommandText = "UPDATE world_clock_locations SET is_primary = 0 WHERE is_primary != 0;";
            await clearCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var setCmd = connection.CreateCommand())
        {
            setCmd.Transaction = transaction;
            setCmd.CommandText = "UPDATE world_clock_locations SET is_primary = 1 WHERE location_id = $location_id;";
            setCmd.Parameters.AddWithValue("$location_id", trimmedId);
            var affected = await setCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (affected == 0)
            {
                throw new KeyNotFoundException($"World clock location '{trimmedId}' was not found.");
            }
        }

        transaction.Commit();
    }

    public async Task UpdateOrderAsync(IReadOnlyList<string> orderedLocationIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedLocationIds);
        if (orderedLocationIds.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        for (var i = 0; i < orderedLocationIds.Count; i++)
        {
            var id = orderedLocationIds[i]?.Trim();
            ArgumentException.ThrowIfNullOrWhiteSpace(id);

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE world_clock_locations SET sort_order = $sort_order WHERE location_id = $location_id;";
            cmd.Parameters.AddWithValue("$sort_order", i);
            cmd.Parameters.AddWithValue("$location_id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    public async Task ReplaceAllAsync(IReadOnlyList<WorldClockLocation> locations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var validated = locations.Select(ValidateAndNormalize).ToArray();

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        await using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM world_clock_locations;";
            await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var item in validated)
        {
            await UpsertCoreAsync(connection, transaction, item, ct).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    private static async Task UpsertCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        WorldClockLocation location,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO world_clock_locations (location_id, city_name, country_name, windows_tz_id, iana_tz_id, sort_order, is_primary)
            VALUES ($location_id, $city_name, $country_name, $windows_tz_id, $iana_tz_id, $sort_order, $is_primary)
            ON CONFLICT(location_id) DO UPDATE SET
                city_name = excluded.city_name,
                country_name = excluded.country_name,
                windows_tz_id = excluded.windows_tz_id,
                iana_tz_id = excluded.iana_tz_id,
                sort_order = excluded.sort_order,
                is_primary = excluded.is_primary;
            """;

        command.Parameters.AddWithValue("$location_id", location.LocationId);
        command.Parameters.AddWithValue("$city_name", location.CityName);
        command.Parameters.AddWithValue("$country_name", location.CountryName);
        command.Parameters.AddWithValue("$windows_tz_id", location.WindowsTzId);
        command.Parameters.AddWithValue("$iana_tz_id", location.IanaTzId);
        command.Parameters.AddWithValue("$sort_order", location.SortOrder);
        command.Parameters.AddWithValue("$is_primary", location.IsPrimary ? 1 : 0);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static WorldClockLocation ValidateAndNormalize(WorldClockLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(location.LocationId);

        var city = (location.CityName ?? string.Empty).Trim();
        if (city.Length is < 1 or > 80)
        {
            throw new ArgumentException("CityName must be 1 to 80 characters.", nameof(location));
        }

        var country = (location.CountryName ?? string.Empty).Trim();
        if (country.Length > 80)
        {
            country = country[..80];
        }

        if (location.SortOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(location), "SortOrder must be non-negative.");
        }

        var windowsTzId = (location.WindowsTzId ?? string.Empty).Trim();
        _ = TimeZoneInfo.FindSystemTimeZoneById(windowsTzId);

        var ianaTzId = string.IsNullOrWhiteSpace(location.IanaTzId)
            ? (TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsTzId, out var converted) ? converted : windowsTzId)
            : location.IanaTzId.Trim();

        return location with
        {
            LocationId = location.LocationId.Trim(),
            CityName = city,
            CountryName = country,
            WindowsTzId = windowsTzId,
            IanaTzId = ianaTzId
        };
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
