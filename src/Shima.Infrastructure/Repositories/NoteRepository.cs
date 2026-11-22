using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Shima.Core.Contracts;

namespace Shima.Infrastructure.Repositories;

/// <summary>
/// Parameterized SQLite repository persisting scratchpad notes in notes per data-model.md.
/// </summary>
public sealed class NoteRepository : INoteRepository
{
    private readonly string _connectionString;

    public NoteRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task<IReadOnlyList<NoteEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT note_id, title, content_text, is_pinned, created_at_utc, updated_at_utc
            FROM notes
            ORDER BY is_pinned DESC, updated_at_utc DESC, note_id ASC;
            """;

        return await ReadNotesAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NoteEntry>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return await GetAllAsync(ct).ConfigureAwait(false);
        }

        var escaped = EscapeLikePattern(query.Trim());
        var pattern = $"%{escaped}%";

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT note_id, title, content_text, is_pinned, created_at_utc, updated_at_utc
            FROM notes
            WHERE title LIKE $pattern ESCAPE '\' COLLATE NOCASE
               OR content_text LIKE $pattern ESCAPE '\' COLLATE NOCASE
            ORDER BY is_pinned DESC, updated_at_utc DESC, note_id ASC;
            """;
        command.Parameters.AddWithValue("$pattern", pattern);

        return await ReadNotesAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<NoteEntry> UpsertAsync(NoteEntry note, CancellationToken ct = default)
    {
        var validated = ValidateAndNormalize(note);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO notes (note_id, title, content_text, is_pinned, created_at_utc, updated_at_utc)
            VALUES ($note_id, $title, $content_text, $is_pinned, $created_at_utc, $updated_at_utc)
            ON CONFLICT(note_id) DO UPDATE SET
                title = excluded.title,
                content_text = excluded.content_text,
                is_pinned = excluded.is_pinned,
                updated_at_utc = excluded.updated_at_utc
            RETURNING created_at_utc;
            """;
        command.Parameters.AddWithValue("$note_id", validated.NoteId);
        command.Parameters.AddWithValue("$title", validated.Title);
        command.Parameters.AddWithValue("$content_text", validated.ContentText);
        command.Parameters.AddWithValue("$is_pinned", validated.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue(
            "$created_at_utc",
            validated.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$updated_at_utc",
            validated.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        var createdRaw = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        var persistedCreatedAt = !string.IsNullOrEmpty(createdRaw)
            && DateTimeOffset.TryParse(createdRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : validated.CreatedAtUtc;

        return validated with { CreatedAtUtc = persistedCreatedAt };
    }

    public async Task SetPinnedAsync(string noteId, bool isPinned, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE notes SET is_pinned = $is_pinned WHERE note_id = $note_id;";
        command.Parameters.AddWithValue("$is_pinned", isPinned ? 1 : 0);
        command.Parameters.AddWithValue("$note_id", noteId.Trim());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string noteId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notes WHERE note_id = $note_id;";
        command.Parameters.AddWithValue("$note_id", noteId.Trim());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static NoteEntry ValidateAndNormalize(NoteEntry note)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentException.ThrowIfNullOrWhiteSpace(note.NoteId);

        var content = note.ContentText ?? string.Empty;
        if (content.Length > NoteEntry.MaxContentLength
            || Encoding.UTF8.GetByteCount(content) > NoteEntry.MaxContentLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(note),
                $"Note content exceeds the maximum limit of {NoteEntry.MaxContentLength} bytes.");
        }

        var now = DateTimeOffset.UtcNow;
        var updatedAt = note.UpdatedAtUtc == default ? now : note.UpdatedAtUtc.ToUniversalTime();
        var createdAt = note.CreatedAtUtc == default ? updatedAt : note.CreatedAtUtc.ToUniversalTime();
        var title = NoteEntry.DeriveTitle(note.Title, content);

        return note with
        {
            NoteId = note.NoteId.Trim(),
            Title = title,
            ContentText = content,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt
        };
    }

    private static async Task<IReadOnlyList<NoteEntry>> ReadNotesAsync(SqliteCommand command, CancellationToken ct)
    {
        var items = new List<NoteEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new NoteEntry
            {
                NoteId = reader.GetString(0),
                Title = reader.GetString(1),
                ContentText = reader.GetString(2),
                IsPinned = reader.GetInt64(3) != 0,
                CreatedAtUtc = DateTimeOffset.Parse(
                    reader.GetString(4),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                UpdatedAtUtc = DateTimeOffset.Parse(
                    reader.GetString(5),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)
            });
        }

        return items;
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
}
