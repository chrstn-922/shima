namespace Shima.Core.Contracts;

public enum IslandShellState
{
    Collapsed,
    Active,
    Notification,
    Expanded,
    Fullscreen,
    Suspended
}

public enum PowerProfile
{
    AC,
    Battery,
    EnergySaver,
    DisplayOff
}

public sealed record IslandStateChangedEventArgs(IslandShellState PreviousState, IslandShellState NewState);

public interface IIslandStateManager
{
    IslandShellState CurrentState { get; }

    event EventHandler<IslandStateChangedEventArgs>? StateChanged;

    Task TransitionToAsync(IslandShellState newState, CancellationToken ct = default);
}
