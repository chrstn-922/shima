using System.Globalization;
using Microsoft.Data.Sqlite;
using Shima.Core.Widgets;

namespace Shima.Infrastructure.Persistence;

public sealed class SqliteWidgetConfigRepository : IWidgetConfigRepository
{
    private readonly string _connectionString;

    public SqliteWidgetConfigRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task<IReadOnlyList<WidgetConfig>> GetAllAsync()
    {
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT widget_id, is_enabled, sort_order, allow_peek_notifications, settings_json, updated_at_utc
            FROM widget_configs
            ORDER BY sort_order, widget_id;
            """;

        var configs = new List<WidgetConfig>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            configs.Add(new WidgetConfig
            {
                WidgetId = reader.GetString(0),
                IsEnabled = reader.GetInt64(1) != 0,
                SortOrder = reader.GetInt32(2),
                AllowPeekNotifications = reader.GetInt64(3) != 0,
                SettingsJson = reader.GetString(4),
                UpdatedAtUtc = DateTimeOffset.Parse(
                    reader.GetString(5),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None)
            });
        }

        return configs;
    }

    public async Task SaveAsync(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.WidgetId);

        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO widget_configs (widget_id, is_enabled, sort_order, allow_peek_notifications, settings_json, updated_at_utc)
            VALUES ($widget_id, $is_enabled, $sort_order, $allow_peek_notifications, $settings_json, $updated_at_utc)
            ON CONFLICT(widget_id) DO UPDATE SET
                is_enabled = excluded.is_enabled,
                sort_order = excluded.sort_order,
                allow_peek_notifications = excluded.allow_peek_notifications,
                settings_json = excluded.settings_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$widget_id", config.WidgetId);
        command.Parameters.AddWithValue("$is_enabled", config.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$sort_order", config.SortOrder);
        command.Parameters.AddWithValue("$allow_peek_notifications", config.AllowPeekNotifications ? 1 : 0);
        command.Parameters.AddWithValue("$settings_json", config.SettingsJson);
        command.Parameters.AddWithValue(
            "$updated_at_utc",
            config.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task UpdateOrderAsync(IReadOnlyList<string> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        if (orderedIds.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync().ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var updatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        for (var index = 0; index < orderedIds.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE widget_configs SET sort_order = $sort_order, updated_at_utc = $updated_at_utc WHERE widget_id = $widget_id;";
            command.Parameters.AddWithValue("$sort_order", index);
            command.Parameters.AddWithValue("$updated_at_utc", updatedAt);
            command.Parameters.AddWithValue("$widget_id", orderedIds[index]);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        transaction.Commit();
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
