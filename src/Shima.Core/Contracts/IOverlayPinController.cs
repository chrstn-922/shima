namespace Shima.Core.Contracts;

/// <summary>
/// Deterministic visibility priority tiers resolved by the island state arbiter.
/// Higher values always override lower values.
/// </summary>
public enum VisibilityPriority
{
    CollapsedIdle = 0,
    CompactActive = 20,
    NotificationPeek = 40,
    UserExpanded = 60,
    HotkeySummon = 70,
    FullscreenHardHide = 80,
    ExplicitPin = 90,
    SystemSuspended = 100
}

public enum PinReason
{
    ColorPickerPreview,
    FocusAttention
}

public enum PreviewTargetState
{
    Collapsed,
    Expanded
}

public sealed record PinToken(Guid Id, PinReason Reason, PreviewTargetState TargetState);

public interface IOverlayPinController
{
    PinToken AcquirePin(PinReason reason, PreviewTargetState targetState);

    void UpdatePinTargetState(PinToken token, PreviewTargetState targetState);

    void ReleasePin(PinToken token);

    bool IsPinned { get; }

    PreviewTargetState? ActiveTargetState { get; }

    event EventHandler? PinStateChanged;
}
