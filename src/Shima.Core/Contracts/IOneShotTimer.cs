namespace Shima.Core.Contracts;

/// <summary>
/// Single-shot timer that fires its callback once and then stays idle (no repeating ticks).
/// </summary>
public interface IOneShotTimer : IDisposable
{
    bool IsPending { get; }

    void Schedule(TimeSpan delay, Action callback);

    void Cancel();
}
