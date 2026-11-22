using System.Globalization;
using Microsoft.Data.Sqlite;
using Shima.Core.Contracts;

namespace Shima.Infrastructure.Repositories;

/// <summary>
/// Parameterized SQLite WAL repository recording Focus Timer sessions and recent focus statistics.
/// </summary>
public sealed class FocusRepository : IFocusRepository
{
    private readonly string _connectionString;

    public FocusRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task<FocusSession> RecordSessionAsync(FocusSession session, CancellationToken ct = default)
    {
        var normalized = ValidateAndNormalize(session);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO focus_sessions (
                session_id, phase_type, planned_seconds, completed_seconds, zen_noise_type, screen_effect_used, started_at_utc, ended_at_utc
            )
            VALUES (
                $session_id, $phase_type, $planned_seconds, $completed_seconds, $zen_noise_type, $screen_effect_used, $started_at_utc, $ended_at_utc
            )
            ON CONFLICT(session_id) DO UPDATE SET
                phase_type = excluded.phase_type,
                planned_seconds = excluded.planned_seconds,
                completed_seconds = excluded.completed_seconds,
                zen_noise_type = excluded.zen_noise_type,
                screen_effect_used = excluded.screen_effect_used,
                started_at_utc = excluded.started_at_utc,
                ended_at_utc = excluded.ended_at_utc;
            """;
        command.Parameters.AddWithValue("$session_id", normalized.SessionId);
        command.Parameters.AddWithValue("$phase_type", normalized.PhaseType.ToString());
        command.Parameters.AddWithValue("$planned_seconds", normalized.PlannedSeconds);
        command.Parameters.AddWithValue("$completed_seconds", normalized.CompletedSeconds);
        command.Parameters.AddWithValue("$zen_noise_type", normalized.ZenNoiseType.ToString());
        command.Parameters.AddWithValue("$screen_effect_used", normalized.ScreenEffectUsed ? 1 : 0);
        command.Parameters.AddWithValue("$started_at_utc", normalized.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$ended_at_utc",
            normalized.EndedAtUtc.HasValue
                ? normalized.EndedAtUtc.Value.ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return normalized;
    }

    public async Task<FocusStatsSummary> GetRecentStatsAsync(int days = 7, CancellationToken ct = default)
    {
        var clampedDays = Math.Clamp(days, 1, 365);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-clampedDays).ToString("O", CultureInfo.InvariantCulture);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COUNT(*) AS session_count,
                COALESCE(SUM(completed_seconds), 0) AS total_seconds,
                MAX(COALESCE(ended_at_utc, started_at_utc)) AS last_at_utc
            FROM focus_sessions
            WHERE phase_type = 'Work'
              AND completed_seconds > 0
              AND started_at_utc >= $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new FocusStatsSummary(0, 0, null);
        }

        var count = reader.GetInt32(0);
        var totalSeconds = reader.GetInt32(1);
        DateTimeOffset? lastAt = reader.IsDBNull(2)
            ? null
            : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        return new FocusStatsSummary(count, totalSeconds, lastAt);
    }

    public async Task<IReadOnlyList<FocusSession>> GetRecentSessionsAsync(int limit = 20, CancellationToken ct = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 200);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id, phase_type, planned_seconds, completed_seconds, zen_noise_type, screen_effect_used, started_at_utc, ended_at_utc
            FROM focus_sessions
            ORDER BY started_at_utc DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", boundedLimit);

        var items = new List<FocusSession>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new FocusSession
            {
                SessionId = reader.GetString(0),
                PhaseType = Enum.Parse<FocusPhaseType>(reader.GetString(1), ignoreCase: false),
                PlannedSeconds = reader.GetInt32(2),
                CompletedSeconds = reader.GetInt32(3),
                ZenNoiseType = Enum.Parse<ZenNoiseType>(reader.GetString(4), ignoreCase: false),
                ScreenEffectUsed = reader.GetInt64(5) != 0,
                StartedAtUtc = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                EndedAtUtc = reader.IsDBNull(7)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            });
        }

        return items;
    }

    private static FocusSession ValidateAndNormalize(FocusSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.SessionId);

        if (!Enum.IsDefined(session.PhaseType))
        {
            throw new ArgumentOutOfRangeException(nameof(session), "Unsupported focus phase type.");
        }

        if (!Enum.IsDefined(session.ZenNoiseType))
        {
            throw new ArgumentOutOfRangeException(nameof(session), "Unsupported Zen noise type.");
        }

        if (session.PlannedSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(session), "PlannedSeconds must be positive.");
        }

        if (session.CompletedSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(session), "CompletedSeconds must be non-negative.");
        }

        var startedAt = session.StartedAtUtc == default ? DateTimeOffset.UtcNow : session.StartedAtUtc.ToUniversalTime();
        var endedAt = session.EndedAtUtc?.ToUniversalTime();

        return session with
        {
            SessionId = session.SessionId.Trim(),
            StartedAtUtc = startedAt,
            EndedAtUtc = endedAt
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
