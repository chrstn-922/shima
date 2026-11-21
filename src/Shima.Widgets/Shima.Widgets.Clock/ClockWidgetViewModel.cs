using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Shima.Core.Contracts;

namespace Shima.Widgets.Clock;

public sealed record WorldClockCityRow(
    string LocationId,
    string CityName,
    string CountryName,
    string WindowsTzId,
    string IanaTzId,
    int SortOrder,
    bool IsPrimary,
    string DisplayBulletName,
    string CompactTimeText,
    string UtcOffsetLabel,
    string SwitchActionLabel,
    bool CanMoveUp,
    bool CanMoveDown,
    bool CanRemove);

/// <summary>
/// Observable state and async actions backing both the two-column Clock expanded panel and Clock Settings tab.
/// </summary>
public sealed class ClockWidgetViewModel : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IClockRepository _repository;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly object _sync = new();

    private IWidgetContext? _context;
    private ClockSettings _settings = new();
    private List<WorldClockLocation> _locations = [];
    private Action<Action>? _uiDispatcher;

    private string _dateText = string.Empty;
    private string _timeText = string.Empty;
    private string _amPmText = string.Empty;
    private bool _hasAmPm = true;
    private string _locationSubtitle = string.Empty;
    private string _accessibleStatus = "Clock widget";
    private string _searchQuery = string.Empty;
    private string _statusMessage = string.Empty;
    private IReadOnlyList<CatalogCityEntry> _catalogMatches = WorldCityCatalog.AllCities;

    public ClockWidgetViewModel(
        IClockRepository repository,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public ObservableCollection<WorldClockCityRow> Locations { get; } = [];

    public ClockSettings CurrentSettings
    {
        get
        {
            lock (_sync)
            {
                return _settings;
            }
        }
    }

    public bool Use24HourFormat
    {
        get
        {
            lock (_sync)
            {
                return _settings.Use24HourFormat;
            }
        }
    }

    public bool ShowSeconds
    {
        get
        {
            lock (_sync)
            {
                return _settings.ShowSeconds;
            }
        }
    }

    public string DateText
    {
        get => _dateText;
        private set => SetField(ref _dateText, value);
    }

    public string TimeText
    {
        get => _timeText;
        private set => SetField(ref _timeText, value);
    }

    public string AmPmText
    {
        get => _amPmText;
        private set => SetField(ref _amPmText, value);
    }

    public bool HasAmPm
    {
        get => _hasAmPm;
        private set => SetField(ref _hasAmPm, value);
    }

    public string LocationSubtitle
    {
        get => _locationSubtitle;
        private set => SetField(ref _locationSubtitle, value);
    }

    public string AccessibleStatus
    {
        get => _accessibleStatus;
        private set => SetField(ref _accessibleStatus, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetField(ref _searchQuery, value ?? string.Empty))
            {
                CatalogMatches = WorldCityCatalog.Search(_searchQuery);
            }
        }
    }

    public IReadOnlyList<CatalogCityEntry> CatalogMatches
    {
        get => _catalogMatches;
        private set => SetField(ref _catalogMatches, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SettingsChanged;

    public void AttachUiDispatcher(Action<Action> dispatch)
    {
        _uiDispatcher = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        RefreshReadout();
    }

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        var rawJson = await context.Settings.ReadJsonAsync(ct).ConfigureAwait(false);
        var loadedSettings = ParseSettingsOrDefault(rawJson);

        var storedLocations = (await _repository.GetAllAsync(ct).ConfigureAwait(false)).ToList();
        if (storedLocations.Count == 0)
        {
            storedLocations = WorldCityCatalog.CreateDefaultLocations().ToList();
            await _repository.ReplaceAllAsync(storedLocations, ct).ConfigureAwait(false);
        }
        else if (!storedLocations.Any(l => l.IsPrimary))
        {
            storedLocations[0] = storedLocations[0] with { IsPrimary = true };
            await _repository.SetPrimaryAsync(storedLocations[0].LocationId, ct).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _settings = loadedSettings;
            _locations = storedLocations.OrderBy(l => l.SortOrder).ToList();
        }

        RefreshReadout();
    }

    public void RefreshReadout()
    {
        List<WorldClockLocation> snapshot;
        ClockSettings settings;
        lock (_sync)
        {
            if (_locations.Count == 0)
            {
                _locations = WorldCityCatalog.CreateDefaultLocations().ToList();
            }

            snapshot = _locations.OrderBy(l => l.SortOrder).ToList();
            settings = _settings;
        }

        var utcNow = _utcNowProvider();
        var primary = snapshot.FirstOrDefault(l => l.IsPrimary) ?? snapshot[0];
        var readout = ClockFormatting.FormatReadout(utcNow, primary, settings);

        var canRemove = snapshot.Count > 1;
        var rows = snapshot
            .Select((loc, index) =>
            {
                var compactTime = ClockFormatting.FormatCompactTime(utcNow, loc, settings);
                var tz = ClockFormatting.ResolveTimeZone(loc.WindowsTzId, loc.IanaTzId);
                var offset = ClockFormatting.FormatUtcOffset(tz.GetUtcOffset(utcNow));
                var bulletName = loc.IsPrimary ? $"• {loc.CityName}" : $"  {loc.CityName}";
                var switchLabel = loc.IsPrimary
                    ? $"Active city {loc.CityName}, {offset}"
                    : $"Switch primary clock to {loc.CityName}";

                return new WorldClockCityRow(
                    loc.LocationId,
                    loc.CityName,
                    loc.CountryName,
                    loc.WindowsTzId,
                    loc.IanaTzId,
                    loc.SortOrder,
                    loc.IsPrimary,
                    bulletName,
                    compactTime,
                    offset,
                    switchLabel,
                    index > 0,
                    index < snapshot.Count - 1,
                    canRemove);
            })
            .ToArray();

        DispatchToUi(() =>
        {
            DateText = readout.DateText;
            TimeText = readout.TimeText;
            AmPmText = readout.AmPmText;
            HasAmPm = readout.HasAmPm;
            LocationSubtitle = readout.LocationSubtitle;
            AccessibleStatus = readout.AccessibleStatus;

            Locations.Clear();
            foreach (var row in rows)
            {
                Locations.Add(row);
            }

            OnPropertyChanged(nameof(Use24HourFormat));
            OnPropertyChanged(nameof(ShowSeconds));
        });
    }

    public async Task SelectCityAsync(string locationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);

        lock (_sync)
        {
            if (!_locations.Any(l => string.Equals(l.LocationId, locationId, StringComparison.Ordinal)))
            {
                return;
            }

            _locations = _locations
                .Select(l => l with { IsPrimary = string.Equals(l.LocationId, locationId, StringComparison.Ordinal) })
                .ToList();
        }

        await _repository.SetPrimaryAsync(locationId, ct).ConfigureAwait(false);
        RefreshReadout();
    }

    public async Task<WorldClockLocation> AddCityAsync(CatalogCityEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        WorldClockLocation created;
        lock (_sync)
        {
            var nextOrder = _locations.Count == 0 ? 0 : _locations.Max(l => l.SortOrder) + 1;
            created = WorldCityCatalog.CreateLocation(entry, nextOrder, isPrimary: _locations.Count == 0);
            _locations.Add(created);
        }

        await _repository.UpsertAsync(created, ct).ConfigureAwait(false);
        RefreshReadout();
        StatusMessage = $"Added {created.CityName} ({created.IanaTzId}).";
        return created;
    }

    public async Task<bool> RemoveCityAsync(string locationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);

        string? promotedPrimaryId = null;
        lock (_sync)
        {
            if (_locations.Count <= 1)
            {
                StatusMessage = "At least one world clock city must remain active.";
                return false;
            }

            var targetIndex = _locations.FindIndex(l => string.Equals(l.LocationId, locationId, StringComparison.Ordinal));
            if (targetIndex < 0)
            {
                return false;
            }

            var removedWasPrimary = _locations[targetIndex].IsPrimary;
            _locations.RemoveAt(targetIndex);

            for (var i = 0; i < _locations.Count; i++)
            {
                _locations[i] = _locations[i] with { SortOrder = i };
            }

            if (removedWasPrimary && _locations.Count > 0)
            {
                _locations[0] = _locations[0] with { IsPrimary = true };
                promotedPrimaryId = _locations[0].LocationId;
            }
        }

        await _repository.DeleteAsync(locationId, ct).ConfigureAwait(false);
        var orderedIds = SnapshotOrderedIds();
        await _repository.UpdateOrderAsync(orderedIds, ct).ConfigureAwait(false);
        if (promotedPrimaryId is not null)
        {
            await _repository.SetPrimaryAsync(promotedPrimaryId, ct).ConfigureAwait(false);
        }

        RefreshReadout();
        return true;
    }

    public async Task MoveCityAsync(string locationId, int delta, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);

        IReadOnlyList<string> orderedIds;
        lock (_sync)
        {
            var list = _locations.OrderBy(l => l.SortOrder).ToList();
            var idx = list.FindIndex(l => string.Equals(l.LocationId, locationId, StringComparison.Ordinal));
            var targetIdx = idx + delta;
            if (idx < 0 || targetIdx < 0 || targetIdx >= list.Count)
            {
                return;
            }

            (list[idx], list[targetIdx]) = (list[targetIdx], list[idx]);
            for (var i = 0; i < list.Count; i++)
            {
                list[i] = list[i] with { SortOrder = i };
            }

            _locations = list;
            orderedIds = list.Select(l => l.LocationId).ToArray();
        }

        await _repository.UpdateOrderAsync(orderedIds, ct).ConfigureAwait(false);
        RefreshReadout();
    }

    public Task SetUse24HourFormatAsync(bool use24Hour, CancellationToken ct = default) =>
        UpdateSettingsAsync(s => s with { Use24HourFormat = use24Hour }, ct);

    public Task SetShowSecondsAsync(bool showSeconds, CancellationToken ct = default) =>
        UpdateSettingsAsync(s => s with { ShowSeconds = showSeconds }, ct);

    public void OpenSettings() =>
        _context?.Events.Publish(new OpenSettingsRequestedEvent("Clock"));

    public async Task ResetToDefaultsAsync(CancellationToken ct = default)
    {
        var defaults = WorldCityCatalog.CreateDefaultLocations().ToList();
        var defaultSettings = new ClockSettings();

        lock (_sync)
        {
            _settings = defaultSettings;
            _locations = defaults;
        }

        await _repository.ReplaceAllAsync(defaults, ct).ConfigureAwait(false);
        if (_context is not null)
        {
            await _context.Settings.WriteJsonAsync(JsonSerializer.Serialize(defaultSettings, JsonOptions), ct)
                .ConfigureAwait(false);
        }

        RefreshReadout();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        StatusMessage = "Restored default world clock cities (Manila, London, New York, Tokyo).";
    }

    private async Task UpdateSettingsAsync(Func<ClockSettings, ClockSettings> transform, CancellationToken ct)
    {
        ClockSettings updated;
        lock (_sync)
        {
            updated = transform(_settings);
            _settings = updated;
        }

        if (_context is not null)
        {
            await _context.Settings.WriteJsonAsync(JsonSerializer.Serialize(updated, JsonOptions), ct)
                .ConfigureAwait(false);
        }

        RefreshReadout();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<string> SnapshotOrderedIds()
    {
        lock (_sync)
        {
            return _locations.OrderBy(l => l.SortOrder).Select(l => l.LocationId).ToArray();
        }
    }

    private static ClockSettings ParseSettingsOrDefault(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || string.Equals(rawJson.Trim(), "{}", StringComparison.Ordinal))
        {
            return new ClockSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<ClockSettings>(rawJson, JsonOptions) ?? new ClockSettings();
        }
        catch (JsonException)
        {
            return new ClockSettings();
        }
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

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
