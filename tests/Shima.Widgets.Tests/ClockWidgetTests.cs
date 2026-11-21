using System.Windows;
using System.Windows.Threading;
using FluentAssertions;
using NSubstitute;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Widgets.Clock;
using Shima.Widgets.Clock.Views;
using Xunit;

namespace Shima.Widgets.Tests;

public sealed class ClockWidgetTests
{
    [Fact]
    public void FormatReadout_ComputesDstAndFractionalUtcOffsetsAcrossTimezones()
    {
        var settings12h = new ClockSettings { Use24HourFormat = false, ShowSeconds = false };

        // Winter (Jan 15 2026 14:30 UTC): Manila +8 (22:30), London GMT+0 (14:30), New York EST-5 (09:30), Kolkata +5:30 (20:00)
        var winterUtc = new DateTimeOffset(2026, 1, 15, 14, 30, 12, TimeSpan.Zero);

        var manila = ClockFormatting.FormatReadout(winterUtc, CreateLocation("manila", "Manila", "Singapore Standard Time", "Asia/Manila"), settings12h);
        manila.DateText.Should().Be("Thursday, January 15");
        manila.TimeText.Should().Be("10:30");
        manila.AmPmText.Should().Be("PM");
        manila.HasAmPm.Should().BeTrue();
        manila.UtcOffsetLabel.Should().Be("UTC+8");
        manila.LocationSubtitle.Should().Be("Manila · UTC+8");

        var londonWinter = ClockFormatting.FormatReadout(winterUtc, CreateLocation("london", "London", "GMT Standard Time", "Europe/London"), settings12h);
        londonWinter.TimeText.Should().Be("2:30");
        londonWinter.AmPmText.Should().Be("PM");
        londonWinter.UtcOffsetLabel.Should().Be("UTC+0");

        var nyWinter = ClockFormatting.FormatReadout(winterUtc, CreateLocation("ny", "New York", "Eastern Standard Time", "America/New_York"), settings12h);
        nyWinter.TimeText.Should().Be("9:30");
        nyWinter.AmPmText.Should().Be("AM");
        nyWinter.UtcOffsetLabel.Should().Be("UTC-5");

        var kolkata = ClockFormatting.FormatReadout(winterUtc, CreateLocation("kolkata", "Kolkata", "India Standard Time", "Asia/Kolkata"), settings12h);
        kolkata.TimeText.Should().Be("8:00");
        kolkata.AmPmText.Should().Be("PM");
        kolkata.UtcOffsetLabel.Should().Be("UTC+5:30");

        // Summer DST (July 15 2026 14:30 UTC): London BST +1, New York EDT -4
        var summerUtc = new DateTimeOffset(2026, 7, 15, 14, 30, 12, TimeSpan.Zero);

        var londonSummer = ClockFormatting.FormatReadout(summerUtc, CreateLocation("london", "London", "GMT Standard Time", "Europe/London"), settings12h);
        londonSummer.TimeText.Should().Be("3:30");
        londonSummer.UtcOffsetLabel.Should().Be("UTC+1");

        var nySummer = ClockFormatting.FormatReadout(summerUtc, CreateLocation("ny", "New York", "Eastern Standard Time", "America/New_York"), settings12h);
        nySummer.TimeText.Should().Be("10:30");
        nySummer.UtcOffsetLabel.Should().Be("UTC-4");
    }

    [Fact]
    public void FormatReadout_AndBoundaryTimerDelay_Respect12h24hAndSecondsSettings()
    {
        var utc = new DateTimeOffset(2026, 11, 21, 13, 15, 42, 250, TimeSpan.Zero); // 9:15:42 PM in Manila (UTC+8)
        var manila = CreateLocation("manila", "Manila", "Singapore Standard Time", "Asia/Manila");

        var f12NoSec = ClockFormatting.FormatReadout(utc, manila, new ClockSettings { Use24HourFormat = false, ShowSeconds = false });
        f12NoSec.DateText.Should().Be("Saturday, November 21");
        f12NoSec.TimeText.Should().Be("9:15");
        f12NoSec.AmPmText.Should().Be("PM");
        f12NoSec.HasAmPm.Should().BeTrue();

        var f24Sec = ClockFormatting.FormatReadout(utc, manila, new ClockSettings { Use24HourFormat = true, ShowSeconds = true });
        f24Sec.TimeText.Should().Be("21:15:42");
        f24Sec.AmPmText.Should().BeEmpty();
        f24Sec.HasAmPm.Should().BeFalse();

        // Next minute boundary: 17.75 seconds remaining
        ClockFormatting.CalculateDelayUntilNextBoundary(utc, showSeconds: false)
            .Should().Be(TimeSpan.FromSeconds(17.75));

        // Next second boundary: 0.75 seconds remaining
        ClockFormatting.CalculateDelayUntilNextBoundary(utc, showSeconds: true)
            .Should().Be(TimeSpan.FromMilliseconds(750));

        // Exact minute boundary waits a full 60 seconds; exact clamped floor >= 50ms
        var exactMinute = new DateTimeOffset(2026, 11, 21, 13, 16, 0, 0, TimeSpan.Zero);
        ClockFormatting.CalculateDelayUntilNextBoundary(exactMinute, showSeconds: false)
            .Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ClockWidgetModule_TimerStartsOnActivate_SuspendsOnCollapseFullscreenPowerOff_AndRespondsToTimeChange()
    {
        var repo = new InMemoryClockRepository();
        var now = new DateTimeOffset(2026, 11, 21, 13, 15, 40, TimeSpan.Zero); // 9:15 PM Manila
        var timer = new FakeClockTimer();
        var events = new InMemoryEventDispatcher();
        var context = CreateContext(events);

        await using var module = new ClockWidgetModule(repo, () => now, timer);
        await module.InitializeAsync(context, CancellationToken.None);

        // Inactive right after InitializeAsync -> zero running timers (0.0% idle CPU)
        module.IsTimerRunning.Should().BeFalse();
        module.ViewModel.Locations.Select(l => l.CityName).Should().Equal("Manila", "London", "New York", "Tokyo");
        module.ViewModel.TimeText.Should().Be("9:15");
        module.ViewModel.LocationSubtitle.Should().Be("Manila · UTC+8");

        // Activate -> aligns timer cleanly to 20s until next whole minute
        await module.ActivateAsync(WidgetActivationReason.UserSelection);
        module.IsTimerRunning.Should().BeTrue();
        timer.LastScheduledDelay.Should().Be(TimeSpan.FromSeconds(20));

        // Advancing time and triggering tick updates readout and schedules next 60s boundary
        now = now.AddSeconds(20);
        timer.Fire();
        module.ViewModel.TimeText.Should().Be("9:16");
        timer.LastScheduledDelay.Should().Be(TimeSpan.FromMinutes(1));

        // Collapsing island immediately stops timer
        module.OnShellStateChanged(IslandShellState.Collapsed);
        module.IsTimerRunning.Should().BeFalse();

        // Re-expanding resumes timer cleanly
        now = now.AddMinutes(5);
        module.OnShellStateChanged(IslandShellState.Expanded);
        module.IsTimerRunning.Should().BeTrue();

        // Fullscreen / DisplayOff / Deactivate all stop timer cleanly
        module.OnShellStateChanged(IslandShellState.Fullscreen);
        module.IsTimerRunning.Should().BeFalse();

        module.OnShellStateChanged(IslandShellState.Expanded);
        module.IsTimerRunning.Should().BeTrue();

        module.OnPowerProfileChanged(PowerProfile.DisplayOff);
        module.IsTimerRunning.Should().BeFalse();

        module.OnPowerProfileChanged(PowerProfile.AC);
        module.IsTimerRunning.Should().BeTrue();

        // WM_TIMECHANGE / SystemTimeChangedEvent immediately recalculates readout
        now = now.AddHours(1);
        events.Publish(new SystemTimeChangedEvent(now));
        module.ViewModel.TimeText.Should().Be("10:21");

        await module.DeactivateAsync();
        module.IsTimerRunning.Should().BeFalse();
    }

    [Fact]
    public async Task ClockWidgetViewModel_ManagesCitiesFormatsSettingsNavigationAndReset()
    {
        var repo = new InMemoryClockRepository();
        var now = new DateTimeOffset(2026, 11, 21, 13, 15, 0, TimeSpan.Zero);
        var timer = new FakeClockTimer();
        var events = new InMemoryEventDispatcher();
        string? openedSettingsSection = null;
        using var sub = events.Subscribe<OpenSettingsRequestedEvent>(e => openedSettingsSection = e.SectionTag);

        string? persistedJson = null;
        var settingsStore = Substitute.For<IWidgetSettingsStore>();
        settingsStore.ReadJsonAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(persistedJson));
        settingsStore.WriteJsonAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                persistedJson = ci.Arg<string>();
                return Task.CompletedTask;
            });

        var context = CreateContext(events, settingsStore);

        await using var module = new ClockWidgetModule(repo, () => now, timer);
        await module.InitializeAsync(context, CancellationToken.None);
        await module.ActivateAsync(WidgetActivationReason.UserSelection);

        // Select Tokyo as primary city
        var tokyoId = module.ViewModel.Locations.Single(l => l.CityName == "Tokyo").LocationId;
        await module.ViewModel.SelectCityAsync(tokyoId);
        module.ViewModel.LocationSubtitle.Should().Be("Tokyo · UTC+9");
        module.ViewModel.TimeText.Should().Be("10:15");
        module.ViewModel.AmPmText.Should().Be("PM");

        // Toggle 24h & ShowSeconds -> timer switches from minute boundary to second boundary
        await module.ViewModel.SetUse24HourFormatAsync(true);
        await module.ViewModel.SetShowSecondsAsync(true);
        module.ViewModel.TimeText.Should().Be("22:15:00");
        module.ViewModel.HasAmPm.Should().BeFalse();
        timer.LastScheduledDelay.Should().Be(TimeSpan.FromSeconds(1));
        persistedJson.Should().Contain("use24HourFormat");

        // Search offline catalog & add Kolkata (UTC+5:30)
        module.ViewModel.SearchQuery = "Kolkata";
        var kolkataEntry = module.ViewModel.CatalogMatches.Should().ContainSingle(c => c.CityName == "Kolkata").Subject;
        var added = await module.ViewModel.AddCityAsync(kolkataEntry);
        module.ViewModel.Locations.Select(l => l.CityName).Should().Contain("Kolkata");

        // Move Kolkata up one slot
        await module.ViewModel.MoveCityAsync(added.LocationId, -1);
        module.ViewModel.Locations[^2].CityName.Should().Be("Kolkata");

        // Cannot remove the last remaining city
        while (module.ViewModel.Locations.Count > 1)
        {
            var removed = await module.ViewModel.RemoveCityAsync(module.ViewModel.Locations[0].LocationId);
            removed.Should().BeTrue();
        }

        (await module.ViewModel.RemoveCityAsync(module.ViewModel.Locations[0].LocationId)).Should().BeFalse();
        module.ViewModel.Locations.Should().HaveCount(1);

        // Clicking gear opens Clock settings section
        module.ViewModel.OpenSettings();
        openedSettingsSection.Should().Be("Clock");

        // Reset restores 4 default cities and 12h format without seconds
        await module.ResetSettingsAsync();
        module.ViewModel.Locations.Select(l => l.CityName).Should().Equal("Manila", "London", "New York", "Tokyo");
        module.ViewModel.Use24HourFormat.Should().BeFalse();
        module.ViewModel.ShowSeconds.Should().BeFalse();
    }

    [Fact]
    public async Task ClockViews_InstantiateCleanlyOnStaWpfDispatcher()
    {
        var repo = new InMemoryClockRepository();
        var events = new InMemoryEventDispatcher();
        await using var module = new ClockWidgetModule(repo, () => new DateTimeOffset(2026, 11, 21, 13, 15, 0, TimeSpan.Zero), new FakeClockTimer());
        await module.InitializeAsync(CreateContext(events), CancellationToken.None);

        Exception? staException = null;
        var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                var expanded = module.CreateExpandedView();
                var settings = module.CreateSettingsView();
                expanded.Should().BeOfType<ClockView>();
                settings.Should().BeOfType<ClockSettingsView>();
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

    private static WorldClockLocation CreateLocation(string id, string city, string winTz, string ianaTz) => new()
    {
        LocationId = id,
        CityName = city,
        WindowsTzId = winTz,
        IanaTzId = ianaTz,
        SortOrder = 0,
        IsPrimary = true
    };

    private static IWidgetContext CreateContext(IEventDispatcher events, IWidgetSettingsStore? settingsStore = null)
    {
        if (settingsStore is null)
        {
            settingsStore = Substitute.For<IWidgetSettingsStore>();
            settingsStore.ReadJsonAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("{}"));
        }

        var context = Substitute.For<IWidgetContext>();
        context.WidgetId.Returns(ClockWidgetModule.Id);
        context.Events.Returns(events);
        context.Settings.Returns(settingsStore);
        context.Theme.Returns(Substitute.For<IThemeTokenProvider>());
        context.Island.Returns(Substitute.For<IIslandAttentionRequester>());
        context.Logger.Returns(Substitute.For<ILogger>());
        return context;
    }

    private sealed class InMemoryClockRepository : IClockRepository
    {
        private readonly object _sync = new();
        private readonly List<WorldClockLocation> _items = [];

        public Task<IReadOnlyList<WorldClockLocation>> GetAllAsync(CancellationToken ct = default)
        {
            lock (_sync)
            {
                return Task.FromResult<IReadOnlyList<WorldClockLocation>>(_items.OrderBy(i => i.SortOrder).ToArray());
            }
        }

        public Task UpsertAsync(WorldClockLocation location, CancellationToken ct = default)
        {
            lock (_sync)
            {
                _items.RemoveAll(i => i.LocationId == location.LocationId);
                _items.Add(location);
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string locationId, CancellationToken ct = default)
        {
            lock (_sync)
            {
                _items.RemoveAll(i => i.LocationId == locationId);
            }

            return Task.CompletedTask;
        }

        public Task SetPrimaryAsync(string locationId, CancellationToken ct = default)
        {
            lock (_sync)
            {
                for (var i = 0; i < _items.Count; i++)
                {
                    _items[i] = _items[i] with { IsPrimary = _items[i].LocationId == locationId };
                }
            }

            return Task.CompletedTask;
        }

        public Task UpdateOrderAsync(IReadOnlyList<string> orderedLocationIds, CancellationToken ct = default)
        {
            lock (_sync)
            {
                for (var idx = 0; idx < orderedLocationIds.Count; idx++)
                {
                    var matchIdx = _items.FindIndex(i => i.LocationId == orderedLocationIds[idx]);
                    if (matchIdx >= 0)
                    {
                        _items[matchIdx] = _items[matchIdx] with { SortOrder = idx };
                    }
                }
            }

            return Task.CompletedTask;
        }

        public Task ReplaceAllAsync(IReadOnlyList<WorldClockLocation> locations, CancellationToken ct = default)
        {
            lock (_sync)
            {
                _items.Clear();
                _items.AddRange(locations);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeClockTimer : IClockTimer
    {
        private Action? _onTick;

        public bool IsRunning { get; private set; }

        public TimeSpan? LastScheduledDelay { get; private set; }

        public void Schedule(TimeSpan delay, Action onTick)
        {
            LastScheduledDelay = delay;
            _onTick = onTick;
            IsRunning = true;
        }

        public void Stop()
        {
            IsRunning = false;
            _onTick = null;
        }

        public void Fire()
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            var cb = _onTick;
            _onTick = null;
            cb?.Invoke();
        }

        public void Dispose() => Stop();
    }

    private sealed class InMemoryEventDispatcher : IEventDispatcher
    {
        private readonly List<(Type EventType, Delegate Handler)> _handlers = [];

        public IDisposable Subscribe<T>(Action<T> handler)
        {
            var entry = (typeof(T), (Delegate)handler);
            _handlers.Add(entry);
            return new Subscription(() => _handlers.Remove(entry));
        }

        public void Publish<T>(T payload)
        {
            foreach (var (type, handler) in _handlers.ToArray())
            {
                if (type == typeof(T))
                {
                    ((Action<T>)handler)(payload);
                }
            }
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
