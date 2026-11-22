using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Shima.Core.Contracts;

namespace Shima.Infrastructure.Repositories;

/// <summary>
/// Parameterized SQLite repository persisting clipboard history, consecutive hash deduplication,
/// retention pruning, and cached PNG assets under %LOCALAPPDATA%\Shima\cache\clip\.
/// </summary>
public sealed partial class ClipboardRepository : IClipboardRepository
{
    private readonly string _connectionString;
    private readonly string _dataRoot;

    public ClipboardRepository(string databasePath, string? dataRoot = null)
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

    public async Task<IReadOnlyList<ClipboardEntry>> GetHistoryAsync(
        string? query = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 1000);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        if (string.IsNullOrWhiteSpace(query))
        {
            command.CommandText =
                """
                SELECT entry_id, content_type, text_content, image_rel_path, content_hash, source_app_name, byte_size, is_pinned, copied_at_utc
                FROM clipboard_entries
                ORDER BY is_pinned DESC, copied_at_utc DESC, rowid DESC
                LIMIT $limit;
                """;
        }
        else
        {
            var escaped = EscapeLikePattern(query.Trim());
            command.CommandText =
                """
                SELECT entry_id, content_type, text_content, image_rel_path, content_hash, source_app_name, byte_size, is_pinned, copied_at_utc
                FROM clipboard_entries
                WHERE (text_content LIKE $pattern ESCAPE '\' COLLATE NOCASE)
                   OR (source_app_name LIKE $pattern ESCAPE '\' COLLATE NOCASE)
                ORDER BY is_pinned DESC, copied_at_utc DESC, rowid DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$pattern", $"%{escaped}%");
        }

        command.Parameters.AddWithValue("$limit", boundedLimit);

        var items = new List<ClipboardEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(ReadEntry(reader));
        }

        return items;
    }

    public async Task<ClipboardAddResult> TryAddEntryAsync(
        ClipboardEntry entry,
        int maxUnpinnedItems = 100,
        int retentionDays = 30,
        bool forcePrune = false,
        CancellationToken ct = default)
    {
        var normalized = ValidateAndNormalize(entry);
        var clampedMax = Math.Clamp(maxUnpinnedItems, 1, 1000);
        var clampedDays = Math.Clamp(retentionDays, 1, 365);

        var prunedAssetPaths = new List<string>();
        ClipboardAddResult result;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var latest = await GetMostRecentEntryAsync(connection, transaction, ct).ConfigureAwait(false);
            if (latest is not null
                && string.Equals(latest.ContentHash, normalized.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                await using (var touchCmd = connection.CreateCommand())
                {
                    touchCmd.Transaction = transaction;
                    touchCmd.CommandText =
                        "UPDATE clipboard_entries SET copied_at_utc = $copied_at_utc WHERE entry_id = $entry_id;";
                    touchCmd.Parameters.AddWithValue(
                        "$copied_at_utc",
                        normalized.CopiedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                    touchCmd.Parameters.AddWithValue("$entry_id", latest.EntryId);
                    await touchCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                var updatedLatest = latest with { CopiedAtUtc = normalized.CopiedAtUtc };
                var prunedCount = forcePrune
                    ? await PruneCoreAsync(connection, transaction, clampedMax, clampedDays, DateTimeOffset.UtcNow, prunedAssetPaths, ct)
                        .ConfigureAwait(false)
                    : 0;

                transaction.Commit();
                result = new ClipboardAddResult(false, updatedLatest, true, prunedCount);
            }
            else
            {
                await InsertEntryCoreAsync(connection, transaction, normalized, ct).ConfigureAwait(false);
                var prunedCount = await PruneCoreAsync(
                    connection,
                    transaction,
                    clampedMax,
                    clampedDays,
                    DateTimeOffset.UtcNow,
                    prunedAssetPaths,
                    ct).ConfigureAwait(false);

                transaction.Commit();
                result = new ClipboardAddResult(true, normalized, false, prunedCount);
            }
        }

        DeleteCachedFilesBestEffort(prunedAssetPaths);
        return result;
    }

    public async Task SetPinnedAsync(string entryId, bool isPinned, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clipboard_entries SET is_pinned = $is_pinned WHERE entry_id = $entry_id;";
        command.Parameters.AddWithValue("$is_pinned", isPinned ? 1 : 0);
        command.Parameters.AddWithValue("$entry_id", entryId.Trim());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string entryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        var trimmedId = entryId.Trim();
        var pathsToDelete = new List<string>();

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.Transaction = transaction;
                selectCmd.CommandText =
                    "SELECT image_rel_path FROM clipboard_entries WHERE entry_id = $entry_id AND image_rel_path IS NOT NULL;";
                selectCmd.Parameters.AddWithValue("$entry_id", trimmedId);
                await using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0))
                    {
                        pathsToDelete.Add(reader.GetString(0));
                    }
                }
            }

            await using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM clipboard_entries WHERE entry_id = $entry_id;";
                deleteCmd.Parameters.AddWithValue("$entry_id", trimmedId);
                await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            transaction.Commit();
        }

        DeleteCachedFilesBestEffort(pathsToDelete);
    }

    public async Task<int> ClearUnpinnedAsync(CancellationToken ct = default)
    {
        var pathsToDelete = new List<string>();
        int deletedCount;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.Transaction = transaction;
                selectCmd.CommandText =
                    "SELECT image_rel_path FROM clipboard_entries WHERE is_pinned = 0 AND image_rel_path IS NOT NULL;";
                await using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0))
                    {
                        pathsToDelete.Add(reader.GetString(0));
                    }
                }
            }

            await using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM clipboard_entries WHERE is_pinned = 0;";
                deletedCount = await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            transaction.Commit();
        }

        DeleteCachedFilesBestEffort(pathsToDelete);
        return deletedCount;
    }

    public async Task<int> PruneAsync(
        int maxUnpinnedItems,
        int retentionDays,
        DateTimeOffset? utcNow = null,
        CancellationToken ct = default)
    {
        var clampedMax = Math.Clamp(maxUnpinnedItems, 1, 1000);
        var clampedDays = Math.Clamp(retentionDays, 1, 365);
        var now = (utcNow ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var prunedAssetPaths = new List<string>();
        int prunedCount;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            prunedCount = await PruneCoreAsync(
                connection,
                transaction,
                clampedMax,
                clampedDays,
                now,
                prunedAssetPaths,
                ct).ConfigureAwait(false);
            transaction.Commit();
        }

        DeleteCachedFilesBestEffort(prunedAssetPaths);
        return prunedCount;
    }

    public async Task<string?> SaveImageAssetAsync(string entryId, byte[] pngBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        ArgumentNullException.ThrowIfNull(pngBytes);

        if (pngBytes.Length == 0)
        {
            return null;
        }

        if (pngBytes.Length > ClipboardEntry.MaxImageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pngBytes),
                $"Clipboard image payload exceeds {ClipboardEntry.MaxImageBytes} bytes.");
        }

        var safeId = SafeFileNameRegex().Replace(entryId.Trim(), "_");
        if (string.IsNullOrWhiteSpace(safeId))
        {
            safeId = Guid.NewGuid().ToString("N");
        }

        var relativePath = Path.Combine("cache", "clip", $"{safeId}.png");
        var fullPath = ResolveImageFullPath(relativePath)
            ?? throw new InvalidOperationException("Resolved clipboard image path fell outside data root.");

        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        await File.WriteAllBytesAsync(fullPath, pngBytes, ct).ConfigureAwait(false);
        return relativePath;
    }

    public string? ResolveImageFullPath(string? relativePath)
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

    private static async Task<ClipboardEntry?> GetMostRecentEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            SELECT entry_id, content_type, text_content, image_rel_path, content_hash, source_app_name, byte_size, is_pinned, copied_at_utc
            FROM clipboard_entries
            ORDER BY copied_at_utc DESC, rowid DESC
            LIMIT 1;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadEntry(reader) : null;
    }

    private static async Task InsertEntryCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardEntry entry,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            INSERT INTO clipboard_entries (
                entry_id, content_type, text_content, image_rel_path, content_hash, source_app_name, byte_size, is_pinned, copied_at_utc
            )
            VALUES (
                $entry_id, $content_type, $text_content, $image_rel_path, $content_hash, $source_app_name, $byte_size, $is_pinned, $copied_at_utc
            )
            ON CONFLICT(entry_id) DO UPDATE SET
                content_type = excluded.content_type,
                text_content = excluded.text_content,
                image_rel_path = excluded.image_rel_path,
                content_hash = excluded.content_hash,
                source_app_name = excluded.source_app_name,
                byte_size = excluded.byte_size,
                is_pinned = excluded.is_pinned,
                copied_at_utc = excluded.copied_at_utc;
            """;
        cmd.Parameters.AddWithValue("$entry_id", entry.EntryId);
        cmd.Parameters.AddWithValue("$content_type", entry.ContentType.ToString());
        cmd.Parameters.AddWithValue("$text_content", (object?)entry.TextContent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$image_rel_path", (object?)entry.ImageRelPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$content_hash", entry.ContentHash);
        cmd.Parameters.AddWithValue("$source_app_name", (object?)entry.SourceAppName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$byte_size", entry.ByteSize);
        cmd.Parameters.AddWithValue("$is_pinned", entry.IsPinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$copied_at_utc", entry.CopiedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> PruneCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int maxUnpinnedItems,
        int retentionDays,
        DateTimeOffset utcNow,
        List<string> prunedAssetPaths,
        CancellationToken ct)
    {
        var cutoff = utcNow.ToUniversalTime().AddDays(-retentionDays).ToString("O", CultureInfo.InvariantCulture);

        var targetIds = new List<string>();
        await using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.Transaction = transaction;
            selectCmd.CommandText =
                """
                SELECT entry_id, image_rel_path
                FROM clipboard_entries
                WHERE is_pinned = 0
                  AND (
                      copied_at_utc < $cutoff
                      OR entry_id NOT IN (
                          SELECT entry_id
                          FROM clipboard_entries
                          WHERE is_pinned = 0
                            AND copied_at_utc >= $cutoff
                          ORDER BY copied_at_utc DESC, rowid DESC
                          LIMIT $max_items
                      )
                  );
                """;
            selectCmd.Parameters.AddWithValue("$cutoff", cutoff);
            selectCmd.Parameters.AddWithValue("$max_items", maxUnpinnedItems);

            await using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                targetIds.Add(reader.GetString(0));
                if (!reader.IsDBNull(1))
                {
                    prunedAssetPaths.Add(reader.GetString(1));
                }
            }
        }

        if (targetIds.Count == 0)
        {
            return 0;
        }

        await using var deleteCmd = connection.CreateCommand();
        deleteCmd.Transaction = transaction;
        var paramNames = new string[targetIds.Count];
        for (var i = 0; i < targetIds.Count; i++)
        {
            var name = $"$id{i}";
            paramNames[i] = name;
            deleteCmd.Parameters.AddWithValue(name, targetIds[i]);
        }

        deleteCmd.CommandText = $"DELETE FROM clipboard_entries WHERE entry_id IN ({string.Join(", ", paramNames)});";
        return await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private void DeleteCachedFilesBestEffort(IEnumerable<string> relativePaths)
    {
        foreach (var relative in relativePaths)
        {
            var fullPath = ResolveImageFullPath(relative);
            if (string.IsNullOrEmpty(fullPath))
            {
                continue;
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static ClipboardEntry ValidateAndNormalize(ClipboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.EntryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ContentHash);

        if (entry.ByteSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "ByteSize must be non-negative.");
        }

        if (entry.ContentType == ClipboardContentType.Text)
        {
            var text = entry.TextContent ?? string.Empty;
            if (text.Length > ClipboardEntry.MaxTextLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(entry),
                    $"Clipboard text exceeds {ClipboardEntry.MaxTextLength} characters.");
            }
        }
        else if (entry.ContentType == ClipboardContentType.Image)
        {
            if (entry.ByteSize > ClipboardEntry.MaxImageBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(entry),
                    $"Clipboard image exceeds {ClipboardEntry.MaxImageBytes} bytes.");
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "Unsupported clipboard content type.");
        }

        string? sourceApp = null;
        if (!string.IsNullOrWhiteSpace(entry.SourceAppName))
        {
            var trimmedApp = entry.SourceAppName.Trim();
            sourceApp = trimmedApp.Length > ClipboardEntry.MaxSourceAppNameLength
                ? trimmedApp[..ClipboardEntry.MaxSourceAppNameLength]
                : trimmedApp;
        }

        var copiedAt = entry.CopiedAtUtc == default ? DateTimeOffset.UtcNow : entry.CopiedAtUtc.ToUniversalTime();

        return entry with
        {
            EntryId = entry.EntryId.Trim(),
            ContentHash = entry.ContentHash.Trim().ToLowerInvariant(),
            SourceAppName = sourceApp,
            CopiedAtUtc = copiedAt
        };
    }

    private static ClipboardEntry ReadEntry(SqliteDataReader reader) => new()
    {
        EntryId = reader.GetString(0),
        ContentType = Enum.Parse<ClipboardContentType>(reader.GetString(1), ignoreCase: false),
        TextContent = reader.IsDBNull(2) ? null : reader.GetString(2),
        ImageRelPath = reader.IsDBNull(3) ? null : reader.GetString(3),
        ContentHash = reader.GetString(4),
        SourceAppName = reader.IsDBNull(5) ? null : reader.GetString(5),
        ByteSize = reader.GetInt32(6),
        IsPinned = reader.GetInt64(7) != 0,
        CopiedAtUtc = DateTimeOffset.Parse(
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

    private static string EscapeLikePattern(string query) =>
        query
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

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

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex SafeFileNameRegex();
}
