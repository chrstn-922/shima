using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;

namespace Shima.Infrastructure.Persistence;

public sealed record SqliteMigration(int Version, string Description, string Sql);

public sealed class SqliteMigrationRunner
{
    private static readonly SqliteMigration[] BuiltInMigrations =
    [
        new SqliteMigration(
            1,
            "widget_configs",
            """
            CREATE TABLE IF NOT EXISTS widget_configs (
                widget_id TEXT PRIMARY KEY,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
                allow_peek_notifications INTEGER NOT NULL DEFAULT 1,
                settings_json TEXT NOT NULL DEFAULT '{}',
                updated_at_utc TEXT NOT NULL
            );
            """),
        new SqliteMigration(
            2,
            "world_clock_locations",
            """
            CREATE TABLE IF NOT EXISTS world_clock_locations (
                location_id TEXT PRIMARY KEY,
                city_name TEXT NOT NULL CHECK (length(city_name) BETWEEN 1 AND 80),
                country_name TEXT NOT NULL CHECK (length(country_name) <= 80),
                windows_tz_id TEXT NOT NULL,
                iana_tz_id TEXT NOT NULL,
                sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
                is_primary INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1))
            );
            CREATE INDEX IF NOT EXISTS idx_world_clock_locations_sort_order
                ON world_clock_locations (sort_order, location_id);
            """),
        new SqliteMigration(
            3,
            "notes",
            """
            CREATE TABLE IF NOT EXISTS notes (
                note_id TEXT PRIMARY KEY,
                title TEXT NOT NULL CHECK (length(title) <= 120),
                content_text TEXT NOT NULL CHECK (length(content_text) <= 200000),
                is_pinned INTEGER NOT NULL DEFAULT 0 CHECK (is_pinned IN (0, 1)),
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_notes_updated_at
                ON notes (is_pinned DESC, updated_at_utc DESC, note_id ASC);
            """),
        new SqliteMigration(
            4,
            "clipboard_entries",
            """
            CREATE TABLE IF NOT EXISTS clipboard_entries (
                entry_id TEXT PRIMARY KEY,
                content_type TEXT NOT NULL CHECK (content_type IN ('Text', 'Image')),
                text_content TEXT NULL CHECK (text_content IS NULL OR length(text_content) <= 1048576),
                image_rel_path TEXT NULL,
                content_hash TEXT NOT NULL,
                source_app_name TEXT NULL CHECK (source_app_name IS NULL OR length(source_app_name) <= 128),
                byte_size INTEGER NOT NULL CHECK (byte_size >= 0),
                is_pinned INTEGER NOT NULL DEFAULT 0 CHECK (is_pinned IN (0, 1)),
                copied_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_clipboard_copied_at
                ON clipboard_entries (copied_at_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_clipboard_pinned_copied_at
                ON clipboard_entries (is_pinned DESC, copied_at_utc DESC);
            """),
        new SqliteMigration(
            5,
            "inbox_notifications",
            """
            CREATE TABLE IF NOT EXISTS inbox_notifications (
                notification_id TEXT PRIMARY KEY,
                os_toast_id INTEGER NULL,
                app_id TEXT NOT NULL,
                app_name TEXT NOT NULL CHECK (length(app_name) BETWEEN 1 AND 128),
                app_icon_rel_path TEXT NULL,
                title TEXT NOT NULL CHECK (length(title) <= 512),
                body_text TEXT NOT NULL CHECK (length(body_text) <= 4096),
                is_dismissed INTEGER NOT NULL DEFAULT 0 CHECK (is_dismissed IN (0, 1)),
                received_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_inbox_received_at
                ON inbox_notifications (received_at_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_inbox_os_toast_id
                ON inbox_notifications (os_toast_id);
            """)
    ];

    private readonly string _databasePath;
    private readonly IReadOnlyList<SqliteMigration> _migrations;

    public SqliteMigrationRunner(string databasePath, IReadOnlyList<SqliteMigration>? migrations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _migrations = migrations ?? BuiltInMigrations;

        foreach (var migration in _migrations)
        {
            if (migration.Version <= 0)
            {
                throw new ArgumentException("Migration versions must be positive.", nameof(migrations));
            }

            if (string.IsNullOrWhiteSpace(migration.Sql))
            {
                throw new ArgumentException("Migration SQL is required.", nameof(migrations));
            }
        }

        if (_migrations.Select(migration => migration.Version).Distinct().Count() != _migrations.Count)
        {
            throw new ArgumentException("Migration versions must be unique.", nameof(migrations));
        }
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The database path must include a directory.");
        }

        EnsureUserOnlyDirectory(directory);

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());

        await connection.OpenAsync(ct).ConfigureAwait(false);

        await ExecuteScalarAsync(connection, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;", ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            connection,
            "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at_utc TEXT NOT NULL);",
            ct).ConfigureAwait(false);

        var appliedVersions = await GetAppliedVersionsAsync(connection, ct).ConfigureAwait(false);
        foreach (var migration in _migrations.OrderBy(migration => migration.Version))
        {
            ct.ThrowIfCancellationRequested();
            if (appliedVersions.Contains(migration.Version))
            {
                continue;
            }

            using var transaction = connection.BeginTransaction(deferred: false);
            await ExecuteNonQueryAsync(connection, migration.Sql, ct, transaction).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO schema_migrations (version, applied_at_utc) VALUES ($version, $applied_at_utc);";
                command.Parameters.AddWithValue("$version", migration.Version);
                command.Parameters.AddWithValue(
                    "$applied_at_utc",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            transaction.Commit();
        }
    }

    private static async Task<HashSet<int>> GetAppliedVersionsAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        var versions = new HashSet<int>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static void EnsureUserOnlyDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User;
        if (user is null)
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        new DirectoryInfo(directory).SetAccessControl(security);
    }
}
