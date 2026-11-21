using System.Security.AccessControl;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Shima.Core.Contracts;
using Shima.Core.Widgets;
using Shima.Infrastructure.Configuration;
using Shima.Infrastructure.Persistence;
using Shima.Infrastructure.Storage;
using Xunit;

namespace Shima.Infrastructure.Tests;

public abstract class TempDirectoryTest : IDisposable
{
    protected TempDirectoryTest()
    {
        Root = Path.Combine(Path.GetTempPath(), "shima-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    protected string Root { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class AtomicConfigStoreTests : TempDirectoryTest
{
    [Fact]
    public async Task SaveAtomicAsync_ThenLoad_RoundTripsWithoutLeavingTempFile()
    {
        var store = new AtomicConfigStore<TestConfig>(Root);
        var value = new TestConfig { Name = "settings", Count = 3 };

        await store.SaveAtomicAsync("config/settings.json", value);
        var loaded = await store.LoadOrRecoverAsync("config/settings.json", () => new TestConfig());

        loaded.Should().BeEquivalentTo(value);
        File.Exists(Path.Combine(Root, "config", "settings.json.tmp")).Should().BeFalse();
    }

    [Fact]
    public async Task LoadOrRecoverAsync_WithCorruptJson_BacksUpFileAndReturnsDefault()
    {
        var configDirectory = Path.Combine(Root, "config");
        Directory.CreateDirectory(configDirectory);
        var path = Path.Combine(configDirectory, "settings.json");
        await File.WriteAllTextAsync(path, "{ this is not json");

        var store = new AtomicConfigStore<TestConfig>(Root);
        var loaded = await store.LoadOrRecoverAsync(
            "config/settings.json",
            () => new TestConfig { Name = "default" });

        loaded.Name.Should().Be("default");
        File.Exists(path).Should().BeFalse();
        var backupPath = Directory.GetFiles(configDirectory, "settings.corrupt.*.json").Single();
        (await File.ReadAllTextAsync(backupPath)).Should().Be("{ this is not json");
    }

    [Fact]
    public async Task LoadOrRecoverAsync_WithMissingFile_ReturnsDefaultWithoutWriting()
    {
        var store = new AtomicConfigStore<TestConfig>(Root);
        var loaded = await store.LoadOrRecoverAsync(
            "config/missing.json",
            () => new TestConfig { Name = "default" });

        loaded.Name.Should().Be("default");
        File.Exists(Path.Combine(Root, "config", "missing.json")).Should().BeFalse();
    }

    public sealed class TestConfig
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}

public sealed class SqliteMigrationRunnerTests : TempDirectoryTest
{
    [Fact]
    public async Task InitializeAsync_EnablesWalAndAppliesMigrationsOnce()
    {
        var databasePath = Path.Combine(Root, "data", "shima.db");
        var runner = new SqliteMigrationRunner(databasePath);

        await runner.InitializeAsync();
        await runner.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        await using (var journalMode = connection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode;";
            ((string?)await journalMode.ExecuteScalarAsync()).Should().Be("wal");
        }

        await using (var migrations = connection.CreateCommand())
        {
            migrations.CommandText = "SELECT version, applied_at_utc FROM schema_migrations;";
            await using var reader = await migrations.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(1);
            DateTimeOffset.TryParse(reader.GetString(1), out _).Should().BeTrue();
            (await reader.ReadAsync()).Should().BeFalse();
        }

        await using (var widgetConfigs = connection.CreateCommand())
        {
            widgetConfigs.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'widget_configs';";
            (await widgetConfigs.ExecuteScalarAsync()).Should().Be("widget_configs");
        }
    }

    [Fact]
    public async Task InitializeAsync_ProtectsDatabaseDirectoryWithUserOnlyAcl()
    {
        var databasePath = Path.Combine(Root, "secured", "shima.db");
        var runner = new SqliteMigrationRunner(databasePath);

        await runner.InitializeAsync();

        var directory = new DirectoryInfo(Path.GetDirectoryName(databasePath)!);
        var security = directory.GetAccessControl();
        security.AreAccessRulesProtected.Should().BeTrue();
    }
}

public sealed class SqliteWidgetConfigRepositoryTests : TempDirectoryTest
{
    [Fact]
    public async Task SaveGetAndUpdateOrder_RoundTripsWidgetConfigs()
    {
        var databasePath = Path.Combine(Root, "data", "shima.db");
        await new SqliteMigrationRunner(databasePath).InitializeAsync();
        var repository = new SqliteWidgetConfigRepository(databasePath);
        var updatedAt = DateTimeOffset.UtcNow;

        await repository.SaveAsync(new WidgetConfig
        {
            WidgetId = "shima.clock",
            IsEnabled = false,
            SortOrder = 3,
            AllowPeekNotifications = false,
            SettingsJson = """{"format":"24h"}""",
            UpdatedAtUtc = updatedAt
        });
        await repository.SaveAsync(new WidgetConfig
        {
            WidgetId = "shima.music",
            IsEnabled = true,
            SortOrder = 1,
            AllowPeekNotifications = true,
            SettingsJson = "{}",
            UpdatedAtUtc = updatedAt
        });

        var loaded = await repository.GetAllAsync();
        loaded.Select(config => config.WidgetId).Should().Equal("shima.music", "shima.clock");
        var clock = loaded.Single(config => config.WidgetId == "shima.clock");
        clock.IsEnabled.Should().BeFalse();
        clock.SortOrder.Should().Be(3);
        clock.AllowPeekNotifications.Should().BeFalse();
        clock.SettingsJson.Should().Be("""{"format":"24h"}""");
        clock.UpdatedAtUtc.Should().BeCloseTo(updatedAt, TimeSpan.FromMilliseconds(1));

        await repository.UpdateOrderAsync(["shima.clock", "shima.music"]);

        var reordered = await repository.GetAllAsync();
        reordered.Select(config => config.WidgetId).Should().Equal("shima.clock", "shima.music");
        reordered.Select(config => config.SortOrder).Should().Equal(0, 1);
    }
}

public sealed class AppPreferencesStoreTests : TempDirectoryTest
{
    [Fact]
    public async Task LoadAsync_ClampsOutOfRangeValues_AndClearsInvalidEndpoint()
    {
        var store = new AtomicConfigStore<AppPreferences>(Root);
        await store.SaveAtomicAsync(
            AppPreferencesStore.DefaultRelativePath,
            new AppPreferences
            {
                HoverExpandDelayMs = 1,
                AutoCollapseDelayMs = 99_999,
                ClipboardMaxItems = 5,
                ClipboardRetentionDays = 0,
                InboxRetentionDays = 500,
                TargetMonitorMode = TargetMonitorMode.SpecificMonitorId,
                SpecificMonitorDeviceId = "monitor-1",
                OpenCodeEndpointUrl = "http://example.com:4096"
            });

        var preferences = await new AppPreferencesStore(store).LoadAsync();

        preferences.HoverExpandDelayMs.Should().Be(100);
        preferences.AutoCollapseDelayMs.Should().Be(3000);
        preferences.ClipboardMaxItems.Should().Be(20);
        preferences.ClipboardRetentionDays.Should().Be(1);
        preferences.InboxRetentionDays.Should().Be(90);
        preferences.SpecificMonitorDeviceId.Should().Be("monitor-1");
        preferences.OpenCodeEndpointUrl.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_PreservesValidLoopbackEndpoint()
    {
        var store = new AtomicConfigStore<AppPreferences>(Root);
        var preferences = new AppPreferences { OpenCodeEndpointUrl = "http://127.0.0.1:4096" };

        await new AppPreferencesStore(store).SaveAsync(preferences);

        var loaded = await new AppPreferencesStore(store).LoadAsync();
        loaded.OpenCodeEndpointUrl.Should().Be("http://127.0.0.1:4096");
    }

    [Theory]
    [InlineData("http://localhost:4096", true)]
    [InlineData("http://127.0.0.1:4096", true)]
    [InlineData("https://localhost:4096", false)]
    [InlineData("http://example.com:4096", false)]
    [InlineData("not a url", false)]
    public void IsValidOpenCodeEndpoint_ValidatesLoopbackHttp(string url, bool expected)
    {
        AppPreferencesStore.IsValidOpenCodeEndpoint(url).Should().Be(expected);
    }
}

public sealed class DpapiSecretStoreTests
{
    [Fact]
    public void ProtectAndUnprotect_RoundTripsForCurrentUser()
    {
        var store = new DpapiSecretStore();
        var plaintext = new byte[] { 1, 2, 3, 4, 5 };

        var ciphertext = store.ProtectForCurrentUser(plaintext);

        ciphertext.Should().NotBeEquivalentTo(plaintext);
        store.UnprotectForCurrentUser(ciphertext).Should().Equal(plaintext);
    }
}

public sealed class SanitizedFileLoggerTests : TempDirectoryTest
{
    [Fact]
    public void Log_RedactsUserProfileAndEmailAddresses()
    {
        var logPath = Path.Combine(Root, "logs", "shima.log");
        var logger = new SanitizedFileLogger(logPath);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        logger.Log(
            LogLevel.Error,
            $"Failed at {userProfile}\\file.txt for user@example.com",
            new InvalidOperationException("boom"));

        var contents = File.ReadAllText(logPath);
        contents.Should().Contain("%USERPROFILE%");
        contents.Should().Contain("[redacted-email]");
        contents.Should().NotContain(userProfile);
        contents.Should().NotContain("user@example.com");
    }
}
