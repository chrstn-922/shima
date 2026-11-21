namespace Shima.Core.Contracts;

public enum VisibilityPriority
{
    CollapsedIdle = 0,
    CompactActive = 20,
    NotificationPeek = 40,
    UserExpanded = 60,
    ExplicitPin = 80,
    SystemSuspended = 90,
    FullscreenHardHide = 100
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

    void ReleasePin(PinToken token);

    bool IsPinned { get; }
}
