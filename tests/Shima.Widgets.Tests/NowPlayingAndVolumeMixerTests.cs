using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluentAssertions;
using NSubstitute;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Widgets.NowPlaying;
using Shima.Widgets.NowPlaying.Views;
using Shima.Widgets.VolumeMixer;
using Shima.Widgets.VolumeMixer.Views;
using Xunit;

namespace Shima.Widgets.Tests;

public sealed class NowPlayingAndVolumeMixerTests
{
    [Fact]
    public async Task NowPlayingWidget_CompactPill_SeekFallback_AndTimerPowerLifecycle()
    {
        var now = new DateTimeOffset(2026, 11, 22, 8, 30, 0, TimeSpan.Zero);
        var adapter = new FakeMediaSessionAdapter
        {
            CurrentSession = new MediaSessionSnapshot
            {
                SourceAppId = "Spotify.exe",
                Title = "Midnight City",
                Artist = "M83",
                AlbumTitle = "Hurry Up, We're Dreaming",
                PlaybackState = MediaPlaybackState.Playing,
                CanPlayPause = true,
                CanSkipNext = true,
                CanSkipPrevious = true,
                IsPlaybackPositionEnabled = false,
                Position = TimeSpan.FromSeconds(15),
                Duration = TimeSpan.FromSeconds(240),
                LastUpdatedUtc = now,
                ThumbnailBytes = null
            }
        };
        var timer = new FakeNowPlayingTimer();
        var pinController = new OverlayPinController();
        using var stateManager = new IslandStateManager(pinController);
        var requester = new IslandAttentionRequester(stateManager);
        var context = CreateContext(NowPlayingWidgetModule.Id, requester);

        await using var module = new NowPlayingWidgetModule(adapter, () => now, timer);
        await module.InitializeAsync(context, CancellationToken.None);

        // Compact active pill activates immediately on active playback, with zero progress timer running while Collapsed
        stateManager.CurrentState.Should().Be(IslandShellState.Active);
        stateManager.CompactActiveWidgetId.Should().Be(NowPlayingWidgetModule.Id);
        stateManager.CompactPillBadgeText.Should().Be("Midnight City");
        module.IsProgressTimerRunning.Should().BeFalse();

        // Missing artwork + disabled seek fallback
        module.ViewModel.HasSession.Should().BeTrue();
        module.ViewModel.HasArtwork.Should().BeFalse();
        module.ViewModel.ArtworkBitmap.Should().BeNull();
        module.ViewModel.CanSeek.Should().BeFalse();
        module.ViewModel.ShowSeekUnsupportedHint.Should().BeTrue();
        (await module.ViewModel.SeekAsync(TimeSpan.FromSeconds(50))).Should().BeFalse();
        adapter.SeekCalls.Should().BeEmpty();

        // Activate -> 500ms interpolation timer starts on AC
        await module.ActivateAsync(WidgetActivationReason.UserSelection);
        module.IsProgressTimerRunning.Should().BeTrue();
        timer.LastInterval.Should().Be(TimeSpan.FromMilliseconds(500));

        // Ticking 10s advances ElapsedText cleanly without polling GSMTC
        now = now.AddSeconds(10);
        timer.Fire();
        module.ViewModel.ElapsedText.Should().Be("0:25");

        // EnergySaver throttles interpolation timer to 1000ms
        module.OnPowerProfileChanged(PowerProfile.EnergySaver);
        timer.LastInterval.Should().Be(TimeSpan.FromMilliseconds(1000));

        // Collapse, Fullscreen, DisplayOff immediately stop timer
        module.OnShellStateChanged(IslandShellState.Collapsed);
        module.IsProgressTimerRunning.Should().BeFalse();
        module.OnShellStateChanged(IslandShellState.Expanded);
        module.IsProgressTimerRunning.Should().BeTrue();
        module.OnPowerProfileChanged(PowerProfile.DisplayOff);
        module.IsProgressTimerRunning.Should().BeFalse();
        module.OnPowerProfileChanged(PowerProfile.AC);
        module.IsProgressTimerRunning.Should().BeTrue();

        // Session switches to seekable paused track -> clears compact Active pill and stops progress timer
        adapter.RaiseSession(adapter.CurrentSession! with
        {
            PlaybackState = MediaPlaybackState.Paused,
            IsPlaybackPositionEnabled = true
        });
        stateManager.CurrentState.Should().Be(IslandShellState.Collapsed);
        module.IsProgressTimerRunning.Should().BeFalse();
        module.ViewModel.CanSeek.Should().BeTrue();
        (await module.ViewModel.SeekAsync(TimeSpan.FromSeconds(80))).Should().BeTrue();
        adapter.SeekCalls.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(80));
        module.ViewModel.ElapsedText.Should().Be("1:20");

        // Another widget's compact pill clearing call MUST NOT clear Now Playing's pill
        adapter.RaiseSession(adapter.CurrentSession! with { PlaybackState = MediaPlaybackState.Playing });
        stateManager.CompactActiveWidgetId.Should().Be(NowPlayingWidgetModule.Id);
        requester.UpdateCompactPill("shima.focus", null);
        stateManager.CompactActiveWidgetId.Should().Be(NowPlayingWidgetModule.Id);

        // Hiding compact pill setting clears compact active state
        await module.ViewModel.SetShowCompactActivePillAsync(false);
        stateManager.CurrentState.Should().Be(IslandShellState.Collapsed);

        await module.ResetSettingsAsync();
        module.ViewModel.ShowCompactActivePill.Should().BeTrue();
        stateManager.CurrentState.Should().Be(IslandShellState.Active);

        await module.DeactivateAsync();
        module.IsProgressTimerRunning.Should().BeFalse();
    }

    [Fact]
    public async Task VolumeMixerWidget_BidirectionalSync_PreservesDragRowInstances_AndFiltersSystemSounds()
    {
        var iconPixels = new byte[16 * 16 * 4];
        Array.Fill(iconPixels, (byte)255);
        var validIcon = new RawBgraImage(16, 16, iconPixels);

        var chromeSession = CreateAudioSession("app:chrome", 111, "Google Chrome", 0.5f, isSystemSounds: false, icon: validIcon);
        var sysSession = CreateAudioSession("app:sys", 0, "System Sounds", 0.9f, isSystemSounds: true, icon: null);
        var adapter = new FakeAudioMixerAdapter
        {
            CurrentSnapshot = new AudioMixerSnapshot
            {
                DeviceId = "dev-1",
                DeviceName = "Headphones",
                MasterVolume = 0.72f,
                IsMasterMuted = false,
                IsAvailable = true,
                Sessions = [chromeSession, sysSession]
            }
        };

        await using var module = new VolumeMixerWidgetModule(adapter);
        await module.InitializeAsync(CreateContext(VolumeMixerWidgetModule.Id), CancellationToken.None);

        module.ViewModel.DeviceName.Should().Be("Headphones");
        module.ViewModel.MasterVolumePercent.Should().Be(72);
        // System Sounds filtered out by default
        module.ViewModel.Sessions.Should().ContainSingle(s => s.SessionKey == "app:chrome");
        var initialChromeRow = module.ViewModel.Sessions.Single();
        initialChromeRow.VolumePercent.Should().Be(50);

        // External Windows volume update mutates row in place without replacing instance (no drag stutter!)
        adapter.RaiseMixerChanged(adapter.CurrentSnapshot with
        {
            MasterVolume = 0.65f,
            Sessions = [chromeSession with { Volume = 0.85f, IsMuted = true }, sysSession]
        });

        module.ViewModel.MasterVolumePercent.Should().Be(65);
        ReferenceEquals(initialChromeRow, module.ViewModel.Sessions.Single()).Should().BeTrue();
        initialChromeRow.VolumePercent.Should().Be(85);
        initialChromeRow.IsMuted.Should().BeTrue();

        // Active slider drag prevents background echo from jumping user's thumb
        initialChromeRow.IsUserDragging = true;
        adapter.RaiseMixerChanged(adapter.CurrentSnapshot with
        {
            Sessions = [chromeSession with { Volume = 0.12f, IsMuted = true }, sysSession]
        });
        initialChromeRow.VolumePercent.Should().Be(85);
        initialChromeRow.IsUserDragging = false;

        // UI volume and mute adjustments flow back to adapter
        await module.ViewModel.SetSessionVolumePercentAsync("app:chrome", 40);
        await module.ViewModel.ToggleSessionMuteAsync("app:chrome");
        await module.ViewModel.SetMasterVolumePercentAsync(90);
        await module.ViewModel.ToggleMasterMuteAsync();

        adapter.LastSessionVolumeSet.Should().Be(("app:chrome", 0.4f));
        adapter.LastSessionMuteSet.Should().Be(("app:chrome", false));
        adapter.LastMasterVolumeSet.Should().Be(0.9f);
        adapter.LastMasterMuteSet.Should().BeTrue();

        // Show System Sounds setting toggle + Reset
        await module.ViewModel.SetShowSystemSoundsAsync(true);
        module.ViewModel.Sessions.Select(s => s.SessionKey).Should().Contain(["app:chrome", "app:sys"]);

        await module.ResetSettingsAsync();
        module.ViewModel.ShowSystemSounds.Should().BeFalse();
        module.ViewModel.Sessions.Should().ContainSingle(s => s.SessionKey == "app:chrome");
    }

    [Fact]
    public async Task NowPlayingAndVolumeMixerViews_InstantiateCleanlyOnStaWpfDispatcher()
    {
        var now = new DateTimeOffset(2026, 11, 22, 8, 40, 0, TimeSpan.Zero);
        var mediaAdapter = new FakeMediaSessionAdapter
        {
            CurrentSession = new MediaSessionSnapshot
            {
                SourceAppId = "Spotify.exe",
                Title = "Resonance",
                Artist = "HOME",
                AlbumTitle = "Odyssey",
                PlaybackState = MediaPlaybackState.Playing,
                CanPlayPause = true,
                IsPlaybackPositionEnabled = true,
                Position = TimeSpan.FromSeconds(12),
                Duration = TimeSpan.FromSeconds(210),
                LastUpdatedUtc = now
            }
        };

        var iconPixels = new byte[16 * 16 * 4];
        Array.Fill(iconPixels, (byte)200);
        var audioAdapter = new FakeAudioMixerAdapter
        {
            CurrentSnapshot = new AudioMixerSnapshot
            {
                DeviceId = "dev-1",
                DeviceName = "Speakers",
                MasterVolume = 0.6f,
                IsMasterMuted = false,
                IsAvailable = true,
                Sessions =
                [
                    CreateAudioSession("app:spotify", 555, "Spotify", 0.75f, false, new RawBgraImage(16, 16, iconPixels))
                ]
            }
        };

        await using var nowPlaying = new NowPlayingWidgetModule(mediaAdapter, () => now, new FakeNowPlayingTimer());
        await using var mixer = new VolumeMixerWidgetModule(audioAdapter);

        await nowPlaying.InitializeAsync(CreateContext(NowPlayingWidgetModule.Id), CancellationToken.None);
        await mixer.InitializeAsync(CreateContext(VolumeMixerWidgetModule.Id), CancellationToken.None);

        Exception? staException = null;
        var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                var nowPlayingView = nowPlaying.CreateExpandedView().Should().BeOfType<NowPlayingView>().Subject;
                nowPlaying.CreateCompactActiveView().Should().BeOfType<NowPlayingCompactView>();
                nowPlaying.CreateSettingsView().Should().BeOfType<NowPlayingSettingsView>();
                nowPlayingView.FindName("SettingsGearButton").Should().BeOfType<Button>();
                nowPlayingView.FindName("EmptyStateSettingsGearButton").Should().BeOfType<Button>();

                var mixerView = mixer.CreateExpandedView().Should().BeOfType<VolumeMixerView>().Subject;
                mixer.CreateSettingsView().Should().BeOfType<VolumeMixerSettingsView>();
                mixerView.FindName("SettingsGearButton").Should().BeOfType<Button>();

                mixer.ViewModel.Sessions.Single().IconBitmap.Should().NotBeNull();
                mixer.ViewModel.Sessions.Single().IconBitmap!.IsFrozen.Should().BeTrue();
                mixerView.DataContext.Should().BeSameAs(mixer.ViewModel);
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

    [Fact]
    public async Task NowPlayingAndVolumeMixer_OpenSettings_PublishesTargetSectionRequests()
    {
        var now = new DateTimeOffset(2026, 11, 22, 16, 0, 0, TimeSpan.Zero);
        var events = Substitute.For<IEventDispatcher>();
        var published = new List<OpenSettingsRequestedEvent>();
        events.When(e => e.Publish(Arg.Any<OpenSettingsRequestedEvent>()))
            .Do(ci => published.Add(ci.Arg<OpenSettingsRequestedEvent>()));

        var nowPlayingContext = CreateContext(NowPlayingWidgetModule.Id);
        nowPlayingContext.Events.Returns(events);
        await using var nowPlaying = new NowPlayingWidgetModule(new FakeMediaSessionAdapter(), () => now, new FakeNowPlayingTimer());
        await nowPlaying.InitializeAsync(nowPlayingContext, CancellationToken.None);

        var mixerContext = CreateContext(VolumeMixerWidgetModule.Id);
        mixerContext.Events.Returns(events);
        await using var mixer = new VolumeMixerWidgetModule(new FakeAudioMixerAdapter());
        await mixer.InitializeAsync(mixerContext, CancellationToken.None);

        nowPlaying.ViewModel.OpenSettings();
        mixer.ViewModel.OpenSettings();

        published.Select(p => p.SectionTag).Should().Equal("Widgets:shima.music", "Widgets:shima.mixer");
    }

    [Fact]
    public async Task NowPlayingCompactView_RendersRoundedClippedArtwork_WithoutOverflowInsideWidenedPill()
    {
        var now = new DateTimeOffset(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);
        var bmp = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var pngStream = new MemoryStream();
        encoder.Save(pngStream);

        var adapter = new FakeMediaSessionAdapter
        {
            CurrentSession = new MediaSessionSnapshot
            {
                SourceAppId = "Spotify.exe",
                Title = "Resonance (Extended Remaster)",
                Artist = "HOME",
                PlaybackState = MediaPlaybackState.Playing,
                CanPlayPause = true,
                ThumbnailBytes = pngStream.ToArray(),
                LastUpdatedUtc = now
            }
        };

        await using var nowPlaying = new NowPlayingWidgetModule(adapter, () => now, new FakeNowPlayingTimer());
        await nowPlaying.InitializeAsync(CreateContext(NowPlayingWidgetModule.Id), CancellationToken.None);

        Exception? staException = null;
        var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                var view = nowPlaying.CreateCompactActiveView().Should().BeOfType<NowPlayingCompactView>().Subject;
                view.Measure(new Size(208, 26));
                view.Arrange(new Rect(0, 0, 208, 26));
                view.UpdateLayout();

                var border = view.FindName("CompactArtworkBorder").Should().BeOfType<Border>().Subject;
                border.CornerRadius.TopLeft.Should().BeGreaterOrEqualTo(4);
                border.ActualWidth.Should().Be(18);
                border.ActualHeight.Should().Be(18);

                var innerGrid = border.Child.Should().BeOfType<Grid>().Subject;
                var clip = innerGrid.Clip.Should().BeOfType<RectangleGeometry>().Subject;
                clip.RadiusX.Should().BeGreaterOrEqualTo(4);
                clip.RadiusY.Should().BeGreaterOrEqualTo(4);

                var image = view.FindName("CompactArtworkImage").Should().BeOfType<Image>().Subject;
                image.Visibility.Should().Be(Visibility.Visible);
                image.Source.Should().NotBeNull();
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

    private static AudioSessionSnapshot CreateAudioSession(
        string key,
        uint pid,
        string name,
        float volume,
        bool isSystemSounds,
        RawBgraImage? icon) => new()
    {
        SessionKey = key,
        ProcessId = pid,
        DisplayName = name,
        ProcessName = name,
        Volume = volume,
        IsMuted = false,
        IsSystemSounds = isSystemSounds,
        State = AudioSessionActivityState.Active,
        IconImage = icon
    };

    private static IWidgetContext CreateContext(string widgetId, IIslandAttentionRequester? island = null)
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
        context.WidgetId.Returns(widgetId);
        context.Events.Returns(Substitute.For<IEventDispatcher>());
        context.Settings.Returns(settingsStore);
        context.Theme.Returns(Substitute.For<IThemeTokenProvider>());
        context.Island.Returns(island ?? Substitute.For<IIslandAttentionRequester>());
        context.Logger.Returns(Substitute.For<ILogger>());
        return context;
    }

    private sealed class FakeMediaSessionAdapter : IMediaSessionAdapter
    {
        public MediaSessionSnapshot? CurrentSession { get; set; }

        public List<TimeSpan> SeekCalls { get; } = [];

        public event EventHandler<MediaSessionSnapshot?>? SessionChanged;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void Stop()
        {
        }

        public Task<bool> TogglePlayPauseAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> NextTrackAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> PreviousTrackAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
        {
            if (CurrentSession is not { IsPlaybackPositionEnabled: true } || CurrentSession.Duration <= TimeSpan.Zero)
            {
                return Task.FromResult(false);
            }

            SeekCalls.Add(position);
            return Task.FromResult(true);
        }

        public void RaiseSession(MediaSessionSnapshot? snapshot)
        {
            CurrentSession = snapshot;
            SessionChanged?.Invoke(this, snapshot);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeNowPlayingTimer : INowPlayingTimer
    {
        private Action? _callback;

        public bool IsRunning { get; private set; }

        public TimeSpan? LastInterval { get; private set; }

        public void Start(TimeSpan interval, Action onTick)
        {
            LastInterval = interval;
            _callback = onTick;
            IsRunning = true;
        }

        public void Stop()
        {
            IsRunning = false;
            _callback = null;
        }

        public void Fire() => _callback?.Invoke();

        public void Dispose() => Stop();
    }

    private sealed class FakeAudioMixerAdapter : IAudioMixerAdapter
    {
        public AudioMixerSnapshot CurrentSnapshot { get; set; } = AudioMixerSnapshot.Unavailable;

        public float? LastMasterVolumeSet { get; private set; }

        public bool? LastMasterMuteSet { get; private set; }

        public (string Key, float Volume)? LastSessionVolumeSet { get; private set; }

        public (string Key, bool Muted)? LastSessionMuteSet { get; private set; }

        public event EventHandler<AudioMixerSnapshot>? MixerChanged;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void Stop()
        {
        }

        public Task<bool> SetMasterVolumeAsync(float volume, CancellationToken ct = default)
        {
            LastMasterVolumeSet = volume;
            CurrentSnapshot = CurrentSnapshot with { MasterVolume = volume };
            return Task.FromResult(true);
        }

        public Task<bool> SetMasterMuteAsync(bool isMuted, CancellationToken ct = default)
        {
            LastMasterMuteSet = isMuted;
            CurrentSnapshot = CurrentSnapshot with { IsMasterMuted = isMuted };
            return Task.FromResult(true);
        }

        public Task<bool> SetSessionVolumeAsync(string sessionKey, float volume, CancellationToken ct = default)
        {
            LastSessionVolumeSet = (sessionKey, volume);
            return Task.FromResult(true);
        }

        public Task<bool> SetSessionMuteAsync(string sessionKey, bool isMuted, CancellationToken ct = default)
        {
            LastSessionMuteSet = (sessionKey, isMuted);
            return Task.FromResult(true);
        }

        public void RaiseMixerChanged(AudioMixerSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            MixerChanged?.Invoke(this, snapshot);
        }

        public void Dispose()
        {
        }
    }
}
