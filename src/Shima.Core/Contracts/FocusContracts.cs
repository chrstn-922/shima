namespace Shima.Core.Contracts;

public enum FocusPhaseType
{
    Work,
    ShortBreak,
    LongBreak
}

public enum ZenNoiseType
{
    None,
    White,
    Pink,
    Brown
}

public enum FocusTimerRunState
{
    Idle,
    Running,
    Paused
}

/// <summary>
/// Persisted Focus Timer session record stored in SQLite focus_sessions per data-model.md.
/// </summary>
public sealed record FocusSession
{
    public required string SessionId { get; init; }

    public FocusPhaseType PhaseType { get; init; }

    public int PlannedSeconds { get; init; }

    public int CompletedSeconds { get; init; }

    public ZenNoiseType ZenNoiseType { get; init; } = ZenNoiseType.None;

    public bool ScreenEffectUsed { get; init; }

    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset? EndedAtUtc { get; init; }
}

public sealed record FocusStatsSummary(
    int CompletedWorkSessions,
    int TotalFocusSeconds,
    DateTimeOffset? LastCompletedAtUtc);

/// <summary>
/// Local SQLite repository managing completed/elapsed focus timer sessions and aggregate stats.
/// </summary>
public interface IFocusRepository
{
    Task<FocusSession> RecordSessionAsync(FocusSession session, CancellationToken ct = default);

    Task<FocusStatsSummary> GetRecentStatsAsync(int days = 7, CancellationToken ct = default);

    Task<IReadOnlyList<FocusSession>> GetRecentSessionsAsync(int limit = 20, CancellationToken ct = default);
}

/// <summary>
/// Procedural DSP ambient noise synthesizer (White, Pink, Brown) backed by WASAPI output.
/// Zero audio threads or timers run when stopped.
/// </summary>
public interface IZenAudioSynthAdapter : IDisposable
{
    bool IsPlaying { get; }

    ZenNoiseType ActiveNoiseType { get; }

    double Volume { get; }

    void Start(ZenNoiseType noiseType, double volume);

    void UpdateVolume(double volume);

    void Stop();
}

public sealed record ZenDisplayOptions(
    bool GrayscaleEnabled,
    double DimOpacity,
    bool PauseDuringFullscreen = true);

/// <summary>
/// Pre-effect crash-recovery journal persisted atomically under %LOCALAPPDATA%\Shima\config\runtime-journal.json.
/// </summary>
public sealed record RuntimeRecoveryJournal
{
    public const string DefaultRelativePath = "config/runtime-journal.json";

    public int SchemaVersion { get; init; } = 1;

    public bool ZenDisplayEffectActive { get; init; }

    public bool LastExitClean { get; init; } = true;

    public DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>
/// Process-scoped DWM Magnification grayscale/dimming adapter with pre-effect journal recovery.
/// </summary>
public interface IZenDisplayEffectAdapter : IDisposable
{
    bool IsEffectActive { get; }

    bool WasCrashRecoveredOnStartup { get; }

    bool RecoverFromStartupIfNeeded();

    bool TryApplyEffect(ZenDisplayOptions options);

    void RestoreDisplay();

    void MarkCleanExit();

    void SetPausedByFullscreen(bool isFullscreen);
}
