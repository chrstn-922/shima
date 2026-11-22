using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Shima.Core.Contracts;

namespace Shima.Infrastructure.Repositories;

/// <summary>
/// Parameterized SQLite repository persisting Windows toast notifications in inbox_notifications
/// and cached app icons under %LOCALAPPDATA%\Shima\cache\icons\.
/// </summary>
public sealed class InboxRepository : IInboxRepository
{
    private readonly string _connectionString;
    private readonly string _dataRoot;

    public InboxRepository(string databasePath, string? dataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullDbPath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        _dataRoot = ResolveDataRoot(fullDbPath, dataRoot);
    }

    public async Task<IReadOnlyList<InboxAppGroup>> GetGroupedByAppAsync(
        bool includeDismissed = false,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = includeDismissed
            ? """
              SELECT notification_id, os_toast_id, app_id, app_name, app_icon_rel_path, title, body_text, is_dismissed, received_at_utc
              FROM inbox_notifications
              ORDER BY received_at_utc DESC, rowid DESC;
              """
            : """
              SELECT notification_id, os_toast_id, app_id, app_name, app_icon_rel_path, title, body_text, is_dismissed, received_at_utc
              FROM inbox_notifications
              WHERE is_dismissed = 0
              ORDER BY received_at_utc DESC, rowid DESC;
              """;

        var groups = new Dictionary<string, (string AppId, string AppName, string? IconPath, List<InboxNotificationItem> Items)>(
            StringComparer.OrdinalIgnoreCase);
        var orderedKeys = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var item = ReadItem(reader);
            if (!groups.TryGetValue(item.AppId, out var group))
            {
                group = (item.AppId, item.AppName, item.AppIconRelPath, new List<InboxNotificationItem>());
                groups[item.AppId] = group;
                orderedKeys.Add(item.AppId);
            }
            else if (string.IsNullOrWhiteSpace(group.IconPath) && !string.IsNullOrWhiteSpace(item.AppIconRelPath))
            {
                group = (group.AppId, group.AppName, item.AppIconRelPath, group.Items);
                groups[item.AppId] = group;
            }

            group.Items.Add(item);
        }

        return orderedKeys
            .Select(key =>
            {
                var g = groups[key];
                return new InboxAppGroup(g.AppId, g.AppName, g.IconPath, g.Items);
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<InboxNotificationItem>> GetAllActiveAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT notification_id, os_toast_id, app_id, app_name, app_icon_rel_path, title, body_text, is_dismissed, received_at_utc
            FROM inbox_notifications
            WHERE is_dismissed = 0
            ORDER BY received_at_utc DESC, rowid DESC;
            """;

        var items = new List<InboxNotificationItem>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public async Task<InboxNotificationItem> UpsertToastAsync(
        InboxNotificationItem item,
        CancellationToken ct = default)
    {
        var normalized = ValidateAndNormalize(item);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        if (normalized.OsToastId.HasValue)
        {
            await using var existingCmd = connection.CreateCommand();
            existingCmd.Transaction = transaction;
            existingCmd.CommandText =
                """
                SELECT notification_id
                FROM inbox_notifications
                WHERE os_toast_id = $os_toast_id AND is_dismissed = 0
                LIMIT 1;
                """;
            existingCmd.Parameters.AddWithValue("$os_toast_id", (long)normalized.OsToastId.Value);
            if (await existingCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string existingId
                && !string.IsNullOrWhiteSpace(existingId))
            {
                normalized = normalized with { NotificationId = existingId };
            }
        }

        await using (var upsertCmd = connection.CreateCommand())
        {
            upsertCmd.Transaction = transaction;
            upsertCmd.CommandText =
                """
                INSERT INTO inbox_notifications (
                    notification_id, os_toast_id, app_id, app_name, app_icon_rel_path, title, body_text, is_dismissed, received_at_utc
                )
                VALUES (
                    $notification_id, $os_toast_id, $app_id, $app_name, $app_icon_rel_path, $title, $body_text, $is_dismissed, $received_at_utc
                )
                ON CONFLICT(notification_id) DO UPDATE SET
                    os_toast_id = excluded.os_toast_id,
                    app_id = excluded.app_id,
                    app_name = excluded.app_name,
                    app_icon_rel_path = COALESCE(excluded.app_icon_rel_path, inbox_notifications.app_icon_rel_path),
                    title = excluded.title,
                    body_text = excluded.body_text,
                    is_dismissed = excluded.is_dismissed,
                    received_at_utc = excluded.received_at_utc;
                """;
            upsertCmd.Parameters.AddWithValue("$notification_id", normalized.NotificationId);
            upsertCmd.Parameters.AddWithValue(
                "$os_toast_id",
                normalized.OsToastId.HasValue ? (long)normalized.OsToastId.Value : DBNull.Value);
            upsertCmd.Parameters.AddWithValue("$app_id", normalized.AppId);
            upsertCmd.Parameters.AddWithValue("$app_name", normalized.AppName);
            upsertCmd.Parameters.AddWithValue("$app_icon_rel_path", (object?)normalized.AppIconRelPath ?? DBNull.Value);
            upsertCmd.Parameters.AddWithValue("$title", normalized.Title);
            upsertCmd.Parameters.AddWithValue("$body_text", normalized.BodyText);
            upsertCmd.Parameters.AddWithValue("$is_dismissed", normalized.IsDismissed ? 1 : 0);
            upsertCmd.Parameters.AddWithValue(
                "$received_at_utc",
                normalized.ReceivedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            await upsertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await EnforceRowCapCoreAsync(connection, transaction, InboxNotificationItem.MaxStoredItems, ct)
            .ConfigureAwait(false);
        transaction.Commit();

        return normalized;
    }

    public async Task<InboxNotificationItem?> FindByOsToastIdAsync(uint osToastId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT notification_id, os_toast_id, app_id, app_name, app_icon_rel_path, title, body_text, is_dismissed, received_at_utc
            FROM inbox_notifications
            WHERE os_toast_id = $os_toast_id
            ORDER BY is_dismissed ASC, received_at_utc DESC, rowid DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$os_toast_id", (long)osToastId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadItem(reader) : null;
    }

    public async Task MarkDismissedAsync(string notificationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE inbox_notifications SET is_dismissed = 1 WHERE notification_id = $notification_id;";
        command.Parameters.AddWithValue("$notification_id", notificationId.Trim());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkDismissedByOsToastIdAsync(uint osToastId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE inbox_notifications SET is_dismissed = 1 WHERE os_toast_id = $os_toast_id;";
        command.Parameters.AddWithValue("$os_toast_id", (long)osToastId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<uint>> ClearAppGroupAsync(string appId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var trimmedAppId = appId.Trim();
        var clearedToastIds = new List<uint>();

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        await using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.Transaction = transaction;
            selectCmd.CommandText =
                """
                SELECT DISTINCT os_toast_id
                FROM inbox_notifications
                WHERE app_id = $app_id AND is_dismissed = 0 AND os_toast_id IS NOT NULL;
                """;
            selectCmd.Parameters.AddWithValue("$app_id", trimmedAppId);
            await using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var raw = reader.GetInt64(0);
                if (raw >= 0 && raw <= uint.MaxValue)
                {
                    clearedToastIds.Add((uint)raw);
                }
            }
        }

        await using (var updateCmd = connection.CreateCommand())
        {
            updateCmd.Transaction = transaction;
            updateCmd.CommandText =
                "UPDATE inbox_notifications SET is_dismissed = 1 WHERE app_id = $app_id AND is_dismissed = 0;";
            updateCmd.Parameters.AddWithValue("$app_id", trimmedAppId);
            await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
        return clearedToastIds;
    }

    public async Task<IReadOnlyList<uint>> ClearAllAsync(CancellationToken ct = default)
    {
        var clearedToastIds = new List<uint>();

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        await using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.Transaction = transaction;
            selectCmd.CommandText =
                """
                SELECT DISTINCT os_toast_id
                FROM inbox_notifications
                WHERE is_dismissed = 0 AND os_toast_id IS NOT NULL;
                """;
            await using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var raw = reader.GetInt64(0);
                if (raw >= 0 && raw <= uint.MaxValue)
                {
                    clearedToastIds.Add((uint)raw);
                }
            }
        }

        await using (var updateCmd = connection.CreateCommand())
        {
            updateCmd.Transaction = transaction;
            updateCmd.CommandText = "UPDATE inbox_notifications SET is_dismissed = 1 WHERE is_dismissed = 0;";
            await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
        return clearedToastIds;
    }

    public async Task<int> PruneExpiredAsync(
        int retentionDays = 14,
        int maxStoredItems = InboxNotificationItem.MaxStoredItems,
        DateTimeOffset? utcNow = null,
        CancellationToken ct = default)
    {
        var clampedDays = Math.Clamp(retentionDays, 1, 365);
        var clampedMax = Math.Clamp(maxStoredItems, 1, InboxNotificationItem.MaxStoredItems);
        var cutoff = (utcNow ?? DateTimeOffset.UtcNow)
            .ToUniversalTime()
            .AddDays(-clampedDays)
            .ToString("O", CultureInfo.InvariantCulture);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        int removedByAge;
        await using (var ageCmd = connection.CreateCommand())
        {
            ageCmd.Transaction = transaction;
            ageCmd.CommandText = "DELETE FROM inbox_notifications WHERE received_at_utc < $cutoff;";
            ageCmd.Parameters.AddWithValue("$cutoff", cutoff);
            removedByAge = await ageCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var removedByCap = await EnforceRowCapCoreAsync(connection, transaction, clampedMax, ct).ConfigureAwait(false);
        transaction.Commit();
        return removedByAge + removedByCap;
    }

    public async Task<string?> SaveAppIconAssetAsync(string appId, byte[] pngBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(pngBytes);

        if (pngBytes.Length == 0 || pngBytes.Length > 2 * 1024 * 1024)
        {
            return null;
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(appId.Trim().ToLowerInvariant()));
        var hashPrefix = Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
        var relativePath = Path.Combine("cache", "icons", $"{hashPrefix}.png");
        var fullPath = ResolveIconFullPath(relativePath);
        if (string.IsNullOrEmpty(fullPath))
        {
            return null;
        }

        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        await File.WriteAllBytesAsync(fullPath, pngBytes, ct).ConfigureAwait(false);
        return relativePath;
    }

    public string? ResolveIconFullPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_dataRoot));
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Trim()));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    private static async Task<int> EnforceRowCapCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int maxStoredItems,
        CancellationToken ct)
    {
        await using var capCmd = connection.CreateCommand();
        capCmd.Transaction = transaction;
        capCmd.CommandText =
            """
            DELETE FROM inbox_notifications
            WHERE notification_id NOT IN (
                SELECT notification_id
                FROM inbox_notifications
                ORDER BY received_at_utc DESC, rowid DESC
                LIMIT $max_items
            );
            """;
        capCmd.Parameters.AddWithValue("$max_items", maxStoredItems);
        return await capCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static InboxNotificationItem ValidateAndNormalize(InboxNotificationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.NotificationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.AppId);

        var rawAppName = string.IsNullOrWhiteSpace(item.AppName) ? "Unknown App" : item.AppName.Trim();
        var appName = rawAppName.Length > InboxNotificationItem.MaxAppNameLength
            ? rawAppName[..InboxNotificationItem.MaxAppNameLength]
            : rawAppName;

        var rawTitle = (item.Title ?? string.Empty).Trim();
        var title = rawTitle.Length > InboxNotificationItem.MaxTitleLength
            ? rawTitle[..InboxNotificationItem.MaxTitleLength]
            : rawTitle;

        var rawBody = item.BodyText ?? string.Empty;
        var body = rawBody.Length > InboxNotificationItem.MaxBodyLength
            ? rawBody[..InboxNotificationItem.MaxBodyLength]
            : rawBody;

        var receivedAt = item.ReceivedAtUtc == default ? DateTimeOffset.UtcNow : item.ReceivedAtUtc.ToUniversalTime();

        return item with
        {
            NotificationId = item.NotificationId.Trim(),
            AppId = item.AppId.Trim(),
            AppName = appName,
            Title = title,
            BodyText = body,
            ReceivedAtUtc = receivedAt
        };
    }

    private static InboxNotificationItem ReadItem(SqliteDataReader reader) => new()
    {
        NotificationId = reader.GetString(0),
        OsToastId = reader.IsDBNull(1) ? null : (uint)reader.GetInt64(1),
        AppId = reader.GetString(2),
        AppName = reader.GetString(3),
        AppIconRelPath = reader.IsDBNull(4) ? null : reader.GetString(4),
        Title = reader.GetString(5),
        BodyText = reader.GetString(6),
        IsDismissed = reader.GetInt64(7) != 0,
        ReceivedAtUtc = DateTimeOffset.Parse(
            reader.GetString(8),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind)
    };

    private static string ResolveDataRoot(string fullDbPath, string? dataRoot)
    {
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            return Path.GetFullPath(dataRoot);
        }

        var dbDir = Path.GetDirectoryName(fullDbPath)
            ?? throw new InvalidOperationException("Database path must include a directory.");
        return string.Equals(Path.GetFileName(dbDir), "data", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dbDir) ?? dbDir
            : dbDir;
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
