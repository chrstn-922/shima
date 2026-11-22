using FluentAssertions;
using Shima.Core.Contracts;
using Shima.Infrastructure.Persistence;
using Shima.Infrastructure.Repositories;
using Xunit;

namespace Shima.Infrastructure.Tests;

public sealed class FocusRepositoryTests : TempDirectoryTest
{
    [Fact]
    public async Task FocusRepository_RecordsSessionsAggregatesRecentStats_AndValidatesBounds()
    {
        var dbPath = Path.Combine(Root, "data", "shima.db");
        await new SqliteMigrationRunner(dbPath).InitializeAsync();
        var repo = new FocusRepository(dbPath);
        var now = DateTimeOffset.UtcNow;

        await repo.RecordSessionAsync(new FocusSession
        {
            SessionId = "s-work-1",
            PhaseType = FocusPhaseType.Work,
            PlannedSeconds = 1500,
            CompletedSeconds = 1500,
            ZenNoiseType = ZenNoiseType.Brown,
            ScreenEffectUsed = true,
            StartedAtUtc = now.AddMinutes(-50),
            EndedAtUtc = now.AddMinutes(-25)
        });

        // Short breaks and 0-second entries must NOT inflate completed work stats
        await repo.RecordSessionAsync(new FocusSession
        {
            SessionId = "s-break-1",
            PhaseType = FocusPhaseType.ShortBreak,
            PlannedSeconds = 300,
            CompletedSeconds = 300,
            ZenNoiseType = ZenNoiseType.Pink,
            ScreenEffectUsed = false,
            StartedAtUtc = now.AddMinutes(-25),
            EndedAtUtc = now.AddMinutes(-20)
        });

        await repo.RecordSessionAsync(new FocusSession
        {
            SessionId = "s-old-work",
            PhaseType = FocusPhaseType.Work,
            PlannedSeconds = 1500,
            CompletedSeconds = 1500,
            ZenNoiseType = ZenNoiseType.White,
            ScreenEffectUsed = false,
            StartedAtUtc = now.AddDays(-20),
            EndedAtUtc = now.AddDays(-20).AddMinutes(25)
        });

        await repo.RecordSessionAsync(new FocusSession
        {
            SessionId = "s-work-2",
            PhaseType = FocusPhaseType.Work,
            PlannedSeconds = 1500,
            CompletedSeconds = 600,
            ZenNoiseType = ZenNoiseType.None,
            ScreenEffectUsed = false,
            StartedAtUtc = now.AddMinutes(-15),
            EndedAtUtc = now.AddMinutes(-5)
        });

        var stats7 = await repo.GetRecentStatsAsync(days: 7);
        stats7.CompletedWorkSessions.Should().Be(2);
        stats7.TotalFocusSeconds.Should().Be(2100);
        stats7.LastCompletedAtUtc.Should().NotBeNull();

        var recent = await repo.GetRecentSessionsAsync(limit: 10);
        recent.Select(s => s.SessionId).Should().Equal("s-work-2", "s-break-1", "s-work-1", "s-old-work");
        recent.Single(s => s.SessionId == "s-work-1").ScreenEffectUsed.Should().BeTrue();
        recent.Single(s => s.SessionId == "s-work-1").ZenNoiseType.Should().Be(ZenNoiseType.Brown);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.RecordSessionAsync(new FocusSession
            {
                SessionId = "bad-planned",
                PhaseType = FocusPhaseType.Work,
                PlannedSeconds = 0,
                CompletedSeconds = 10,
                StartedAtUtc = now
            }));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.RecordSessionAsync(new FocusSession
            {
                SessionId = "bad-completed",
                PhaseType = FocusPhaseType.Work,
                PlannedSeconds = 60,
                CompletedSeconds = -1,
                StartedAtUtc = now
            }));
    }
}
