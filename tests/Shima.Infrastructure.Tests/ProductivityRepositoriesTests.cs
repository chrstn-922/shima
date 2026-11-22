using FluentAssertions;
using Shima.Core.Contracts;
using Shima.Infrastructure.Persistence;
using Shima.Infrastructure.Repositories;
using Xunit;

namespace Shima.Infrastructure.Tests;

public sealed class ProductivityRepositoriesTests : TempDirectoryTest
{
    private async Task<string> CreateInitializedDbPathAsync()
    {
        var dbPath = Path.Combine(Root, "data", "shima.db");
        await new SqliteMigrationRunner(dbPath).InitializeAsync();
        return dbPath;
    }

    [Fact]
    public async Task NoteRepository_CrudPinSearchAnd200KbCap_EnforcesLimitsAndOrder()
    {
        var dbPath = await CreateInitializedDbPathAsync();
        var repo = new NoteRepository(dbPath);
        var t0 = new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

        var noteA = await repo.UpsertAsync(new NoteEntry
        {
            NoteId = "note-a",
            Title = "",
            ContentText = "  Sprint retro_notes 100%\nDiscuss release checklist",
            UpdatedAtUtc = t0
        });
        noteA.Title.Should().Be("Sprint retro_notes 100%");

        var noteB = await repo.UpsertAsync(new NoteEntry
        {
            NoteId = "note-b",
            Title = "Second note",
            ContentText = "Shopping list: espresso beans",
            UpdatedAtUtc = t0.AddMinutes(5)
        });

        var all = await repo.GetAllAsync();
        all.Select(n => n.NoteId).Should().Equal("note-b", "note-a");

        // Pin older note-a -> floats to top above newer note-b
        await repo.SetPinnedAsync("note-a", true);
        all = await repo.GetAllAsync();
        all.Select(n => n.NoteId).Should().Equal("note-a", "note-b");

        // Wildcard LIKE escaping for '%' and '_'
        var percentSearch = await repo.SearchAsync("100%");
        percentSearch.Should().ContainSingle(n => n.NoteId == "note-a");

        var underscoreSearch = await repo.SearchAsync("retro_notes");
        underscoreSearch.Should().ContainSingle(n => n.NoteId == "note-a");

        // Exactly 200,000 bytes accepted; 200,001 bytes (ASCII or multi-byte UTF-8) rejected
        var exactCapNote = await repo.UpsertAsync(new NoteEntry
        {
            NoteId = "note-exact-cap",
            Title = "Cap test",
            ContentText = new string('a', NoteEntry.MaxContentLength),
            UpdatedAtUtc = t0.AddMinutes(10)
        });
        exactCapNote.ContentText.Length.Should().Be(NoteEntry.MaxContentLength);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.UpsertAsync(new NoteEntry
            {
                NoteId = "note-too-long",
                Title = "Too big",
                ContentText = new string('a', NoteEntry.MaxContentLength + 1),
                UpdatedAtUtc = t0.AddMinutes(11)
            }));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.UpsertAsync(new NoteEntry
            {
                NoteId = "note-utf8-over",
                Title = "UTF-8 over",
                ContentText = new string('\u4E2D', 66_667), // 200,001 UTF-8 bytes
                UpdatedAtUtc = t0.AddMinutes(12)
            }));

        await repo.DeleteAsync("note-b");
        (await repo.GetAllAsync()).Select(n => n.NoteId).Should().NotContain("note-b");
    }

    [Fact]
    public async Task ClipboardRepository_DedupRetentionPruningPinnedExemptionAndAssetCleanup_WorksEndToEnd()
    {
        var dbPath = await CreateInitializedDbPathAsync();
        var repo = new ClipboardRepository(dbPath, Root);
        var now = DateTimeOffset.UtcNow;

        // Path traversal / rooted paths must be blocked cleanly
        repo.ResolveImageFullPath(@"..\secret.png").Should().BeNull();
        repo.ResolveImageFullPath(@"C:\Windows\System32\calc.exe").Should().BeNull();

        var relPath = await repo.SaveImageAssetAsync("img-1", [137, 80, 78, 71]);
        relPath.Should().NotBeNull();
        var fullImagePath = repo.ResolveImageFullPath(relPath);
        fullImagePath.Should().NotBeNull();
        File.Exists(fullImagePath!).Should().BeTrue();

        var firstImg = await repo.TryAddEntryAsync(
            new ClipboardEntry
            {
                EntryId = "img-1",
                ContentType = ClipboardContentType.Image,
                ImageRelPath = relPath,
                ContentHash = "hash-img-1",
                SourceAppName = "SnippingTool",
                ByteSize = 4,
                CopiedAtUtc = now.AddMinutes(-10)
            },
            maxUnpinnedItems: 10,
            retentionDays: 30);
        firstImg.Inserted.Should().BeTrue();

        // Consecutive duplicate hash -> deduplicated in place
        var duplicateImg = await repo.TryAddEntryAsync(
            new ClipboardEntry
            {
                EntryId = "img-1-dup",
                ContentType = ClipboardContentType.Image,
                ImageRelPath = relPath,
                ContentHash = "hash-img-1",
                ByteSize = 4,
                CopiedAtUtc = now.AddMinutes(-9)
            },
            maxUnpinnedItems: 10,
            retentionDays: 30);
        duplicateImg.Inserted.Should().BeFalse();
        duplicateImg.Deduplicated.Should().BeTrue();
        duplicateImg.Entry!.EntryId.Should().Be("img-1");

        // Pin one text item, add old unpinned item (via 365-day window), and verify non-consecutive hash is inserted
        var pinnedRelPath = await repo.SaveImageAssetAsync("img-pinned", [1, 2, 3, 4]);
        await repo.TryAddEntryAsync(new ClipboardEntry
        {
            EntryId = "img-pinned",
            ContentType = ClipboardContentType.Image,
            ImageRelPath = pinnedRelPath,
            ContentHash = "hash-pinned",
            ByteSize = 4,
            IsPinned = true,
            CopiedAtUtc = now.AddDays(-60)
        }, maxUnpinnedItems: 10, retentionDays: 365);

        await repo.TryAddEntryAsync(new ClipboardEntry
        {
            EntryId = "txt-old",
            ContentType = ClipboardContentType.Text,
            TextContent = "expired snippet",
            ContentHash = "hash-old",
            ByteSize = 15,
            CopiedAtUtc = now.AddDays(-45)
        }, maxUnpinnedItems: 10, retentionDays: 365);

        // Re-copying img-1 hash AFTER txt-old is non-consecutive -> inserts new row
        var nonConsecutive = await repo.TryAddEntryAsync(new ClipboardEntry
        {
            EntryId = "txt-new",
            ContentType = ClipboardContentType.Text,
            TextContent = "latest note",
            ContentHash = "hash-txt-new",
            ByteSize = 11,
            CopiedAtUtc = now.AddMinutes(-1)
        }, maxUnpinnedItems: 10, retentionDays: 365);
        nonConsecutive.Inserted.Should().BeTrue();

        // Prune with maxUnpinnedItems=1, retentionDays=30 -> deletes txt-old (expired) AND img-1 (excess unpinned > 1)
        // Must preserve pinned 'img-pinned' and newest unpinned 'txt-new', AND delete img-1's PNG on disk!
        var pruned = await repo.PruneAsync(maxUnpinnedItems: 1, retentionDays: 30, utcNow: now);
        pruned.Should().Be(2);
        File.Exists(fullImagePath!).Should().BeFalse();
        File.Exists(repo.ResolveImageFullPath(pinnedRelPath)!).Should().BeTrue();

        var remaining = await repo.GetHistoryAsync();
        remaining.Select(e => e.EntryId).Should().Equal("img-pinned", "txt-new");

        // Clear unpinned preserves pinned items
        var cleared = await repo.ClearUnpinnedAsync();
        cleared.Should().Be(1);
        (await repo.GetHistoryAsync()).Should().ContainSingle(e => e.EntryId == "img-pinned");

        // Single delete removes pinned entry AND its cached PNG asset
        await repo.DeleteAsync("img-pinned");
        File.Exists(repo.ResolveImageFullPath(pinnedRelPath)!).Should().BeFalse();
        (await repo.GetHistoryAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task InboxRepository_GroupingOsToastDedupDismissalsAndPruning_WorksEndToEnd()
    {
        var dbPath = await CreateInitializedDbPathAsync();
        var repo = new InboxRepository(dbPath, Root);
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        var teamsIcon = await repo.SaveAppIconAssetAsync("Teams", [1, 2, 3]);
        teamsIcon.Should().NotBeNull();

        await repo.UpsertToastAsync(new InboxNotificationItem
        {
            NotificationId = "n-1",
            OsToastId = 101,
            AppId = "com.teams",
            AppName = "Microsoft Teams",
            AppIconRelPath = teamsIcon,
            Title = "Initial message",
            BodyText = "Ping",
            ReceivedAtUtc = now.AddMinutes(-5)
        });

        // Upserting same active OsToastId updates existing notification row cleanly
        await repo.UpsertToastAsync(new InboxNotificationItem
        {
            NotificationId = "n-1-updated",
            OsToastId = 101,
            AppId = "com.teams",
            AppName = "Microsoft Teams",
            Title = "Edited message",
            BodyText = "Ping edited",
            ReceivedAtUtc = now.AddMinutes(-4)
        });

        await repo.UpsertToastAsync(new InboxNotificationItem
        {
            NotificationId = "n-2",
            OsToastId = 102,
            AppId = "com.teams",
            AppName = "Microsoft Teams",
            Title = "Second ping",
            BodyText = "Standup soon",
            ReceivedAtUtc = now.AddMinutes(-2)
        });

        await repo.UpsertToastAsync(new InboxNotificationItem
        {
            NotificationId = "n-3",
            OsToastId = 201,
            AppId = "com.mail",
            AppName = "Mail",
            Title = "Build alert",
            BodyText = "Green build",
            ReceivedAtUtc = now.AddMinutes(-1)
        });

        await repo.UpsertToastAsync(new InboxNotificationItem
        {
            NotificationId = "n-expired",
            OsToastId = 999,
            AppId = "com.old",
            AppName = "Legacy",
            Title = "Old",
            BodyText = "Expired toast",
            ReceivedAtUtc = now.AddDays(-20)
        });

        var grouped = await repo.GetGroupedByAppAsync();
        grouped[0].AppId.Should().Be("com.mail");
        grouped[1].AppId.Should().Be("com.teams");
        grouped[1].Items.Should().HaveCount(2);
        grouped[1].Items.Select(i => i.Title).Should().Contain("Edited message");

        // External dismissal via OS toast ID
        await repo.MarkDismissedByOsToastIdAsync(201);
        (await repo.FindByOsToastIdAsync(201))!.IsDismissed.Should().BeTrue();

        // Clear app group returns active OS toast IDs for bidirectional Action Center sync
        var clearedGroupIds = await repo.ClearAppGroupAsync("com.teams");
        clearedGroupIds.Should().BeEquivalentTo(new uint[] { 101, 102 });

        // Prune removes expired 20-day item
        var pruned = await repo.PruneExpiredAsync(retentionDays: 14, maxStoredItems: 500, utcNow: now);
        pruned.Should().Be(1);
        (await repo.FindByOsToastIdAsync(999)).Should().BeNull();

        // Cap enforcement prunes oldest rows beyond maxStoredItems
        var prunedCap = await repo.PruneExpiredAsync(retentionDays: 30, maxStoredItems: 1, utcNow: now);
        prunedCap.Should().BeGreaterThanOrEqualTo(2);
    }
}
