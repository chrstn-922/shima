using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Shima.Core.Contracts;

namespace Shima.Widgets.FocusTimer;

/// <summary>
/// Pomodoro/custom countdown state machine, compact pill formatting, Zen DSP/display orchestration,
/// and SQLite focus session stats tracking. Runs ticks ONLY when RunState == Running.
/// </summary>
public sealed class FocusTimerWidgetViewModel : INotifyPropertyChanged, IDisposable
{
    public static readonly TimeSpan CountdownTickInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IFocusRepository _repository;
    private readonly IZenAudioSynthAdapter _zenAudio;
    private readonly IZenDisplayEffectAdapter _zenDisplay;
    private readonly IFocusTickTimer _tickTimer;
    private readonly bool _ownsTickTimer;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly object _sync = new();

    private IWidgetContext? _context;
    private Action<Action>? _uiDispatcher;
    private FocusSettings _settings = new();
    private FocusPhaseType _currentPhase = FocusPhaseType.Work;
    private FocusTimerRunState _runState = FocusTimerRunState.Idle;
    private int _plannedSeconds = 25 * 60;
    private int _remainingSeconds = 25 * 60;
    private int _completedWorkCyclesInSet;
    private int _activeSessionCompletedSeconds;
    private bool _activeSessionUsedScreenEffect;
    private DateTimeOffset? _activeSessionStartedAtUtc;
    private DateTimeOffset _lastTickUtc;
    private PowerProfile _powerProfile = PowerProfile.AC;
    private FocusStatsSummary _recentStats = new(0, 0, null);
    private string _statusMessage = "Ready";
    private bool _showCrashRecoveryNotice;
    private bool _disposed;

    public FocusTimerWidgetViewModel(
        IFocusRepository repository,
        IZenAudioSynthAdapter zenAudio,
        IZenDisplayEffectAdapter zenDisplay,
        IFocusTickTimer? tickTimer = null,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _zenAudio = zenAudio ?? throw new ArgumentNullException(nameof(zenAudio));
        _zenDisplay = zenDisplay ?? throw new ArgumentNullException(nameof(zenDisplay));
        _ownsTickTimer = tickTimer is null;
        _tickTimer = tickTimer ?? new SystemFocusTickTimer();
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public bool IsTimerRunning => _tickTimer.IsRunning;

    public FocusPhaseType CurrentPhase
    {
        get
        {
            lock (_sync)
            {
                return _currentPhase;
            }
        }
    }

    public FocusTimerRunState RunState
    {
        get
        {
            lock (_sync)
            {
                return _runState;
            }
        }
    }

    public bool IsRunning => RunState == FocusTimerRunState.Running;

    public bool IsWorkSelected => CurrentPhase == FocusPhaseType.Work;

    public bool IsShortBreakSelected => CurrentPhase == FocusPhaseType.ShortBreak;

    public bool IsLongBreakSelected => CurrentPhase == FocusPhaseType.LongBreak;

    public int RemainingSeconds
    {
        get
        {
            lock (_sync)
            {
                return _remainingSeconds;
            }
        }
    }

    public int PlannedSeconds
    {
        get
        {
            lock (_sync)
            {
                return _plannedSeconds;
            }
        }
    }

    public string CountdownDisplay => FormatCountdown(RemainingSeconds);

    public string PhaseLabel => CurrentPhase switch
    {
        FocusPhaseType.Work => "Focus",
        FocusPhaseType.ShortBreak => "Short break",
        FocusPhaseType.LongBreak => "Long break",
        _ => "Focus"
    };

    public string CompactPhaseShortLabel => CurrentPhase switch
    {
        FocusPhaseType.Work => "Focus",
        FocusPhaseType.ShortBreak => "Break",
        FocusPhaseType.LongBreak => "Rest",
        _ => "Focus"
    };

    public string PrimaryActionLabel => RunState switch
    {
        FocusTimerRunState.Running => "Pause",
        FocusTimerRunState.Paused => "Resume",
        _ => "Start"
    };

    public string PrimaryActionAutomationName => RunState switch
    {
        FocusTimerRunState.Running => "Pause focus timer",
        FocusTimerRunState.Paused => "Resume focus timer",
        _ => "Start focus timer"
    };

    public double ProgressFraction
    {
        get
        {
            lock (_sync)
            {
                if (_plannedSeconds <= 0)
                {
                    return 0.0;
                }

                var elapsed = Math.Clamp(_plannedSeconds - _remainingSeconds, 0, _plannedSeconds);
                return elapsed / (double)_plannedSeconds;
            }
        }
    }

    public double ProgressPercent => Math.Round(ProgressFraction * 100.0, 2);

    public string CycleStatusLabel
    {
        get
        {
            lock (_sync)
            {
                return $"Cycle {(_completedWorkCyclesInSet % _settings.CyclesBeforeLongBreak) + 1}/{_settings.CyclesBeforeLongBreak}";
            }
        }
    }

    public string StatsLabel
    {
        get
        {
            lock (_sync)
            {
                var mins = _recentStats.TotalFocusSeconds / 60;
                return _recentStats.CompletedWorkSessions == 1
                    ? $"1 session · {mins}m"
                    : $"{_recentStats.CompletedWorkSessions} sessions · {mins}m";
            }
        }
    }

    public ZenNoiseType SelectedNoiseType
    {
        get
        {
            lock (_sync)
            {
                return _settings.DefaultNoiseType;
            }
        }
    }

    public bool IsNoiseNone => SelectedNoiseType == ZenNoiseType.None;

    public bool IsNoiseWhite => SelectedNoiseType == ZenNoiseType.White;

    public bool IsNoisePink => SelectedNoiseType == ZenNoiseType.Pink;

    public bool IsNoiseBrown => SelectedNoiseType == ZenNoiseType.Brown;

    public double NoiseVolume
    {
        get
        {
            lock (_sync)
            {
                return _settings.NoiseVolume;
            }
        }
    }

    public double NoiseVolumePercent
    {
        get => Math.Round(NoiseVolume * 100.0);
        set => _ = SetNoiseVolumeAsync(value / 100.0);
    }

    public string NoiseVolumeLabel => $"{(int)NoiseVolumePercent}%";

    public bool IsGrayscaleEnabled
    {
        get
        {
            lock (_sync)
            {
                return _settings.EnableGrayscale;
            }
        }
    }

    public bool IsDimEnabled
    {
        get
        {
            lock (_sync)
            {
                return _settings.EnableScreenDim;
            }
        }
    }

    public double DimOpacity
    {
        get
        {
            lock (_sync)
            {
                return _settings.DimOpacity;
            }
        }
    }

    public double DimOpacityPercent
    {
        get => Math.Round(DimOpacity * 100.0);
        set => _ = SetDimOpacityAsync(value / 100.0);
    }

    public string DimOpacityLabel => $"{(int)DimOpacityPercent}%";

    public bool AutoStartNextPhase
    {
        get
        {
            lock (_sync)
            {
                return _settings.AutoStartNextPhase;
            }
        }
    }

    public bool PauseScreenEffectOnFullscreen
    {
        get
        {
            lock (_sync)
            {
                return _settings.PauseScreenEffectOnFullscreen;
            }
        }
    }

    public int WorkMinutes
    {
        get
        {
            lock (_sync)
            {
                return _settings.WorkMinutes;
            }
        }
    }

    public int ShortBreakMinutes
    {
        get
        {
            lock (_sync)
            {
                return _settings.ShortBreakMinutes;
            }
        }
    }

    public int LongBreakMinutes
    {
        get
        {
            lock (_sync)
            {
                return _settings.LongBreakMinutes;
            }
        }
    }

    public int CyclesBeforeLongBreak
    {
        get
        {
            lock (_sync)
            {
                return _settings.CyclesBeforeLongBreak;
            }
        }
    }

    public bool HasActiveZenEffects =>
        SelectedNoiseType != ZenNoiseType.None || IsGrayscaleEnabled || IsDimEnabled;

    public string StatusMessage
    {
        get
        {
            lock (_sync)
            {
                return _statusMessage;
            }
        }
        private set
        {
            bool changed;
            lock (_sync)
            {
                changed = !string.Equals(_statusMessage, value, StringComparison.Ordinal);
                if (changed)
                {
                    _statusMessage = value;
                }
            }

            if (changed)
            {
                DispatchToUi(() =>
                {
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AccessibleStatus));
                });
            }
        }
    }

    public bool ShowCrashRecoveryNotice
    {
        get
        {
            lock (_sync)
            {
                return _showCrashRecoveryNotice;
            }
        }
        private set
        {
            bool changed;
            lock (_sync)
            {
                changed = _showCrashRecoveryNotice != value;
                if (changed)
                {
                    _showCrashRecoveryNotice = value;
                }
            }

            if (changed)
            {
                DispatchToUi(() => OnPropertyChanged());
            }
        }
    }

    public string AccessibleStatus => $"Focus Timer: {PhaseLabel}, {CountdownDisplay}, {RunState}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SettingsChanged;

    public void AttachUiDispatcher(Action<Action> dispatch) =>
        _uiDispatcher = dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var rawJson = await context.Settings.ReadJsonAsync(ct).ConfigureAwait(false);
        var loaded = ParseSettingsOrDefault(rawJson);

        var stats = await _repository.GetRecentStatsAsync(days: 7, ct).ConfigureAwait(false);

        lock (_sync)
        {
            _settings = loaded;
            _plannedSeconds = loaded.GetPlannedSeconds(_currentPhase);
            _remainingSeconds = _plannedSeconds;
            _recentStats = stats;
            _showCrashRecoveryNotice = _zenDisplay.WasCrashRecoveredOnStartup;
            _statusMessage = _showCrashRecoveryNotice
                ? "Display restored safely after unexpected exit"
                : "Ready";
        }

        RaiseAllProperties();
    }

    public void OpenSettings() =>
        _context?.Events.Publish(new OpenSettingsRequestedEvent("FocusTimer"));

    public void SelectPhase(FocusPhaseType phase)
    {
        if (!Enum.IsDefined(phase))
        {
            return;
        }

        var partialSession = StopSessionCore(recordPartial: true, nextStatus: "Ready");
        if (partialSession is not null)
        {
            _ = PersistCompletedSessionSafeAsync(partialSession);
        }

        lock (_sync)
        {
            _currentPhase = phase;
            _plannedSeconds = _settings.GetPlannedSeconds(phase);
            _remainingSeconds = _plannedSeconds;
            _runState = FocusTimerRunState.Idle;
        }

        UpdateCompactPill(null);
        RaiseAllProperties();
    }

    public void StartOrTogglePause()
    {
        FocusTimerRunState state;
        lock (_sync)
        {
            state = _runState;
        }

        switch (state)
        {
            case FocusTimerRunState.Idle:
                Start();
                break;
            case FocusTimerRunState.Running:
                Pause();
                break;
            case FocusTimerRunState.Paused:
                Resume();
                break;
        }
    }

    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        var now = _utcNowProvider().ToUniversalTime();
        FocusSettings settings;
        FocusPhaseType phase;
        int planned;
        lock (_sync)
        {
            if (_runState == FocusTimerRunState.Running)
            {
                return;
            }

            if (_runState == FocusTimerRunState.Idle)
            {
                _plannedSeconds = _settings.GetPlannedSeconds(_currentPhase);
                _remainingSeconds = _plannedSeconds;
                _activeSessionCompletedSeconds = 0;
                _activeSessionStartedAtUtc = now;
                _activeSessionUsedScreenEffect = false;
            }

            _runState = FocusTimerRunState.Running;
            _lastTickUtc = now;
            _showCrashRecoveryNotice = false;
            settings = _settings;
            phase = _currentPhase;
            planned = _plannedSeconds;
        }

        ApplyZenEffectsForActiveSession(settings);
        StatusMessage = phase == FocusPhaseType.Work ? "Focus session active" : $"{PhaseLabel} active";
        UpdateCompactPill(BuildCompactPillText());

        if (_powerProfile != PowerProfile.DisplayOff)
        {
            _tickTimer.Start(CountdownTickInterval, OnTickTimerElapsed);
        }

        RaiseAllProperties();
    }

    public void Pause()
    {
        var now = _utcNowProvider().ToUniversalTime();
        bool reachedZero;
        lock (_sync)
        {
            if (_runState != FocusTimerRunState.Running)
            {
                return;
            }

            AdvanceElapsedSecondsLocked(now);
            reachedZero = _remainingSeconds <= 0;
            if (!reachedZero)
            {
                _runState = FocusTimerRunState.Paused;
            }
        }

        _tickTimer.Stop();
        _zenAudio.Stop();
        _zenDisplay.RestoreDisplay();

        if (reachedZero)
        {
            HandlePhaseCompleted(now);
            return;
        }

        UpdateCompactPill(null);
        StatusMessage = "Paused";
        RaiseAllProperties();
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_runState != FocusTimerRunState.Paused)
            {
                return;
            }
        }

        Start();
    }

    public void StopAndReset(bool recordPartialSession = true)
    {
        var partialSession = StopSessionCore(recordPartialSession, "Ready");
        if (partialSession is not null)
        {
            _ = PersistCompletedSessionSafeAsync(partialSession);
        }

        UpdateCompactPill(null);
        RaiseAllProperties();
    }

    public void SkipToNextPhase()
    {
        var now = _utcNowProvider().ToUniversalTime();
        var partial = StopSessionCore(recordPartial: true, nextStatus: "Ready");
        if (partial is not null)
        {
            _ = PersistCompletedSessionSafeAsync(partial);
        }

        lock (_sync)
        {
            _currentPhase = ResolveNextPhaseLocked(wasCompleted: false);
            _plannedSeconds = _settings.GetPlannedSeconds(_currentPhase);
            _remainingSeconds = _plannedSeconds;
            _runState = FocusTimerRunState.Idle;
            _activeSessionStartedAtUtc = null;
            _activeSessionCompletedSeconds = 0;
        }

        _ = now;
        UpdateCompactPill(null);
        RaiseAllProperties();
    }

    public void Tick(DateTimeOffset utcNow)
    {
        if (_disposed)
        {
            return;
        }

        bool reachedZero;
        lock (_sync)
        {
            if (_runState != FocusTimerRunState.Running)
            {
                return;
            }

            AdvanceElapsedSecondsLocked(utcNow.ToUniversalTime());
            reachedZero = _remainingSeconds <= 0;
        }

        if (reachedZero)
        {
            HandlePhaseCompleted(utcNow.ToUniversalTime());
            return;
        }

        UpdateCompactPill(BuildCompactPillText());
        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(RemainingSeconds));
            OnPropertyChanged(nameof(CountdownDisplay));
            OnPropertyChanged(nameof(ProgressFraction));
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(AccessibleStatus));
        });
    }

    public async Task SetNoiseTypeAsync(ZenNoiseType noiseType, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(noiseType))
        {
            return;
        }

        await UpdateSettingsAsync(s => s with { DefaultNoiseType = noiseType }, ct).ConfigureAwait(false);

        if (RunState == FocusTimerRunState.Running)
        {
            if (noiseType == ZenNoiseType.None)
            {
                _zenAudio.Stop();
            }
            else
            {
                _zenAudio.Start(noiseType, NoiseVolume);
            }
        }

        RaiseAllProperties();
    }

    public async Task SetNoiseVolumeAsync(double volume, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(volume, 0.0, 1.0);
        await UpdateSettingsAsync(s => s with { NoiseVolume = clamped }, ct).ConfigureAwait(false);
        _zenAudio.UpdateVolume(clamped);
        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(NoiseVolume));
            OnPropertyChanged(nameof(NoiseVolumePercent));
            OnPropertyChanged(nameof(NoiseVolumeLabel));
        });
    }

    public async Task ToggleGrayscaleAsync(CancellationToken ct = default) =>
        await SetDisplayOptionsAsync(!IsGrayscaleEnabled, IsDimEnabled, DimOpacity, PauseScreenEffectOnFullscreen, ct)
            .ConfigureAwait(false);

    public async Task ToggleScreenDimAsync(CancellationToken ct = default) =>
        await SetDisplayOptionsAsync(IsGrayscaleEnabled, !IsDimEnabled, DimOpacity, PauseScreenEffectOnFullscreen, ct)
            .ConfigureAwait(false);

    public async Task SetDimOpacityAsync(double dimOpacity, CancellationToken ct = default) =>
        await SetDisplayOptionsAsync(IsGrayscaleEnabled, IsDimEnabled, dimOpacity, PauseScreenEffectOnFullscreen, ct)
            .ConfigureAwait(false);

    public async Task SetDisplayOptionsAsync(
        bool enableGrayscale,
        bool enableDim,
        double dimOpacity,
        bool pauseOnFullscreen,
        CancellationToken ct = default)
    {
        var clampedDim = Math.Clamp(dimOpacity, 0.10, 0.75);
        await UpdateSettingsAsync(
            s => s with
            {
                EnableGrayscale = enableGrayscale,
                EnableScreenDim = enableDim,
                DimOpacity = clampedDim,
                PauseScreenEffectOnFullscreen = pauseOnFullscreen
            },
            ct).ConfigureAwait(false);

        if (RunState == FocusTimerRunState.Running)
        {
            ApplyZenDisplayOnly(_settings);
        }

        RaiseAllProperties();
    }

    public async Task UpdateIntervalsAsync(
        int workMinutes,
        int shortBreakMinutes,
        int longBreakMinutes,
        int cyclesBeforeLongBreak,
        bool autoStartNextPhase,
        CancellationToken ct = default)
    {
        await UpdateSettingsAsync(
            s => s with
            {
                WorkMinutes = workMinutes,
                ShortBreakMinutes = shortBreakMinutes,
                LongBreakMinutes = longBreakMinutes,
                CyclesBeforeLongBreak = cyclesBeforeLongBreak,
                AutoStartNextPhase = autoStartNextPhase
            },
            ct).ConfigureAwait(false);

        lock (_sync)
        {
            if (_runState == FocusTimerRunState.Idle)
            {
                _plannedSeconds = _settings.GetPlannedSeconds(_currentPhase);
                _remainingSeconds = _plannedSeconds;
            }
        }

        RaiseAllProperties();
    }

    public async Task ResetToDefaultsAsync(CancellationToken ct = default)
    {
        var defaults = new FocusSettings().Normalize();
        lock (_sync)
        {
            _settings = defaults;
            if (_runState == FocusTimerRunState.Idle)
            {
                _plannedSeconds = defaults.GetPlannedSeconds(_currentPhase);
                _remainingSeconds = _plannedSeconds;
            }
        }

        if (_context is not null)
        {
            await _context.Settings.WriteJsonAsync(JsonSerializer.Serialize(defaults, JsonOptions), ct)
                .ConfigureAwait(false);
        }

        if (RunState == FocusTimerRunState.Running)
        {
            ApplyZenEffectsForActiveSession(defaults);
        }

        RaiseAllProperties();
        DispatchToUi(() => SettingsChanged?.Invoke(this, EventArgs.Empty));
    }

    public void VerifyAndRestoreDisplay()
    {
        _zenDisplay.RestoreDisplay();
        if (RunState == FocusTimerRunState.Running)
        {
            ApplyZenDisplayOnly(_settings);
        }

        StatusMessage = "Display restored cleanly";
    }

    internal void OnPowerProfileChanged(PowerProfile profile)
    {
        var now = _utcNowProvider().ToUniversalTime();
        bool shouldStopTimer;
        bool shouldStartTimer;
        lock (_sync)
        {
            _powerProfile = profile;
            if (profile == PowerProfile.DisplayOff)
            {
                if (_runState == FocusTimerRunState.Running)
                {
                    AdvanceElapsedSecondsLocked(now);
                    _runState = FocusTimerRunState.Paused;
                }

                shouldStopTimer = true;
                shouldStartTimer = false;
            }
            else
            {
                shouldStopTimer = false;
                shouldStartTimer = _runState == FocusTimerRunState.Running && !_tickTimer.IsRunning;
            }
        }

        if (shouldStopTimer)
        {
            _tickTimer.Stop();
            _zenAudio.Stop();
            _zenDisplay.RestoreDisplay();
            UpdateCompactPill(null);
            RaiseAllProperties();
        }
        else if (shouldStartTimer)
        {
            _tickTimer.Start(CountdownTickInterval, OnTickTimerElapsed);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tickTimer.Stop();
        if (_ownsTickTimer)
        {
            _tickTimer.Dispose();
        }

        _zenAudio.Stop();
        _zenDisplay.MarkCleanExit();
        UpdateCompactPill(null);
    }

    private void OnTickTimerElapsed()
    {
        if (_disposed)
        {
            return;
        }

        Tick(_utcNowProvider());
    }

    private FocusSession? StopSessionCore(bool recordPartial, string nextStatus)
    {
        var now = _utcNowProvider().ToUniversalTime();
        FocusSession? toPersist = null;

        lock (_sync)
        {
            if (_runState == FocusTimerRunState.Running)
            {
                AdvanceElapsedSecondsLocked(now);
            }

            if (recordPartial && _activeSessionCompletedSeconds > 0 && _activeSessionStartedAtUtc.HasValue)
            {
                toPersist = new FocusSession
                {
                    SessionId = Guid.NewGuid().ToString("N"),
                    PhaseType = _currentPhase,
                    PlannedSeconds = _plannedSeconds,
                    CompletedSeconds = _activeSessionCompletedSeconds,
                    ZenNoiseType = _settings.DefaultNoiseType,
                    ScreenEffectUsed = _activeSessionUsedScreenEffect,
                    StartedAtUtc = _activeSessionStartedAtUtc.Value,
                    EndedAtUtc = now
                };
            }

            _runState = FocusTimerRunState.Idle;
            _activeSessionCompletedSeconds = 0;
            _activeSessionStartedAtUtc = null;
            _activeSessionUsedScreenEffect = false;
            _plannedSeconds = _settings.GetPlannedSeconds(_currentPhase);
            _remainingSeconds = _plannedSeconds;
            _statusMessage = nextStatus;
        }

        _tickTimer.Stop();
        _zenAudio.Stop();
        _zenDisplay.RestoreDisplay();

        return toPersist;
    }

    private void HandlePhaseCompleted(DateTimeOffset completedAtUtc)
    {
        FocusSession? completedRecord;
        FocusPhaseType finishedPhase;
        FocusPhaseType nextPhase;
        bool autoStart;

        lock (_sync)
        {
            finishedPhase = _currentPhase;
            var elapsedSeconds = Math.Max(_activeSessionCompletedSeconds, _plannedSeconds);
            completedRecord = _activeSessionStartedAtUtc.HasValue
                ? new FocusSession
                {
                    SessionId = Guid.NewGuid().ToString("N"),
                    PhaseType = finishedPhase,
                    PlannedSeconds = _plannedSeconds,
                    CompletedSeconds = elapsedSeconds,
                    ZenNoiseType = _settings.DefaultNoiseType,
                    ScreenEffectUsed = _activeSessionUsedScreenEffect,
                    StartedAtUtc = _activeSessionStartedAtUtc.Value,
                    EndedAtUtc = completedAtUtc
                }
                : null;

            if (finishedPhase == FocusPhaseType.Work)
            {
                _completedWorkCyclesInSet++;
            }

            nextPhase = ResolveNextPhaseLocked(wasCompleted: true);
            autoStart = _settings.AutoStartNextPhase;

            _currentPhase = nextPhase;
            _plannedSeconds = _settings.GetPlannedSeconds(nextPhase);
            _remainingSeconds = _plannedSeconds;
            _activeSessionCompletedSeconds = 0;
            _activeSessionStartedAtUtc = autoStart ? completedAtUtc : null;
            _activeSessionUsedScreenEffect = false;
            _lastTickUtc = completedAtUtc;
            _runState = autoStart ? FocusTimerRunState.Running : FocusTimerRunState.Idle;
            _statusMessage = finishedPhase == FocusPhaseType.Work ? "Focus phase complete" : "Break complete";
        }

        if (!autoStart)
        {
            _tickTimer.Stop();
            _zenAudio.Stop();
            _zenDisplay.RestoreDisplay();
            UpdateCompactPill(null);
        }
        else
        {
            ApplyZenEffectsForActiveSession(_settings);
            UpdateCompactPill(BuildCompactPillText());
        }

        if (completedRecord is not null)
        {
            _ = PersistCompletedSessionSafeAsync(completedRecord);
        }

        PublishCompletionAlert(finishedPhase, nextPhase);
        RaiseAllProperties();
    }

    private FocusPhaseType ResolveNextPhaseLocked(bool wasCompleted)
    {
        if (_currentPhase == FocusPhaseType.Work)
        {
            var cyclesCompleted = _completedWorkCyclesInSet;
            if (!wasCompleted)
            {
                return FocusPhaseType.ShortBreak;
            }

            return cyclesCompleted > 0 && cyclesCompleted % _settings.CyclesBeforeLongBreak == 0
                ? FocusPhaseType.LongBreak
                : FocusPhaseType.ShortBreak;
        }

        return FocusPhaseType.Work;
    }

    private void AdvanceElapsedSecondsLocked(DateTimeOffset utcNow)
    {
        if (utcNow <= _lastTickUtc)
        {
            return;
        }

        var deltaSeconds = (int)Math.Floor((utcNow - _lastTickUtc).TotalSeconds);
        if (deltaSeconds <= 0)
        {
            return;
        }

        var consumed = Math.Min(deltaSeconds, Math.Max(0, _remainingSeconds));
        _remainingSeconds -= consumed;
        _activeSessionCompletedSeconds += consumed;
        _lastTickUtc = _lastTickUtc.AddSeconds(deltaSeconds);
    }

    private void ApplyZenEffectsForActiveSession(FocusSettings settings)
    {
        if (settings.DefaultNoiseType != ZenNoiseType.None)
        {
            _zenAudio.Start(settings.DefaultNoiseType, settings.NoiseVolume);
        }
        else
        {
            _zenAudio.Stop();
        }

        ApplyZenDisplayOnly(settings);
    }

    private void ApplyZenDisplayOnly(FocusSettings settings)
    {
        var dim = settings.EnableScreenDim ? settings.DimOpacity : 0.0;
        if (settings.EnableGrayscale || dim > 0.001)
        {
            var applied = _zenDisplay.TryApplyEffect(
                new ZenDisplayOptions(settings.EnableGrayscale, dim, settings.PauseScreenEffectOnFullscreen));
            if (applied)
            {
                lock (_sync)
                {
                    _activeSessionUsedScreenEffect = true;
                }
            }
        }
        else
        {
            _zenDisplay.RestoreDisplay();
        }
    }

    private void PublishCompletionAlert(FocusPhaseType finishedPhase, FocusPhaseType nextPhase)
    {
        var title = finishedPhase switch
        {
            FocusPhaseType.Work => "Focus session complete",
            FocusPhaseType.ShortBreak => "Short break complete",
            FocusPhaseType.LongBreak => "Long break complete",
            _ => "Focus phase complete"
        };
        var body = $"Up next: {(nextPhase == FocusPhaseType.Work ? "Focus" : nextPhase == FocusPhaseType.ShortBreak ? "Short break" : "Long break")}";

        _context?.Events.Publish(new NotificationPeekContentEvent(FocusTimerWidgetModule.Id, title, body));
        _context?.Island.RequestPeekNotification(NotificationPriority.Critical, TimeSpan.FromSeconds(5));
    }

    private async Task PersistCompletedSessionSafeAsync(FocusSession session)
    {
        try
        {
            await _repository.RecordSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
            var stats = await _repository.GetRecentStatsAsync(days: 7, CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                _recentStats = stats;
            }

            DispatchToUi(() => OnPropertyChanged(nameof(StatsLabel)));
        }
        catch (Exception ex)
        {
            _context?.Logger.Warn("Failed to record Focus Timer session to SQLite repository.", ex);
        }
    }

    private async Task UpdateSettingsAsync(Func<FocusSettings, FocusSettings> transform, CancellationToken ct)
    {
        FocusSettings updated;
        lock (_sync)
        {
            updated = transform(_settings).Normalize();
            _settings = updated;
        }

        if (_context is not null)
        {
            await _context.Settings.WriteJsonAsync(JsonSerializer.Serialize(updated, JsonOptions), ct)
                .ConfigureAwait(false);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCompactPill(string? badge) =>
        _context?.Island.UpdateCompactPill(FocusTimerWidgetModule.Id, badge);

    private string BuildCompactPillText() => $"{CompactPhaseShortLabel} {CountdownDisplay}";

    internal static string FormatCountdown(int totalSeconds)
    {
        var safe = Math.Max(0, totalSeconds);
        var mins = safe / 60;
        var secs = safe % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{mins:D2}:{secs:D2}");
    }

    private static FocusSettings ParseSettingsOrDefault(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || string.Equals(rawJson.Trim(), "{}", StringComparison.Ordinal))
        {
            return new FocusSettings().Normalize();
        }

        try
        {
            return (JsonSerializer.Deserialize<FocusSettings>(rawJson, JsonOptions) ?? new FocusSettings()).Normalize();
        }
        catch (JsonException)
        {
            return new FocusSettings().Normalize();
        }
    }

    private void RaiseAllProperties()
    {
        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(IsTimerRunning));
            OnPropertyChanged(nameof(CurrentPhase));
            OnPropertyChanged(nameof(RunState));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsWorkSelected));
            OnPropertyChanged(nameof(IsShortBreakSelected));
            OnPropertyChanged(nameof(IsLongBreakSelected));
            OnPropertyChanged(nameof(RemainingSeconds));
            OnPropertyChanged(nameof(PlannedSeconds));
            OnPropertyChanged(nameof(CountdownDisplay));
            OnPropertyChanged(nameof(PhaseLabel));
            OnPropertyChanged(nameof(CompactPhaseShortLabel));
            OnPropertyChanged(nameof(PrimaryActionLabel));
            OnPropertyChanged(nameof(PrimaryActionAutomationName));
            OnPropertyChanged(nameof(ProgressFraction));
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(CycleStatusLabel));
            OnPropertyChanged(nameof(StatsLabel));
            OnPropertyChanged(nameof(SelectedNoiseType));
            OnPropertyChanged(nameof(IsNoiseNone));
            OnPropertyChanged(nameof(IsNoiseWhite));
            OnPropertyChanged(nameof(IsNoisePink));
            OnPropertyChanged(nameof(IsNoiseBrown));
            OnPropertyChanged(nameof(NoiseVolume));
            OnPropertyChanged(nameof(NoiseVolumePercent));
            OnPropertyChanged(nameof(NoiseVolumeLabel));
            OnPropertyChanged(nameof(IsGrayscaleEnabled));
            OnPropertyChanged(nameof(IsDimEnabled));
            OnPropertyChanged(nameof(DimOpacity));
            OnPropertyChanged(nameof(DimOpacityPercent));
            OnPropertyChanged(nameof(DimOpacityLabel));
            OnPropertyChanged(nameof(AutoStartNextPhase));
            OnPropertyChanged(nameof(PauseScreenEffectOnFullscreen));
            OnPropertyChanged(nameof(WorkMinutes));
            OnPropertyChanged(nameof(ShortBreakMinutes));
            OnPropertyChanged(nameof(LongBreakMinutes));
            OnPropertyChanged(nameof(CyclesBeforeLongBreak));
            OnPropertyChanged(nameof(HasActiveZenEffects));
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(ShowCrashRecoveryNotice));
            OnPropertyChanged(nameof(AccessibleStatus));
        });
    }

    private void DispatchToUi(Action action)
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher is not null)
        {
            dispatcher(action);
        }
        else
        {
            action();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
