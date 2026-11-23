using FluentAssertions;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Xunit;

namespace Shima.Core.Tests;

public sealed class IslandVisibilityArbiterTests
{
    [Fact]
    public void InitialState_IsCollapsedIdle()
    {
        var pinController = new OverlayPinController();
        using var manager = new IslandStateManager(pinController);

        manager.CurrentState.Should().Be(IslandShellState.Collapsed);
        manager.CurrentPriority.Should().Be(VisibilityPriority.CollapsedIdle);
        pinController.IsPinned.Should().BeFalse();
        pinController.ActiveTargetState.Should().BeNull();
    }

    [Fact]
    public void PriorityHierarchy_HigherTiersOverrideLowerTiers_Deterministically()
    {
        var pinController = new OverlayPinController();
        using var manager = new IslandStateManager(pinController);

        // 1. CompactActive overrides CollapsedIdle
        manager.SetCompactActive(true, "Focus 24:59");
        manager.CurrentState.Should().Be(IslandShellState.Active);
        manager.CurrentPriority.Should().Be(VisibilityPriority.CompactActive);
        manager.CompactPillBadgeText.Should().Be("Focus 24:59");

        // 2. NotificationPeek overrides CompactActive
        manager.RequestNotificationPeek(NotificationPriority.Normal, TimeSpan.FromSeconds(4));
        manager.CurrentState.Should().Be(IslandShellState.Notification);
        manager.CurrentPriority.Should().Be(VisibilityPriority.NotificationPeek);

        // 3. UserExpanded overrides NotificationPeek
        manager.RequestUserExpand();
        manager.CurrentState.Should().Be(IslandShellState.Expanded);
        manager.CurrentPriority.Should().Be(VisibilityPriority.UserExpanded);

        // 4. FullscreenHardHide overrides UserExpanded and CompactActive
        manager.SetFullscreenOnHostMonitor(true);
        manager.CurrentState.Should().Be(IslandShellState.Fullscreen);
        manager.CurrentPriority.Should().Be(VisibilityPriority.FullscreenHardHide);

        // 5. ExplicitPin overrides FullscreenHardHide
        var pin = pinController.AcquirePin(PinReason.ColorPickerPreview, PreviewTargetState.Expanded);
        manager.CurrentState.Should().Be(IslandShellState.Expanded);
        manager.CurrentPriority.Should().Be(VisibilityPriority.ExplicitPin);

        // 6. SystemSuspended overrides ExplicitPin (highest tier)
        manager.SetSystemSuspended(true);
        manager.CurrentState.Should().Be(IslandShellState.Suspended);
        manager.CurrentPriority.Should().Be(VisibilityPriority.SystemSuspended);

        // Unwind top-down: resuming restores ExplicitPin first
        manager.SetSystemSuspended(false);
        manager.CurrentState.Should().Be(IslandShellState.Expanded);
        manager.CurrentPriority.Should().Be(VisibilityPriority.ExplicitPin);

        // Releasing pin returns immediately to FullscreenHardHide
        pinController.ReleasePin(pin);
        manager.CurrentState.Should().Be(IslandShellState.Fullscreen);
        manager.CurrentPriority.Should().Be(VisibilityPriority.FullscreenHardHide);

        // Exiting fullscreen restores persistent CompactActive state (UserExpanded was cleared on fullscreen entry)
        manager.SetFullscreenOnHostMonitor(false);
        manager.CurrentState.Should().Be(IslandShellState.Active);
        manager.CurrentPriority.Should().Be(VisibilityPriority.CompactActive);
    }

    [Fact]
    public void PinController_BlocksAutoCollapseWhilePinned_AndRestoresPreviousCollapseBehaviorOnRelease()
    {
        var pinController = new OverlayPinController();
        using var manager = new IslandStateManager(pinController);

        manager.RequestUserExpand();
        manager.CurrentState.Should().Be(IslandShellState.Expanded);

        var pin = pinController.AcquirePin(PinReason.ColorPickerPreview, PreviewTargetState.Expanded);
        pinController.IsPinned.Should().BeTrue();

        // Mouse-leave auto-collapse must be ignored while pinned.
        manager.RequestUserCollapse();
        manager.CurrentState.Should().Be(IslandShellState.Expanded);
        manager.CurrentPriority.Should().Be(VisibilityPriority.ExplicitPin);

        // Toggle live preview mode from Expanded -> Collapsed while keeping pin held.
        pinController.UpdatePinTargetState(pin, PreviewTargetState.Collapsed);
        manager.CurrentState.Should().Be(IslandShellState.Collapsed);
        manager.CurrentPriority.Should().Be(VisibilityPriority.ExplicitPin);

        // Toggle back to Expanded.
        pinController.UpdatePinTargetState(pin, PreviewTargetState.Expanded);
        manager.CurrentState.Should().Be(IslandShellState.Expanded);

        // Release pin and simulate mouse-leave auto-collapse (cursor outside Settings/Island).
        pinController.ReleasePin(pin);
        pinController.IsPinned.Should().BeFalse();

        manager.RequestUserCollapse();
        manager.CurrentState.Should().Be(IslandShellState.Collapsed);
        manager.CurrentPriority.Should().Be(VisibilityPriority.CollapsedIdle);
    }

    [Fact]
    public void PinController_SupportsNestedPins_AndIdempotentRelease()
    {
        var pinController = new OverlayPinController();
        using var manager = new IslandStateManager(pinController);

        var firstPin = pinController.AcquirePin(PinReason.FocusAttention, PreviewTargetState.Collapsed);
        var secondPin = pinController.AcquirePin(PinReason.ColorPickerPreview, PreviewTargetState.Expanded);

        pinController.IsPinned.Should().BeTrue();
        manager.CurrentState.Should().Be(IslandShellState.Expanded);

        // Releasing second pin reveals the earlier pin target state.
        pinController.ReleasePin(secondPin);
        pinController.IsPinned.Should().BeTrue();
        manager.CurrentState.Should().Be(IslandShellState.Collapsed);
        manager.CurrentPriority.Should().Be(VisibilityPriority.ExplicitPin);

        // Duplicate release must be a safe no-op.
        pinController.ReleasePin(secondPin);
        pinController.IsPinned.Should().BeTrue();

        // Releasing final pin restores idle priority.
        pinController.ReleasePin(firstPin);
        pinController.IsPinned.Should().BeFalse();
        manager.CurrentPriority.Should().Be(VisibilityPriority.CollapsedIdle);
    }

    [Fact]
    public void Fullscreen_SuppressesHoverAndNormalNotifications_WhileAllowingHotkeySummonAndOptInCriticalBreakthrough()
    {
        var pinController = new OverlayPinController();
        using var manager = new IslandStateManager(pinController);

        manager.SetFullscreenOnHostMonitor(true);
        manager.CurrentState.Should().Be(IslandShellState.Fullscreen);

        // Normal user expand & standard notifications are blocked during fullscreen.
        manager.RequestUserExpand();
        manager.CurrentState.Should().Be(IslandShellState.Fullscreen);

        manager.RequestNotificationPeek(NotificationPriority.Normal, TimeSpan.FromSeconds(5));
        manager.CurrentState.Should().Be(IslandShellState.Fullscreen);

        // Critical alert with FullscreenBreakthroughAlerts == false remains hidden.
        manager.FullscreenBreakthroughAlerts = false;
        manager.RequestNotificationPeek(NotificationPriority.Critical, TimeSpan.FromSeconds(5));
        manager.CurrentState.Should().Be(IslandShellState.Fullscreen);

        // Enabling FullscreenBreakthroughAlerts allows critical alert to surface over fullscreen.
        manager.FullscreenBreakthroughAlerts = true;
        manager.CurrentState.Should().Be(IslandShellState.Notification);
        manager.CurrentPriority.Should().Be(VisibilityPriority.HotkeySummon);

        manager.ClearNotificationPeek();
        manager.CurrentState.Should().Be(IslandShellState.Fullscreen);

        // Hotkey summon temporarily surfaces Expanded over fullscreen; toggling again returns to Fullscreen.
        manager.ToggleHotkeySummon();
        manager.CurrentState.Should().Be(IslandShellState.Expanded);
        manager.CurrentPriority.Should().Be(VisibilityPriority.HotkeySummon);

        manager.ToggleHotkeySummon();
        manager.CurrentState.Should().Be(IslandShellState.Fullscreen);
        manager.CurrentPriority.Should().Be(VisibilityPriority.FullscreenHardHide);
    }

    [Fact]
    public void InputFocusActive_BlocksSoftCollapse_UntilClearedOrForced()
    {
        var pinController = new OverlayPinController();
        using var manager = new IslandStateManager(pinController);

        manager.RequestUserExpand();
        manager.SetInputFocusActive(true);

        manager.RequestUserCollapse();
        manager.CurrentState.Should().Be(IslandShellState.Expanded);

        // Force collapse (e.g. Esc key) collapses immediately.
        manager.RequestUserCollapse(force: true);
        manager.CurrentState.Should().Be(IslandShellState.Collapsed);
    }

    [Fact]
    public void StateChanged_EmitsTransitionsOnlyWhenStateChanges()
    {
        var pinController = new OverlayPinController();
        using var manager = new IslandStateManager(pinController);
        var transitions = new List<IslandStateChangedEventArgs>();
        manager.StateChanged += (_, args) => transitions.Add(args);

        manager.Reevaluate();
        transitions.Should().BeEmpty();

        manager.SetCompactActive(true, "Playing");
        manager.SetCompactActive(true, "Updated track"); // Still Active -> no state change event
        manager.RequestUserExpand();
        manager.RequestUserCollapse();

        transitions.Should().Equal(
            new IslandStateChangedEventArgs(IslandShellState.Collapsed, IslandShellState.Active),
            new IslandStateChangedEventArgs(IslandShellState.Active, IslandShellState.Expanded),
            new IslandStateChangedEventArgs(IslandShellState.Expanded, IslandShellState.Active));
    }

    [Fact]
    public async Task TransitionToAsync_MapsShellStatesAndHonorsCancellation()
    {
        var pinController = new OverlayPinController();
        using var manager = new IslandStateManager(pinController);

        await manager.TransitionToAsync(IslandShellState.Expanded);
        manager.CurrentState.Should().Be(IslandShellState.Expanded);

        await manager.TransitionToAsync(IslandShellState.Collapsed);
        manager.CurrentState.Should().Be(IslandShellState.Collapsed);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await FluentActions.Invoking(() => manager.TransitionToAsync(IslandShellState.Active, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void CompactCompanionSlot_TracksBackgroundWidgetAlongsideNormalPrimary_AndYieldsToUrgentOrIdle()
    {
        var pinController = new OverlayPinController();
        using var manager = new IslandStateManager(pinController);
        var compactEvents = 0;
        manager.CompactPillUpdated += (_, _) => compactEvents++;

        // Media playing alone -> no companion
        manager.SetCompactActive("shima.music", isActive: true, badgeText: "Midnight City", CompactPillPriority.Normal);
        manager.CompactActiveWidgetId.Should().Be("shima.music");
        manager.CompactCompanionWidgetId.Should().BeNull();

        // Developer activity becomes active in background -> Media remains primary, Agent becomes companion
        var countBeforeCompanion = compactEvents;
        manager.SetCompactActive("shima.developer", isActive: true, badgeText: "Agent working", CompactPillPriority.Background);
        manager.CurrentState.Should().Be(IslandShellState.Active);
        manager.CompactActiveWidgetId.Should().Be("shima.music");
        manager.CompactCompanionWidgetId.Should().Be("shima.developer");
        compactEvents.Should().BeGreaterThan(countBeforeCompanion);

        // Developer escalates to Urgent (approval/error) -> takes full compact pill, clears companion
        manager.SetCompactActive("shima.developer", isActive: true, badgeText: "Approval needed", CompactPillPriority.Urgent);
        manager.CompactActiveWidgetId.Should().Be("shima.developer");
        manager.CompactCompanionWidgetId.Should().BeNull();

        // Urgent resolved back to Working (Background) -> Media primary + Agent companion restored
        manager.SetCompactActive("shima.developer", isActive: true, badgeText: "Agent working", CompactPillPriority.Background);
        manager.CompactActiveWidgetId.Should().Be("shima.music");
        manager.CompactCompanionWidgetId.Should().Be("shima.developer");

        // Developer goes Idle -> companion clears while Media stays active
        var countBeforeIdle = compactEvents;
        manager.SetCompactActive("shima.developer", isActive: false, badgeText: null, CompactPillPriority.Background);
        manager.CurrentState.Should().Be(IslandShellState.Active);
        manager.CompactActiveWidgetId.Should().Be("shima.music");
        manager.CompactCompanionWidgetId.Should().BeNull();
        compactEvents.Should().BeGreaterThan(countBeforeIdle);
    }
}
