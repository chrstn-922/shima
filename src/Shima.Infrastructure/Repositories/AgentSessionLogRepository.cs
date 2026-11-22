using System.Globalization;
using Microsoft.Data.Sqlite;
using Shima.Core.Contracts;

namespace Shima.Infrastructure.Repositories;

/// <summary>
/// Parameterized SQLite WAL repository managing active and recently finished coding-agent session logs.
/// Keeps active sessions alongside up to 50 most recent finished sessions per data-model.md.
/// </summary>
public sealed class AgentSessionLogRepository : IAgentSessionLogRepository
{
    private readonly string _connectionString;

    public AgentSessionLogRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task UpsertAsync(AgentSessionLogEntry entry, CancellationToken ct = default)
    {
        var normalized = ValidateAndNormalize(entry);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO agent_session_logs (
                session_id, adapter_type, workspace_path, process_id, last_status, started_at_utc, last_seen_at_utc
            )
            VALUES (
                $session_id, $adapter_type, $workspace_path, $process_id, $last_status, $started_at_utc, $last_seen_at_utc
            )
            ON CONFLICT(session_id) DO UPDATE SET
                adapter_type = excluded.adapter_type,
                workspace_path = excluded.workspace_path,
                process_id = COALESCE(excluded.process_id, agent_session_logs.process_id),
                last_status = excluded.last_status,
                last_seen_at_utc = excluded.last_seen_at_utc;
            """;
        command.Parameters.AddWithValue("$session_id", normalized.SessionId);
        command.Parameters.AddWithValue("$adapter_type", normalized.AdapterType);
        command.Parameters.AddWithValue("$workspace_path", normalized.WorkspacePath);
        command.Parameters.AddWithValue("$process_id", normalized.ProcessId.HasValue ? normalized.ProcessId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$last_status", normalized.LastStatus.ToString());
        command.Parameters.AddWithValue("$started_at_utc", normalized.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$last_seen_at_utc", normalized.LastSeenAtUtc.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AgentSessionLogEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        var bounded = Math.Clamp(limit, 1, 200);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id, adapter_type, workspace_path, process_id, last_status, started_at_utc, last_seen_at_utc
            FROM agent_session_logs
            ORDER BY last_seen_at_utc DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", bounded);

        var rows = new List<AgentSessionLogEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new AgentSessionLogEntry
            {
                SessionId = reader.GetString(0),
                AdapterType = reader.GetString(1),
                WorkspacePath = reader.GetString(2),
                ProcessId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                LastStatus = Enum.Parse<AgentStatus>(reader.GetString(4), ignoreCase: false),
                StartedAtUtc = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                LastSeenAtUtc = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            });
        }

        return rows;
    }

    public async Task<int> PruneFinishedSessionsAsync(int keepFinishedCount = 50, CancellationToken ct = default)
    {
        var keep = Math.Clamp(keepFinishedCount, 0, 500);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM agent_session_logs
            WHERE last_status = 'Finished'
              AND session_id NOT IN (
                  SELECT session_id
                  FROM agent_session_logs
                  WHERE last_status = 'Finished'
                  ORDER BY last_seen_at_utc DESC, rowid DESC
                  LIMIT $keep
              );
            """;
        command.Parameters.AddWithValue("$keep", keep);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_session_logs;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static AgentSessionLogEntry ValidateAndNormalize(AgentSessionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SessionId);

        if (!Enum.IsDefined(entry.LastStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "Unsupported agent session status.");
        }

        var normalizedAdapter = entry.AdapterType?.Trim() switch
        {
            "ClaudeCode" or "claude-code" => "ClaudeCode",
            "OpenCode" or "opencode" => "OpenCode",
            _ => throw new ArgumentException("AdapterType must be 'ClaudeCode' or 'OpenCode'.", nameof(entry))
        };

        var startedAt = entry.StartedAtUtc == default ? DateTimeOffset.UtcNow : entry.StartedAtUtc.ToUniversalTime();
        var lastSeenAt = entry.LastSeenAtUtc == default ? startedAt : entry.LastSeenAtUtc.ToUniversalTime();

        return entry with
        {
            SessionId = entry.SessionId.Trim(),
            AdapterType = normalizedAdapter,
            WorkspacePath = entry.WorkspacePath ?? string.Empty,
            StartedAtUtc = startedAt,
            LastSeenAtUtc = lastSeenAt
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
