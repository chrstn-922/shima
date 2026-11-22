using Shima.Core.Contracts;

namespace Shima.Widgets.FocusTimer;

public sealed record FocusSettings
{
    public int WorkMinutes { get; init; } = 25;

    public int ShortBreakMinutes { get; init; } = 5;

    public int LongBreakMinutes { get; init; } = 15;

    public int CyclesBeforeLongBreak { get; init; } = 4;

    public bool AutoStartNextPhase { get; init; }

    public ZenNoiseType DefaultNoiseType { get; init; } = ZenNoiseType.None;

    public double NoiseVolume { get; init; } = 0.35;

    public bool EnableGrayscale { get; init; }

    public bool EnableScreenDim { get; init; }

    public double DimOpacity { get; init; } = 0.35;

    public bool PauseScreenEffectOnFullscreen { get; init; } = true;

    public FocusSettings Normalize() => this with
    {
        WorkMinutes = Math.Clamp(WorkMinutes, 1, 120),
        ShortBreakMinutes = Math.Clamp(ShortBreakMinutes, 1, 60),
        LongBreakMinutes = Math.Clamp(LongBreakMinutes, 1, 60),
        CyclesBeforeLongBreak = Math.Clamp(CyclesBeforeLongBreak, 1, 12),
        DefaultNoiseType = Enum.IsDefined(DefaultNoiseType) ? DefaultNoiseType : ZenNoiseType.None,
        NoiseVolume = double.IsFinite(NoiseVolume) ? Math.Clamp(NoiseVolume, 0.0, 1.0) : 0.35,
        DimOpacity = double.IsFinite(DimOpacity) ? Math.Clamp(DimOpacity, 0.10, 0.75) : 0.35
    };

    public int GetPlannedSeconds(FocusPhaseType phase)
    {
        var normalized = Normalize();
        return phase switch
        {
            FocusPhaseType.Work => normalized.WorkMinutes * 60,
            FocusPhaseType.ShortBreak => normalized.ShortBreakMinutes * 60,
            FocusPhaseType.LongBreak => normalized.LongBreakMinutes * 60,
            _ => normalized.WorkMinutes * 60
        };
    }
}
