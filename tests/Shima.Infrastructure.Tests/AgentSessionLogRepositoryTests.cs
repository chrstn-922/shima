using FluentAssertions;
using Shima.Core.Contracts;
using Shima.Infrastructure.Persistence;
using Shima.Infrastructure.Repositories;
using Xunit;

namespace Shima.Infrastructure.Tests;

public sealed class AgentSessionLogRepositoryTests : TempDirectoryTest
{
    [Fact]
    public async Task UpsertGetRecentAndPruneFinished_PreservesActiveAndLatest50FinishedRows()
    {
        var dbPath = Path.Combine(Root, "data", "shima.db");
        await new SqliteMigrationRunner(dbPath).InitializeAsync();
        var repository = new AgentSessionLogRepository(dbPath);
        var baseTime = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

        await repository.UpsertAsync(new AgentSessionLogEntry
        {
            SessionId = "claude-code:active-1",
            AdapterType = "ClaudeCode",
            WorkspacePath = @"C:\repos\shima",
            ProcessId = 4242,
            LastStatus = AgentStatus.Working,
            StartedAtUtc = baseTime,
            LastSeenAtUtc = baseTime
        });

        await repository.UpsertAsync(new AgentSessionLogEntry
        {
            SessionId = "opencode:active-2",
            AdapterType = "opencode",
            WorkspacePath = @"C:\repos\api",
            ProcessId = null,
            LastStatus = AgentStatus.AwaitingApproval,
            StartedAtUtc = baseTime.AddSeconds(1),
            LastSeenAtUtc = baseTime.AddSeconds(2)
        });

        // Insert 55 finished rows -> PruneFinishedSessionsAsync(50) keeps active rows + latest 50 finished rows
        for (var i = 0; i < 55; i++)
        {
            await repository.UpsertAsync(new AgentSessionLogEntry
            {
                SessionId = $"opencode:fin-{i:D2}",
                AdapterType = "OpenCode",
                WorkspacePath = $@"C:\repos\fin-{i}",
                ProcessId = null,
                LastStatus = AgentStatus.Finished,
                StartedAtUtc = baseTime.AddMinutes(i + 1),
                LastSeenAtUtc = baseTime.AddMinutes(i + 1)
            });
        }

        var deleted = await repository.PruneFinishedSessionsAsync(keepFinishedCount: 50);
        deleted.Should().Be(5);

        var rows = await repository.GetRecentAsync(100);
        rows.Should().HaveCount(52);
        rows.Should().Contain(r => r.SessionId == "claude-code:active-1" && r.ProcessId == 4242);
        rows.Should().Contain(r => r.SessionId == "opencode:active-2" && r.AdapterType == "OpenCode");
        rows.Should().NotContain(r => r.SessionId == "opencode:fin-00");
        rows.Should().Contain(r => r.SessionId == "opencode:fin-54");

        // Update existing session preserving process_id when omitted
        await repository.UpsertAsync(new AgentSessionLogEntry
        {
            SessionId = "claude-code:active-1",
            AdapterType = "claude-code",
            WorkspacePath = @"C:\repos\shima",
            ProcessId = null,
            LastStatus = AgentStatus.Finished,
            StartedAtUtc = baseTime,
            LastSeenAtUtc = baseTime.AddHours(1)
        });

        var updated = (await repository.GetRecentAsync(100)).Single(r => r.SessionId == "claude-code:active-1");
        updated.LastStatus.Should().Be(AgentStatus.Finished);
        updated.ProcessId.Should().Be(4242);

        await repository.ClearAsync();
        (await repository.GetRecentAsync(50)).Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_WithInvalidAdapterType_ThrowsArgumentException()
    {
        var dbPath = Path.Combine(Root, "data", "shima.db");
        await new SqliteMigrationRunner(dbPath).InitializeAsync();
        var repository = new AgentSessionLogRepository(dbPath);

        var entry = new AgentSessionLogEntry
        {
            SessionId = "bad:1",
            AdapterType = "UnknownAgent",
            WorkspacePath = @"C:\repos\x",
            LastStatus = AgentStatus.Working,
            StartedAtUtc = DateTimeOffset.UtcNow,
            LastSeenAtUtc = DateTimeOffset.UtcNow
        };

        await FluentActions.Invoking(() => repository.UpsertAsync(entry))
            .Should().ThrowAsync<ArgumentException>();
    }
}
