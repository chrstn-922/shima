using FluentAssertions;
using NSubstitute;
using Shima.Core.Contracts;
using Shima.WindowsInterop.Audio;
using Shima.WindowsInterop.Media;
using Xunit;

namespace Shima.WindowsInterop.Tests;

public sealed class MediaAndAudioTests
{
    [Fact]
    public async Task MediaSessionAdapter_MissingArtworkAndUnsupportedSeek_FallbackGracefully()
    {
        var now = new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
        var source = new FakeMediaSessionSource();
        var handle = new FakeMediaSessionHandle("Spotify.exe")
        {
            Properties = new RawMediaProperties("Nightcall", "Kavinsky", "OutRun", null, null),
            Playback = new RawPlaybackInfo(
                MediaPlaybackState.Playing,
                CanPlayPause: true,
                CanSkipNext: true,
                CanSkipPrevious: true,
                IsPlaybackPositionEnabled: false),
            Timeline = new RawTimelineInfo(
                TimeSpan.Zero,
                TimeSpan.FromMinutes(4),
                TimeSpan.FromSeconds(42),
                now)
        };
        source.CurrentHandle = handle;

        using var adapter = new MediaSessionAdapter(source, utcNowProvider: () => now);
        MediaSessionSnapshot? lastEmitted = null;
        adapter.SessionChanged += (_, snap) => lastEmitted = snap;

        await adapter.StartAsync();

        adapter.CurrentSession.Should().NotBeNull();
        lastEmitted.Should().NotBeNull();
        adapter.CurrentSession!.Title.Should().Be("Nightcall");
        adapter.CurrentSession.ThumbnailBytes.Should().BeNull();
        adapter.CurrentSession.IsPlaybackPositionEnabled.Should().BeFalse();
        adapter.CurrentSession.CanSeek.Should().BeFalse();

        // Seek guard MUST refuse invoking OS seek when IsPlaybackPositionEnabled is false
        var seekResult = await adapter.SeekAsync(TimeSpan.FromSeconds(90));
        seekResult.Should().BeFalse();
        handle.SeekRequestsTicks.Should().BeEmpty();

        // Metadata property cache: playback status change must NOT re-query artwork/properties stream
        handle.GetMediaPropertiesCalls.Should().Be(1);
        handle.Playback = handle.Playback! with { PlaybackState = MediaPlaybackState.Paused };
        handle.RaisePlaybackInfoChanged();
        await WaitUntilAsync(() => adapter.CurrentSession?.PlaybackState == MediaPlaybackState.Paused);
        handle.GetMediaPropertiesCalls.Should().Be(1);
    }

    [Fact]
    public async Task MediaSessionAdapter_SupportedSeekClampsPosition_AndSessionSwitchDisposesPriorHandle()
    {
        var now = new DateTimeOffset(2026, 11, 22, 8, 5, 0, TimeSpan.Zero);
        var source = new FakeMediaSessionSource();
        var handle1 = new FakeMediaSessionHandle("msedge.exe")
        {
            Properties = new RawMediaProperties("Track 1", "Artist 1", "Album", [1, 2, 3, 4], "image/png"),
            Playback = new RawPlaybackInfo(MediaPlaybackState.Playing, true, true, true, IsPlaybackPositionEnabled: true),
            Timeline = new RawTimelineInfo(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(110), TimeSpan.FromSeconds(30), now)
        };
        source.CurrentHandle = handle1;

        using var adapter = new MediaSessionAdapter(source, utcNowProvider: () => now);
        await adapter.StartAsync();

        adapter.CurrentSession!.Duration.Should().Be(TimeSpan.FromSeconds(100));
        adapter.CurrentSession.Position.Should().Be(TimeSpan.FromSeconds(20));
        adapter.CurrentSession.CanSeek.Should().BeTrue();
        adapter.CurrentSession.ThumbnailBytes.Should().Equal(1, 2, 3, 4);

        // Seek past upper bound clamps cleanly to StartTime + Duration (10 + 100 = 110s)
        (await adapter.SeekAsync(TimeSpan.FromSeconds(999))).Should().BeTrue();
        handle1.SeekRequestsTicks.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(110).Ticks);

        // Transport commands delegate cleanly
        (await adapter.TogglePlayPauseAsync()).Should().BeTrue();
        (await adapter.NextTrackAsync()).Should().BeTrue();
        (await adapter.PreviousTrackAsync()).Should().BeTrue();
        handle1.TogglePlayPauseCalls.Should().Be(1);
        handle1.NextCalls.Should().Be(1);
        handle1.PreviousCalls.Should().Be(1);

        // Switch session -> old handle disposed cleanly and new session emits
        var handle2 = new FakeMediaSessionHandle("vlc.exe")
        {
            Properties = new RawMediaProperties("Track 2", "Artist 2", "", null, null),
            Playback = new RawPlaybackInfo(MediaPlaybackState.Playing, true, false, false, false),
            Timeline = new RawTimelineInfo(TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.Zero, now)
        };
        source.CurrentHandle = handle2;
        source.RaiseCurrentSessionChanged();
        await WaitUntilAsync(() => adapter.CurrentSession?.SourceAppId == "vlc.exe");
        handle1.IsDisposed.Should().BeTrue();

        // Session cleared -> emits null snapshot
        source.CurrentHandle = null;
        source.RaiseCurrentSessionChanged();
        await WaitUntilAsync(() => adapter.CurrentSession is null);
        handle2.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task AudioMixerAdapter_DebouncesRapidSessionChurnFor150ms_AndStopsIdleTimerWhenSettled()
    {
        var now = new DateTimeOffset(2026, 11, 22, 8, 10, 0, TimeSpan.Zero);
        var timer = new FakeAudioDebounceTimer();
        var s1 = CreateSession("app:chrome", 101, "Chrome", 0.8f);
        var s2 = CreateSession("app:beep", 202, "CLI Alert", 0.5f);
        var backend = new FakeAudioMixerBackend(new AudioDeviceSnapshot(true, "dev1", "Speakers", 0.7f, false, [s1, s2]));

        using var adapter = new AudioMixerAdapter(backend, timer, () => now);
        var events = new List<AudioMixerSnapshot>();
        adapter.MixerChanged += (_, snap) => events.Add(snap);

        await adapter.StartAsync();
        adapter.CurrentSnapshot.Sessions.Should().HaveCount(2);
        timer.IsRunning.Should().BeFalse();

        // 1. Transient session churn: s2 disconnects, then recovers within 100ms (< 150ms) -> zero row removal!
        backend.RaiseSessionRemovedOrExpired("app:beep");
        timer.IsRunning.Should().BeTrue();
        timer.LastScheduledDelay.Should().Be(AudioMixerAdapter.SessionRemovalDebounceDelay);
        adapter.CurrentSnapshot.Sessions.Select(s => s.SessionKey).Should().Contain("app:beep");

        now = now.AddMilliseconds(100);
        backend.RaiseSessionUpserted(s2 with { State = AudioSessionActivityState.Active });
        timer.IsRunning.Should().BeFalse();
        adapter.CurrentSnapshot.Sessions.Select(s => s.SessionKey).Should().Contain("app:beep");

        // 2. Disconnect s2 at T=100 (expires T=250) and s1 at T=150 (expires T=300)
        backend.RaiseSessionRemovedOrExpired("app:beep");
        timer.LastScheduledDelay.Should().Be(TimeSpan.FromMilliseconds(150));

        now = now.AddMilliseconds(50); // T=150
        backend.RaiseSessionRemovedOrExpired("app:chrome");

        // Advance to T=250 -> s2 removed, timer reschedules remaining 50ms for s1
        now = now.AddMilliseconds(100); // T=250
        timer.Fire();
        adapter.CurrentSnapshot.Sessions.Select(s => s.SessionKey).Should().Equal("app:chrome");
        timer.IsRunning.Should().BeTrue();
        timer.LastScheduledDelay.Should().Be(TimeSpan.FromMilliseconds(50));

        // Advance to T=300 -> s1 removed, debounce queue empty -> timer MUST stop (0% idle CPU)
        now = now.AddMilliseconds(50); // T=300
        timer.Fire();
        adapter.CurrentSnapshot.Sessions.Should().BeEmpty();
        timer.IsRunning.Should().BeFalse();
        events.Last().Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task AudioMixerAdapter_SyncsBidirectionally_AndRebindsOnDefaultDeviceSwitch()
    {
        var now = new DateTimeOffset(2026, 11, 22, 8, 20, 0, TimeSpan.Zero);
        var timer = new FakeAudioDebounceTimer();
        var s1 = CreateSession("app:spotify", 303, "Spotify", 0.6f);
        var backend = new FakeAudioMixerBackend(new AudioDeviceSnapshot(true, "speakers", "Realtek Speakers", 0.5f, false, [s1]));

        using var adapter = new AudioMixerAdapter(backend, timer, () => now);
        await adapter.StartAsync();

        // Outbound clamping + optimistic sync
        (await adapter.SetMasterVolumeAsync(1.5f)).Should().BeTrue();
        backend.LastMasterVolumeSet.Should().Be(1.0f);
        adapter.CurrentSnapshot.MasterVolume.Should().Be(1.0f);

        (await adapter.SetSessionMuteAsync("app:spotify", true)).Should().BeTrue();
        adapter.CurrentSnapshot.Sessions.Single().IsMuted.Should().BeTrue();

        // External Windows change syncs inward immediately
        backend.RaiseSessionVolumeChanged("app:spotify", 0.25f, false);
        backend.RaiseMasterVolumeChanged(0.4f, true);
        adapter.CurrentSnapshot.MasterVolume.Should().Be(0.4f);
        adapter.CurrentSnapshot.IsMasterMuted.Should().BeTrue();
        adapter.CurrentSnapshot.Sessions.Single().Volume.Should().Be(0.25f);
        adapter.CurrentSnapshot.Sessions.Single().IsMuted.Should().BeFalse();

        // Pending removal cancels cleanly across default output device hot-plug
        backend.RaiseSessionRemovedOrExpired("app:spotify");
        timer.IsRunning.Should().BeTrue();

        var headsetSession = CreateSession("app:teams", 404, "Microsoft Teams", 0.9f);
        backend.NextRebindSnapshot = new AudioDeviceSnapshot(true, "bt", "Bluetooth Headset", 0.75f, false, [headsetSession]);
        backend.RaiseDefaultDeviceChanged();

        await WaitUntilAsync(() => adapter.CurrentSnapshot.DeviceId == "bt");
        timer.IsRunning.Should().BeFalse();
        adapter.CurrentSnapshot.DeviceName.Should().Be("Bluetooth Headset");
        adapter.CurrentSnapshot.MasterVolume.Should().Be(0.75f);
        adapter.CurrentSnapshot.Sessions.Should().ContainSingle(s => s.SessionKey == "app:teams");
    }

    [Fact]
    public void Win32AppIconResolver_ExtractsBgraIconWithoutSystemDrawing_AndHandlesMissingPaths()
    {
        var resolver = new Win32AppIconResolver();
        var exePath = Environment.ProcessPath;
        exePath.Should().NotBeNullOrWhiteSpace();

        var systemSoundsName = resolver.ResolveDisplayName(0, "@AudioSrv.Dll,-203", exePath, isSystemSounds: true);
        systemSoundsName.Should().Be("System Sounds");

        var missingIcon = resolver.TryResolveIcon(@"C:\nonexistent\shima-missing-binary.exe", null);
        missingIcon.Should().BeNull();

        // Shell32 or current process host icon must convert cleanly via GDI GetDIBits to RawBgraImage
        var resolvedIcon = resolver.TryResolveIcon(exePath, null);
        resolvedIcon.Should().NotBeNull();
        resolvedIcon!.IsValid.Should().BeTrue();
        resolvedIcon.Pixels.Length.Should().Be(resolvedIcon.Width * resolvedIcon.Height * 4);
    }

    private static AudioSessionSnapshot CreateSession(string key, uint pid, string name, float volume) => new()
    {
        SessionKey = key,
        ProcessId = pid,
        DisplayName = name,
        ProcessName = name.ToLowerInvariant(),
        Volume = volume,
        IsMuted = false,
        IsSystemSounds = false,
        State = AudioSessionActivityState.Active
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 >= deadline)
            {
                break;
            }

            await Task.Delay(15);
        }

        predicate().Should().BeTrue();
    }

    private sealed class FakeMediaSessionSource : IMediaSessionSource
    {
        public IMediaSessionHandle? CurrentHandle { get; set; }

        public event EventHandler? CurrentSessionChanged;

        public Task<bool> InitializeAsync(CancellationToken ct = default) => Task.FromResult(true);

        public IMediaSessionHandle? GetCurrentSession() => CurrentHandle;

        public void Shutdown()
        {
        }

        public void Dispose()
        {
        }

        public void RaiseCurrentSessionChanged() => CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeMediaSessionHandle(string sourceAppId) : IMediaSessionHandle
    {
        public string SourceAppId { get; } = sourceAppId;

        public RawMediaProperties? Properties { get; set; }

        public RawPlaybackInfo? Playback { get; set; }

        public RawTimelineInfo? Timeline { get; set; }

        public int GetMediaPropertiesCalls { get; private set; }

        public int TogglePlayPauseCalls { get; private set; }

        public int NextCalls { get; private set; }

        public int PreviousCalls { get; private set; }

        public List<long> SeekRequestsTicks { get; } = [];

        public bool IsDisposed { get; private set; }

        public event EventHandler? MediaPropertiesChanged;

        public event EventHandler? PlaybackInfoChanged;

        public event EventHandler? TimelinePropertiesChanged;

        public Task<RawMediaProperties?> TryGetMediaPropertiesAsync(CancellationToken ct = default)
        {
            GetMediaPropertiesCalls++;
            return Task.FromResult(Properties);
        }

        public RawPlaybackInfo? GetPlaybackInfo() => Playback;

        public RawTimelineInfo? GetTimelineProperties() => Timeline;

        public Task<bool> TryTogglePlayPauseAsync(CancellationToken ct = default)
        {
            TogglePlayPauseCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> TrySkipNextAsync(CancellationToken ct = default)
        {
            NextCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> TrySkipPreviousAsync(CancellationToken ct = default)
        {
            PreviousCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> TrySeekAsync(long positionTicks, CancellationToken ct = default)
        {
            SeekRequestsTicks.Add(positionTicks);
            return Task.FromResult(true);
        }

        public void RaisePlaybackInfoChanged() => PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseMediaPropertiesChanged() => MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseTimelinePropertiesChanged() => TimelinePropertiesChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeAudioDebounceTimer : IAudioDebounceTimer
    {
        private Action? _callback;

        public bool IsRunning { get; private set; }

        public TimeSpan? LastScheduledDelay { get; private set; }

        public void Schedule(TimeSpan delay, Action onElapsed)
        {
            LastScheduledDelay = delay;
            _callback = onElapsed;
            IsRunning = true;
        }

        public void Stop()
        {
            IsRunning = false;
            _callback = null;
        }

        public void Fire()
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            var cb = _callback;
            _callback = null;
            cb?.Invoke();
        }

        public void Dispose() => Stop();
    }

    private sealed class FakeAudioMixerBackend : IAudioMixerBackend
    {
        private readonly AudioDeviceSnapshot _initialSnapshot;

        public FakeAudioMixerBackend(AudioDeviceSnapshot initialSnapshot)
        {
            _initialSnapshot = initialSnapshot;
            NextRebindSnapshot = initialSnapshot;
        }

        public AudioDeviceSnapshot NextRebindSnapshot { get; set; }

        public float? LastMasterVolumeSet { get; private set; }

        public event EventHandler<MasterVolumeChangedArgs>? MasterVolumeChanged;

        public event EventHandler<SessionVolumeChangedArgs>? SessionVolumeChanged;

        public event EventHandler<AudioSessionSnapshot>? SessionUpserted;

        public event EventHandler<string>? SessionRemovedOrExpired;

        public event EventHandler? DefaultDeviceChanged;

        public Task<AudioDeviceSnapshot> StartAsync(CancellationToken ct = default) => Task.FromResult(_initialSnapshot);

        public void Stop()
        {
        }

        public Task<AudioDeviceSnapshot> RebindDefaultDeviceAsync(CancellationToken ct = default) =>
            Task.FromResult(NextRebindSnapshot);

        public Task<bool> SetMasterVolumeAsync(float volume, CancellationToken ct = default)
        {
            LastMasterVolumeSet = volume;
            return Task.FromResult(true);
        }

        public Task<bool> SetMasterMuteAsync(bool isMuted, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> SetSessionVolumeAsync(string sessionKey, float volume, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> SetSessionMuteAsync(string sessionKey, bool isMuted, CancellationToken ct = default) =>
            Task.FromResult(true);

        public void RaiseMasterVolumeChanged(float volume, bool muted) =>
            MasterVolumeChanged?.Invoke(this, new MasterVolumeChangedArgs(volume, muted));

        public void RaiseSessionVolumeChanged(string key, float volume, bool muted) =>
            SessionVolumeChanged?.Invoke(this, new SessionVolumeChangedArgs(key, volume, muted));

        public void RaiseSessionUpserted(AudioSessionSnapshot snapshot) =>
            SessionUpserted?.Invoke(this, snapshot);

        public void RaiseSessionRemovedOrExpired(string key) =>
            SessionRemovedOrExpired?.Invoke(this, key);

        public void RaiseDefaultDeviceChanged() => DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }
}
