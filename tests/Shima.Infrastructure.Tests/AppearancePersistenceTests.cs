using System.Text;
using FluentAssertions;
using Shima.Core.Appearance;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Infrastructure.Appearance;
using Shima.Infrastructure.Color;
using Shima.Infrastructure.Configuration;
using Shima.Infrastructure.Storage;
using Xunit;

namespace Shima.Infrastructure.Tests;

public sealed class AppearancePersistenceTests : TempDirectoryTest
{
    private static (
        ColorProcessingService ColorService,
        AtomicAppearanceConfigRepository AppearanceRepo,
        AtomicPresetCatalogRepository PresetRepo,
        PresetManager PresetManager,
        OverlayPinController PinController,
        AppearanceManager AppearanceManager) CreateAppearanceStack(string rootDir)
    {
        var colorService = new ColorProcessingService();
        var appearanceRepo = new AtomicAppearanceConfigRepository(new AtomicConfigStore<AppearanceConfiguration>(rootDir));
        var presetRepo = new AtomicPresetCatalogRepository(new AtomicConfigStore<PresetCatalog>(rootDir));
        var presetManager = new PresetManager(presetRepo, colorService);
        var pinController = new OverlayPinController();
        var appearanceManager = new AppearanceManager(appearanceRepo, presetManager, colorService, pinController);
        return (colorService, appearanceRepo, presetRepo, presetManager, pinController, appearanceManager);
    }

    [Fact]
    public async Task PresetManager_EnforcesImmutableBuiltIns_AndPersistsCustomPresetsSwatchesAndRecents()
    {
        var (_, _, presetRepo, presetManager, _, _) = CreateAppearanceStack(Root);
        await presetManager.InitializeAsync();

        presetManager.BuiltInPresets.Select(p => p.Name).Should().Equal(
            "Midnight", "Snow", "Ocean", "Lavender", "Emerald", "Sunset");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            presetManager.RenameCustomPresetAsync("builtin.midnight", "Hacked"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            presetManager.DeleteCustomPresetAsync("builtin.ocean"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            presetManager.UpdateCustomPresetAsync("builtin.snow", IslandColorPalette.Midnight));

        var duplicated = await presetManager.DuplicatePresetAsync("builtin.ocean", "Ocean Custom");
        duplicated.IsBuiltIn.Should().BeFalse();
        duplicated.CollapsedTokens.Should().BeEquivalentTo(presetManager.FindById("builtin.ocean")!.CollapsedTokens);

        var renamed = await presetManager.RenameCustomPresetAsync(duplicated.PresetId, "Midnight Teal");
        renamed.Name.Should().Be("Midnight Teal");

        var updatedPalette = renamed.ExpandedTokens with { AccentHex = "#22D3EE" };
        var updated = await presetManager.UpdateCustomPresetAsync(renamed.PresetId, renamed.CollapsedTokens, updatedPalette);
        updated.ExpandedTokens.AccentHex.Should().Be("#22D3EE");

        var swatch = await presetManager.SaveSwatchAsync("Emerald Focus", "#34D399");
        swatch.Hex.Should().Be("#34D399");

        // Record 15 recent colors -> verify FIFO deduplicated max 12 in presets.json
        for (var i = 0; i < 15; i++)
        {
            await presetManager.RecordRecentColorAsync($"#20{i:X2}AA");
        }

        // Re-record an older color -> moves to index 0 cleanly without duplicating
        await presetManager.RecordRecentColorAsync("#2005AA");
        presetManager.RecentColors.Should().HaveCount(PresetCatalog.MaxRecentColors);
        presetManager.RecentColors[0].Should().Be("#2005AA");

        // Verify persistence round-trip from disk in a fresh manager instance
        var colorService2 = new ColorProcessingService();
        var presetManager2 = new PresetManager(presetRepo, colorService2);
        await presetManager2.InitializeAsync();
        presetManager2.CustomPresets.Should().ContainSingle(p => p.PresetId == updated.PresetId && p.Name == "Midnight Teal");
        presetManager2.SavedSwatches.Should().ContainSingle(s => s.Label == "Emerald Focus" && s.Hex == "#34D399");
        presetManager2.RecentColors.Should().HaveCount(PresetCatalog.MaxRecentColors);

        await presetManager2.DeleteSwatchAsync(swatch.SwatchId);
        await presetManager2.DeleteCustomPresetAsync(updated.PresetId);
        presetManager2.CustomPresets.Should().BeEmpty();
        presetManager2.SavedSwatches.Should().BeEmpty();
    }

    [Fact]
    public async Task EditingSession_LivePreviewPinning_SlotReset_ContrastFix_AndCancelRollback()
    {
        var stack = CreateAppearanceStack(Root);
        await stack.AppearanceManager.InitializeAsync();

        var appearanceFilePath = Path.Combine(Root, AtomicAppearanceConfigRepository.RelativePath);
        File.Exists(appearanceFilePath).Should().BeFalse();

        // Session 1: Pinning, Collapsed/Expanded preview toggle, live ThemeChanged without disk persistence, Cancel rollback
        using (var session = stack.AppearanceManager.BeginEditingSession(acquireLivePreviewPin: true, PreviewTargetState.Expanded))
        {
            stack.PinController.IsPinned.Should().BeTrue();
            stack.PinController.ActiveTargetState.Should().Be(PreviewTargetState.Expanded);

            session.SetPreviewState(PreviewTargetState.Collapsed);
            stack.PinController.ActiveTargetState.Should().Be(PreviewTargetState.Collapsed);

            var themeEvents = 0;
            stack.AppearanceManager.RenderedAppearanceChanged += (_, _) => themeEvents++;

            session.UpdateToken(AppearanceTokenSlot.Accent, IslandStateTarget.Both, "#FF5500");
            session.UpdateToken(AppearanceTokenSlot.PrimaryText, IslandStateTarget.Both, "#111111");

            themeEvents.Should().BeGreaterOrEqualTo(2);
            stack.AppearanceManager.GetToken("AccentBrush").Should().Be("#FF5500");
            stack.AppearanceManager.CurrentCommitted.ExpandedTokens.AccentHex.Should().Be("#FFFFFF");
            File.Exists(appearanceFilePath).Should().BeFalse();

            // Slot-scoped reset restores ONLY Accent and keeps PrimaryText draft edit
            var resetHex = session.ResetSlotToDefault(AppearanceTokenSlot.Accent, IslandStateTarget.Both);
            resetHex.Should().Be("#FFFFFF");
            session.DraftConfiguration.ExpandedTokens.AccentHex.Should().Be("#FFFFFF");
            session.DraftConfiguration.ExpandedTokens.PrimaryTextHex.Should().Be("#111111");

            // ApplyRecommendedContrastFix repairs low-contrast PrimaryText without altering BackgroundHex
            session.ApplyRecommendedContrastFix();
            session.CurrentContrastReport.HasWarnings.Should().BeFalse();
            session.DraftConfiguration.ExpandedTokens.BackgroundHex.Should().Be("#000000");

            session.Cancel();
        }

        // Cancel must release pin and revert live token back to committed Midnight
        stack.PinController.IsPinned.Should().BeFalse();
        stack.AppearanceManager.GetToken("TextPrimary").Should().Be("#F8FAFC");
        File.Exists(appearanceFilePath).Should().BeFalse();

        // Session 2: Non-blocking low-contrast commit + linked collapsed/expanded + restart survival
        using (var commitSession = stack.AppearanceManager.BeginEditingSession(acquireLivePreviewPin: true, PreviewTargetState.Expanded))
        {
            commitSession.PreviewLinkCollapsedAndExpanded(true);
            commitSession.UpdateToken(AppearanceTokenSlot.Background, IslandStateTarget.Both, "#121212");
            commitSession.DraftConfiguration.CollapsedTokens.BackgroundHex.Should().Be("#121212");
            commitSession.DraftConfiguration.ExpandedTokens.BackgroundHex.Should().Be("#121212");

            // Intentionally low contrast (#161616 text on #121212 background) must NOT block CommitAsync
            commitSession.UpdateToken(AppearanceTokenSlot.PrimaryText, IslandStateTarget.Both, "#161616");
            commitSession.CurrentContrastReport.HasWarnings.Should().BeTrue();

            var committed = await commitSession.CommitAsync();
            committed.CollapsedTokens.BackgroundHex.Should().Be("#121212");
            committed.ExpandedTokens.BackgroundHex.Should().Be("#121212");
            committed.ExpandedTokens.PrimaryTextHex.Should().Be("#161616");
            committed.GlowOpacity.Should().Be(AppearanceConfiguration.DefaultGlowOpacity);
            committed.BorderThicknessDip.Should().Be(AppearanceConfiguration.DefaultBorderThicknessDip);
        }

        stack.PinController.IsPinned.Should().BeFalse();
        stack.AppearanceManager.CurrentContrastReport.HasWarnings.Should().BeTrue();
        File.Exists(appearanceFilePath).Should().BeTrue();
        stack.PresetManager.RecentColors.Should().Contain("#161616");

        // Unlinked Collapsed & Expanded backgrounds edit independently
        await stack.AppearanceManager.SetLinkCollapsedAndExpandedAsync(false);
        using (var unlinkedSession = stack.AppearanceManager.BeginEditingSession(acquireLivePreviewPin: false))
        {
            unlinkedSession.UpdateToken(AppearanceTokenSlot.Background, IslandStateTarget.Expanded, "#0B2447");
            await unlinkedSession.CommitAsync();
        }

        stack.AppearanceManager.CurrentCommitted.CollapsedTokens.BackgroundHex.Should().Be("#121212");
        stack.AppearanceManager.CurrentCommitted.ExpandedTokens.BackgroundHex.Should().Be("#0B2447");

        // Simulated process restart: fresh AppearanceManager & PresetManager load committed appearance + recentColors
        var restarted = CreateAppearanceStack(Root);
        await restarted.AppearanceManager.InitializeAsync();
        restarted.AppearanceManager.CurrentCommitted.CollapsedTokens.BackgroundHex.Should().Be("#121212");
        restarted.AppearanceManager.CurrentCommitted.ExpandedTokens.BackgroundHex.Should().Be("#0B2447");
        restarted.AppearanceManager.CurrentCommitted.ExpandedTokens.PrimaryTextHex.Should().Be("#161616");
        restarted.PresetManager.RecentColors.Should().Contain("#0B2447");

        // Reset All restores Midnight defaults
        var restored = await restarted.AppearanceManager.ResetAllToDefaultsAsync();
        restored.CollapsedTokens.Should().BeEquivalentTo(IslandColorPalette.Midnight);
        restored.ExpandedTokens.Should().BeEquivalentTo(IslandColorPalette.Midnight);
        restored.LinkCollapsedAndExpanded.Should().BeTrue();
        restored.UsePerWidgetAccents.Should().BeFalse();
        restored.PerWidgetAccents.Should().BeEmpty();
    }

    [Fact]
    public async Task AppPreferencesService_ProtectsOpenCodePasswordWithDpapi_AndDiagnosticServiceRedactsSecrets()
    {
        var store = new AppPreferencesStore(new AtomicConfigStore<AppPreferences>(Root));
        var dpapi = new DpapiSecretStore();
        var service = new AppPreferencesService(store, dpapi);
        await service.InitializeAsync();

        var secretPassword = "opencode-super-secret-passphrase";
        await service.SaveAsync(
            service.Current with
            {
                OpenCodeOptInEnabled = true,
                OpenCodeEndpointUrl = "http://127.0.0.1:4096"
            },
            newOpenCodeServerPassword: secretPassword);

        service.Current.HasOpenCodeServerPassword.Should().BeTrue();
        service.ReadOpenCodeServerPassword().Should().Be(secretPassword);

        var settingsJson = await File.ReadAllTextAsync(Path.Combine(Root, "config", "settings.json"));
        settingsJson.Should().NotContain(secretPassword);

        await service.SaveAsync(service.Current, clearOpenCodeServerPassword: true);
        service.Current.HasOpenCodeServerPassword.Should().BeFalse();
        service.ReadOpenCodeServerPassword().Should().BeNull();

        var logDir = Path.Combine(Root, "logs");
        Directory.CreateDirectory(logDir);
        await File.WriteAllLinesAsync(
            Path.Combine(logDir, "shima-20261122.log"),
            [
                "2026-11-22 INFO Startup complete",
                "2026-11-22 WARN Auth header bearer: secret-jwt-token-123 password=my-secret"
            ],
            Encoding.UTF8);

        var diagService = new DiagnosticLogService(logDir);
        var recent = diagService.GetRecentSanitizedLines(20);
        recent.Should().HaveCount(2);
        recent[1].Should().NotContain("secret-jwt-token-123");
        recent[1].Should().NotContain("my-secret");
        recent[1].Should().Contain("[REDACTED]");

        var exportDir = Path.Combine(Root, "export");
        var bundlePath = diagService.ExportRedactedBundle(exportDir);
        File.Exists(bundlePath).Should().BeTrue();
        (await File.ReadAllTextAsync(bundlePath)).Should().NotContain("secret-jwt-token-123");
    }
}
