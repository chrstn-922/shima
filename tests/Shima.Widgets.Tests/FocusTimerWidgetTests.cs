using System.Windows;
using System.Windows.Threading;
using FluentAssertions;
using NSubstitute;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Widgets.FocusTimer;
using Shima.Widgets.FocusTimer.Views;
using Xunit;

namespace Shima.Widgets.Tests;

public sealed class FocusTimerWidgetTests
{
    [Fact]
    public async Task FocusTimerWidgetModule_IdleTimerIsStopped_PomodoroCyclesTransitionAndRecordStats()
    {
        var now = new DateTimeOffset(2026, 11, 22, 13, 0, 0, TimeSpan.Zero);
        var repo = new InMemoryFocusRepository();
        var zenAudio = new FakeZenAudioSynthAdapter();
        var zenDisplay = new FakeZenDisplayEffectAdapter();
        var tickTimer = new FakeFocusTickTimer();
        var events = new InMemoryEventDispatcher();
        var island = Substitute.For<IIslandAttentionRequester>();

        var peeks = new List<NotificationPeekContentEvent>();
        using var sub = events.Subscribe<NotificationPeekContentEvent>(e => peeks.Add(e));

        await using var module = new FocusTimerWidgetModule(
            repo,
            zenAudio,
            zenDisplay,
            tickTimer,
            utcNowProvider: () => now);
        await module.InitializeAsync(CreateContext(events, island), CancellationToken.None);

        // Zero idle polling when inactive
        module.IsTimerRunning.Should().BeFalse();
        module.ViewModel.CountdownDisplay.Should().Be("25:00");

        // Configure 1-minute phases and 2 work cycles before LongBreak
        await module.ViewModel.UpdateIntervalsAsync(
            workMinutes: 1,
            shortBreakMinutes: 1,
            longBreakMinutes: 2,
            cyclesBeforeLongBreak: 2,
            autoStartNextPhase: false);
        await module.ViewModel.SetNoiseTypeAsync(ZenNoiseType.Pink);
        await module.ViewModel.ToggleGrayscaleAsync();

        module.ViewModel.CountdownDisplay.Should().Be("01:00");

        // Start Cycle 1 Work
        module.ViewModel.Start();
        module.IsTimerRunning.Should().BeTrue();
        tickTimer.LastInterval.Should().Be(TimeSpan.FromSeconds(1));
        zenAudio.IsPlaying.Should().BeTrue();
        zenAudio.ActiveNoiseType.Should().Be(ZenNoiseType.Pink);
        zenDisplay.IsEffectActive.Should().BeTrue();
        island.Received().UpdateCompactPill(FocusTimerWidgetModule.Id, "Focus 01:00");

        // Advance 30s
        now = now.AddSeconds(30);
        tickTimer.FireTick();
        module.ViewModel.CountdownDisplay.Should().Be("00:30");
        module.ViewModel.ProgressPercent.Should().Be(50.0);
        island.Received().UpdateCompactPill(FocusTimerWidgetModule.Id, "Focus 00:30");

        // Complete Cycle 1 Work -> stops timers/audio/display, records DB session, emits critical peek, moves to ShortBreak
        now = now.AddSeconds(30);
        tickTimer.FireTick();
        await Task.Delay(50);

        module.IsTimerRunning.Should().BeFalse();
        zenAudio.IsPlaying.Should().BeFalse();
        zenDisplay.IsEffectActive.Should().BeFalse();
        module.ViewModel.CurrentPhase.Should().Be(FocusPhaseType.ShortBreak);
        module.ViewModel.CountdownDisplay.Should().Be("01:00");
        module.ViewModel.StatsLabel.Should().Be("1 session · 1m");
        repo.Sessions.Should().ContainSingle(s => s.PhaseType == FocusPhaseType.Work && s.CompletedSeconds == 60);
        peeks.Should().ContainSingle();
        peeks[0].WidgetId.Should().Be(FocusTimerWidgetModule.Id);
        island.Received().RequestPeekNotification(NotificationPriority.Critical, TimeSpan.FromSeconds(5));
        island.Received().UpdateCompactPill(FocusTimerWidgetModule.Id, null);

        // Complete ShortBreak -> advances cleanly back to Work
        module.ViewModel.Start();
        now = now.AddSeconds(60);
        tickTimer.FireTick();
        module.ViewModel.CurrentPhase.Should().Be(FocusPhaseType.Work);

        // Complete Cycle 2 Work -> transitions to LongBreak
        module.ViewModel.Start();
        now = now.AddSeconds(60);
        tickTimer.FireTick();
        module.ViewModel.CurrentPhase.Should().Be(FocusPhaseType.LongBreak);
        module.ViewModel.CountdownDisplay.Should().Be("02:00");
    }

    [Fact]
    public async Task FocusTimerWidgetModule_PauseFullscreenDisplayOffAndDispose_RestoreDisplayAndStopTimers()
    {
        var now = new DateTimeOffset(2026, 11, 22, 13, 30, 0, TimeSpan.Zero);
        var repo = new InMemoryFocusRepository();
        var zenAudio = new FakeZenAudioSynthAdapter();
        var zenDisplay = new FakeZenDisplayEffectAdapter { SimulateCrashRecoveryOnStartup = true };
        var tickTimer = new FakeFocusTickTimer();

        await using var module = new FocusTimerWidgetModule(
            repo,
            zenAudio,
            zenDisplay,
            tickTimer,
            utcNowProvider: () => now);
        await module.InitializeAsync(CreateContext(new InMemoryEventDispatcher(), Substitute.For<IIslandAttentionRequester>()), CancellationToken.None);

        zenDisplay.RecoverCalls.Should().Be(1);
        module.ViewModel.ShowCrashRecoveryNotice.Should().BeTrue();

        await module.ViewModel.SetNoiseTypeAsync(ZenNoiseType.Brown);
        await module.ViewModel.ToggleScreenDimAsync();
        module.ViewModel.Start();
        module.IsTimerRunning.Should().BeTrue();
        zenAudio.IsPlaying.Should().BeTrue();
        zenDisplay.IsEffectActive.Should().BeTrue();

        // Fullscreen pauses Zen display effect temporarily and restores on exit
        module.OnShellStateChanged(IslandShellState.Fullscreen);
        zenDisplay.IsPausedByFullscreen.Should().BeTrue();
        zenDisplay.IsEffectActive.Should().BeFalse();

        module.OnShellStateChanged(IslandShellState.Collapsed);
        zenDisplay.IsPausedByFullscreen.Should().BeFalse();
        zenDisplay.IsEffectActive.Should().BeTrue();

        // Pause halts timers and restores audio/display
        module.ViewModel.Pause();
        module.ViewModel.RunState.Should().Be(FocusTimerRunState.Paused);
        module.IsTimerRunning.Should().BeFalse();
        zenAudio.IsPlaying.Should().BeFalse();
        zenDisplay.IsEffectActive.Should().BeFalse();

        // Resume restarts timer and Zen effects
        module.ViewModel.Resume();
        module.ViewModel.RunState.Should().Be(FocusTimerRunState.Running);
        module.IsTimerRunning.Should().BeTrue();
        zenAudio.IsPlaying.Should().BeTrue();
        zenDisplay.IsEffectActive.Should().BeTrue();

        // DisplayOff suspends timer/audio/display
        module.OnPowerProfileChanged(PowerProfile.DisplayOff);
        module.IsTimerRunning.Should().BeFalse();
        zenAudio.IsPlaying.Should().BeFalse();
        zenDisplay.IsEffectActive.Should().BeFalse();

        await module.DisposeAsync();
        zenDisplay.CleanExitMarked.Should().BeTrue();
    }

    [Fact]
    public async Task FocusTimerViews_InstantiateAndBindCleanlyOnStaWpfDispatcher()
    {
        var now = new DateTimeOffset(2026, 11, 22, 13, 45, 0, TimeSpan.Zero);
        await using var module = new FocusTimerWidgetModule(
            new InMemoryFocusRepository(),
            new FakeZenAudioSynthAdapter(),
            new FakeZenDisplayEffectAdapter(),
            new FakeFocusTickTimer(),
            utcNowProvider: () => now);
        await module.InitializeAsync(CreateContext(new InMemoryEventDispatcher(), Substitute.For<IIslandAttentionRequester>()), CancellationToken.None);

        Exception? staException = null;
        var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                var expanded = module.CreateExpandedView().Should().BeOfType<FocusTimerView>().Subject;
                expanded.DataContext.Should().BeSameAs(module.ViewModel);
                expanded.Measure(new Size(400, 168));
                expanded.Arrange(new Rect(0, 0, 400, 168));

                var compact = module.CreateCompactActiveView().Should().BeOfType<FocusTimerCompactView>().Subject;
                compact.DataContext.Should().BeSameAs(module.ViewModel);
                compact.Measure(new Size(220, 32));
                compact.Arrange(new Rect(0, 0, 220, 32));

                var settings = module.CreateSettingsView().Should().BeOfType<FocusTimerSettingsView>().Subject;
                settings.DataContext.Should().BeSameAs(module.ViewModel);
                settings.Measure(new Size(600, 500));
                settings.Arrange(new Rect(0, 0, 600, 500));
            }
            catch (Exception ex)
            {
                staException = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        completed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        staException.Should().BeNull();
    }

    private static IWidgetContext CreateContext(IEventDispatcher events, IIslandAttentionRequester island)
    {
        string? json = "{}";
        var settingsStore = Substitute.For<IWidgetSettingsStore>();
        settingsStore.ReadJsonAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<string?>(json));
        settingsStore.WriteJsonAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                json = ci.Arg<string>();
                return Task.CompletedTask;
            });

        var context = Substitute.For<IWidgetContext>();
        context.WidgetId.Returns(FocusTimerWidgetModule.Id);
        context.Events.Returns(events);
        context.Settings.Returns(settingsStore);
        context.Theme.Returns(Substitute.For<IThemeTokenProvider>());
        context.Island.Returns(island);
        context.Logger.Returns(Substitute.For<ILogger>());
        return context;
    }

    private sealed class FakeFocusTickTimer : IFocusTickTimer
    {
        private Action? _onTick;

        public bool IsRunning { get; private set; }
        public TimeSpan? LastInterval { get; private set; }

        public void Start(TimeSpan interval, Action onTick)
        {
            LastInterval = interval;
            _onTick = onTick;
            IsRunning = true;
        }

        public void Stop()
        {
            IsRunning = false;
            _onTick = null;
        }

        public void FireTick()
        {
            if (IsRunning)
            {
                _onTick?.Invoke();
            }
        }

        public void Dispose() => Stop();
    }

    private sealed class FakeZenAudioSynthAdapter : IZenAudioSynthAdapter
    {
        public bool IsPlaying { get; private set; }
        public ZenNoiseType ActiveNoiseType { get; private set; } = ZenNoiseType.None;
        public double Volume { get; private set; } = 0.35;

        public void Start(ZenNoiseType noiseType, double volume)
        {
            Volume = volume;
            if (noiseType == ZenNoiseType.None)
            {
                Stop();
                return;
            }

            ActiveNoiseType = noiseType;
            IsPlaying = true;
        }

        public void UpdateVolume(double volume) => Volume = volume;

        public void Stop()
        {
            IsPlaying = false;
            ActiveNoiseType = ZenNoiseType.None;
        }

        public void Dispose() => Stop();
    }

    private sealed class FakeZenDisplayEffectAdapter : IZenDisplayEffectAdapter
    {
        private bool _logicalActive;

        public bool SimulateCrashRecoveryOnStartup { get; set; }
        public int RecoverCalls { get; private set; }
        public bool CleanExitMarked { get; private set; }
        public bool IsPausedByFullscreen { get; private set; }
        public bool WasCrashRecoveredOnStartup { get; private set; }
        public bool IsEffectActive => _logicalActive && !IsPausedByFullscreen;

        public bool RecoverFromStartupIfNeeded()
        {
            RecoverCalls++;
            WasCrashRecoveredOnStartup = SimulateCrashRecoveryOnStartup;
            return WasCrashRecoveredOnStartup;
        }

        public bool TryApplyEffect(ZenDisplayOptions options)
        {
            _logicalActive = options.GrayscaleEnabled || options.DimOpacity > 0.001;
            return true;
        }

        public void RestoreDisplay() => _logicalActive = false;

        public void MarkCleanExit()
        {
            _logicalActive = false;
            CleanExitMarked = true;
        }

        public void SetPausedByFullscreen(bool isFullscreen) => IsPausedByFullscreen = isFullscreen;

        public void Dispose() => MarkCleanExit();
    }

    private sealed class InMemoryFocusRepository : IFocusRepository
    {
        public List<FocusSession> Sessions { get; } = [];

        public Task<FocusSession> RecordSessionAsync(FocusSession session, CancellationToken ct = default)
        {
            Sessions.Add(session);
            return Task.FromResult(session);
        }

        public Task<FocusStatsSummary> GetRecentStatsAsync(int days = 7, CancellationToken ct = default)
        {
            var work = Sessions.Where(s => s.PhaseType == FocusPhaseType.Work && s.CompletedSeconds > 0).ToList();
            return Task.FromResult(new FocusStatsSummary(
                work.Count,
                work.Sum(s => s.CompletedSeconds),
                work.LastOrDefault()?.EndedAtUtc));
        }

        public Task<IReadOnlyList<FocusSession>> GetRecentSessionsAsync(int limit = 20, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FocusSession>>(Sessions.OrderByDescending(s => s.StartedAtUtc).Take(limit).ToArray());
    }
}
