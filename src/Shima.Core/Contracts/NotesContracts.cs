namespace Shima.Core.Contracts;

/// <summary>
/// Persisted local scratchpad note stored in SQLite notes per data-model.md.
/// </summary>
public sealed record NoteEntry
{
    public const int MaxTitleLength = 120;
    public const int MaxContentLength = 200_000;

    public required string NoteId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string ContentText { get; init; } = string.Empty;

    public bool IsPinned { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public static string DeriveTitle(string? explicitTitle, string? contentText)
    {
        if (!string.IsNullOrWhiteSpace(explicitTitle))
        {
            var trimmed = explicitTitle.Trim();
            return trimmed.Length > MaxTitleLength ? trimmed[..MaxTitleLength] : trimmed;
        }

        if (!string.IsNullOrEmpty(contentText))
        {
            using var reader = new StringReader(contentText);
            while (reader.ReadLine() is { } line)
            {
                var trimmedLine = line.Trim();
                if (!string.IsNullOrEmpty(trimmedLine))
                {
                    return trimmedLine.Length > MaxTitleLength
                        ? trimmedLine[..MaxTitleLength]
                        : trimmedLine;
                }
            }
        }

        return "Untitled note";
    }
}

/// <summary>
/// Local SQLite repository managing quick notes, pinning, and keyword search.
/// </summary>
public interface INoteRepository
{
    Task<IReadOnlyList<NoteEntry>> GetAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<NoteEntry>> SearchAsync(string query, CancellationToken ct = default);

    Task<NoteEntry> UpsertAsync(NoteEntry note, CancellationToken ct = default);

    Task SetPinnedAsync(string noteId, bool isPinned, CancellationToken ct = default);

    Task DeleteAsync(string noteId, CancellationToken ct = default);
}
