using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shima.Core.Contracts;

namespace Shima.Widgets.VolumeMixer;

public sealed class AudioSessionRowViewModel : INotifyPropertyChanged
{
    private RawBgraImage? _sourceIcon;
    private BitmapSource? _iconBitmap;
    private string _displayName;
    private double _volumePercent;
    private bool _isMuted;

    public AudioSessionRowViewModel(AudioSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SessionKey = snapshot.SessionKey;
        ProcessId = snapshot.ProcessId;
        _displayName = NormalizeDisplayName(snapshot);
        _volumePercent = ToPercent(snapshot.Volume);
        _isMuted = snapshot.IsMuted;
        _sourceIcon = snapshot.IconImage;
        _iconBitmap = ConvertIcon(snapshot.IconImage);
    }

    public string SessionKey { get; }

    public uint ProcessId { get; }

    public bool IsUserDragging { get; set; }

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (SetField(ref _displayName, value))
            {
                OnPropertyChanged(nameof(MuteActionLabel));
                OnPropertyChanged(nameof(VolumeSliderActionLabel));
                OnPropertyChanged(nameof(AccessibleState));
            }
        }
    }

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 0, MidpointRounding.AwayFromZero), 0.0, 100.0);
            if (SetField(ref _volumePercent, clamped))
            {
                OnPropertyChanged(nameof(VolumePercentLabel));
                OnPropertyChanged(nameof(AccessibleState));
            }
        }
    }

    public string VolumePercentLabel => $"{(int)Math.Round(_volumePercent)}%";

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetField(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(MuteActionLabel));
                OnPropertyChanged(nameof(AccessibleState));
            }
        }
    }

    public BitmapSource? IconBitmap
    {
        get => _iconBitmap;
        private set
        {
            if (SetField(ref _iconBitmap, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => _iconBitmap is not null;

    public string MuteActionLabel => IsMuted ? $"Unmute {DisplayName}" : $"Mute {DisplayName}";

    public string VolumeSliderActionLabel => $"Adjust volume for {DisplayName}";

    public string AccessibleState =>
        IsMuted
            ? $"{DisplayName}, muted"
            : $"{DisplayName}, {(int)Math.Round(_volumePercent)} percent";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateFromSnapshot(AudioSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DisplayName = NormalizeDisplayName(snapshot);
        IsMuted = snapshot.IsMuted;

        if (!IsUserDragging)
        {
            VolumePercent = ToPercent(snapshot.Volume);
        }

        if (!ReferenceEquals(snapshot.IconImage, _sourceIcon))
        {
            _sourceIcon = snapshot.IconImage;
            IconBitmap = ConvertIcon(snapshot.IconImage);
        }
    }

    internal static double ToPercent(float scalar) =>
        Math.Clamp(Math.Round(scalar * 100.0, 0, MidpointRounding.AwayFromZero), 0.0, 100.0);

    private static string NormalizeDisplayName(AudioSessionSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.DisplayName)
            ? snapshot.ProcessName ?? "Application"
            : snapshot.DisplayName.Trim();

    private static BitmapSource? ConvertIcon(RawBgraImage? image)
    {
        if (image is null || !image.IsValid)
        {
            return null;
        }

        try
        {
            var bmp = BitmapSource.Create(
                image.Width,
                image.Height,
                96.0,
                96.0,
                PixelFormats.Bgra32,
                null,
                image.Pixels,
                image.Width * 4);
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
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

/// <summary>
/// Observable view-model backing bidirectional CoreAudio master + per-application volume mixing,
/// reconciling session rows in place so active drags remain smooth across Windows volume updates.
/// </summary>
public sealed class VolumeMixerWidgetViewModel : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAudioMixerAdapter _audioAdapter;
    private readonly object _sync = new();

    private IWidgetContext? _context;
    private Action<Action>? _uiDispatcher;
    private VolumeMixerSettings _settings = new();
    private AudioMixerSnapshot _latestSnapshot = AudioMixerSnapshot.Unavailable;

    private string _deviceName = "Default Output";
    private bool _isAvailable;
    private bool _hasSessions;
    private string _emptyStateMessage = "No active application audio streams";
    private double _masterVolumePercent;
    private bool _isMasterMuted;
    private string _accessibleStatus = "Volume Mixer";

    public VolumeMixerWidgetViewModel(IAudioMixerAdapter audioAdapter)
    {
        _audioAdapter = audioAdapter ?? throw new ArgumentNullException(nameof(audioAdapter));
    }

    public ObservableCollection<AudioSessionRowViewModel> Sessions { get; } = [];

    public bool IsMasterDragging { get; set; }

    public string DeviceName
    {
        get => _deviceName;
        private set => SetField(ref _deviceName, value);
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetField(ref _isAvailable, value);
    }

    public bool HasSessions
    {
        get => _hasSessions;
        private set
        {
            if (SetField(ref _hasSessions, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public bool ShowEmptyState => !_hasSessions;

    public string EmptyStateMessage
    {
        get => _emptyStateMessage;
        private set => SetField(ref _emptyStateMessage, value);
    }

    public double MasterVolumePercent
    {
        get => _masterVolumePercent;
        private set
        {
            var clamped = Math.Clamp(Math.Round(value, 0, MidpointRounding.AwayFromZero), 0.0, 100.0);
            if (SetField(ref _masterVolumePercent, clamped))
            {
                OnPropertyChanged(nameof(MasterVolumePercentLabel));
                OnPropertyChanged(nameof(AccessibleStatus));
            }
        }
    }

    public string MasterVolumePercentLabel => $"{(int)Math.Round(_masterVolumePercent)}%";

    public bool IsMasterMuted
    {
        get => _isMasterMuted;
        private set
        {
            if (SetField(ref _isMasterMuted, value))
            {
                OnPropertyChanged(nameof(MasterMuteActionLabel));
                OnPropertyChanged(nameof(AccessibleStatus));
            }
        }
    }

    public string MasterMuteActionLabel => IsMasterMuted ? "Unmute master output" : "Mute master output";

    public string AccessibleStatus
    {
        get => _accessibleStatus;
        private set => SetField(ref _accessibleStatus, value);
    }

    public bool ShowSystemSounds
    {
        get
        {
            lock (_sync)
            {
                return _settings.ShowSystemSounds;
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SettingsChanged;

    public void AttachUiDispatcher(Action<Action> dispatch)
    {
        _uiDispatcher = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        ApplySnapshot(_audioAdapter.CurrentSnapshot);
    }

    public void OpenSettings() =>
        _context?.Events.Publish(new OpenSettingsRequestedEvent("Widgets:shima.mixer"));

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var rawJson = await context.Settings.ReadJsonAsync(ct).ConfigureAwait(false);
        var loaded = ParseSettingsOrDefault(rawJson);

        lock (_sync)
        {
            _settings = loaded;
        }

        DispatchToUi(() => OnPropertyChanged(nameof(ShowSystemSounds)));
    }

    public void ApplySnapshot(AudioMixerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool showSystemSounds;
        lock (_sync)
        {
            _latestSnapshot = snapshot;
            showSystemSounds = _settings.ShowSystemSounds;
        }

        var filtered = snapshot.Sessions
            .Where(s => s.State != AudioSessionActivityState.Expired && (showSystemSounds || !s.IsSystemSounds))
            .ToArray();

        DispatchToUi(() =>
        {
            IsAvailable = snapshot.IsAvailable;
            DeviceName = string.IsNullOrWhiteSpace(snapshot.DeviceName)
                ? "Default Output"
                : snapshot.DeviceName;
            IsMasterMuted = snapshot.IsMasterMuted;
            if (!IsMasterDragging)
            {
                MasterVolumePercent = AudioSessionRowViewModel.ToPercent(snapshot.MasterVolume);
            }

            ReconcileSessionRows(filtered);
            HasSessions = Sessions.Count > 0;
            EmptyStateMessage = snapshot.IsAvailable
                ? "No active application audio streams"
                : "No audio output device detected";

            AccessibleStatus = snapshot.IsAvailable
                ? $"Volume Mixer: {DeviceName}, master {(IsMasterMuted ? "muted" : MasterVolumePercentLabel)}, {Sessions.Count} active app(s)"
                : "Volume Mixer: no audio output device detected";
        });
    }

    public async Task SetMasterVolumePercentAsync(double percent, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(Math.Round(percent, 0, MidpointRounding.AwayFromZero), 0.0, 100.0);
        DispatchToUi(() => MasterVolumePercent = clamped);
        await _audioAdapter.SetMasterVolumeAsync((float)(clamped / 100.0), ct).ConfigureAwait(false);
    }

    public async Task ToggleMasterMuteAsync(CancellationToken ct = default)
    {
        var nextMuted = !IsMasterMuted;
        DispatchToUi(() => IsMasterMuted = nextMuted);
        await _audioAdapter.SetMasterMuteAsync(nextMuted, ct).ConfigureAwait(false);
    }

    public async Task SetSessionVolumePercentAsync(string sessionKey, double percent, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        var clamped = Math.Clamp(Math.Round(percent, 0, MidpointRounding.AwayFromZero), 0.0, 100.0);
        DispatchToUi(() =>
        {
            var row = Sessions.FirstOrDefault(s => string.Equals(s.SessionKey, sessionKey, StringComparison.Ordinal));
            if (row is not null)
            {
                row.VolumePercent = clamped;
            }
        });

        await _audioAdapter.SetSessionVolumeAsync(sessionKey, (float)(clamped / 100.0), ct).ConfigureAwait(false);
    }

    public async Task ToggleSessionMuteAsync(string sessionKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        var row = Sessions.FirstOrDefault(s => string.Equals(s.SessionKey, sessionKey, StringComparison.Ordinal));
        var nextMuted = row is not null ? !row.IsMuted : true;
        DispatchToUi(() =>
        {
            var target = Sessions.FirstOrDefault(s => string.Equals(s.SessionKey, sessionKey, StringComparison.Ordinal));
            if (target is not null)
            {
                target.IsMuted = nextMuted;
            }
        });

        await _audioAdapter.SetSessionMuteAsync(sessionKey, nextMuted, ct).ConfigureAwait(false);
    }

    public async Task SetShowSystemSoundsAsync(bool show, CancellationToken ct = default)
    {
        await UpdateSettingsAsync(s => s with { ShowSystemSounds = show }, ct).ConfigureAwait(false);
        ApplySnapshot(SnapshotCopy());
    }

    public async Task ResetToDefaultsAsync(CancellationToken ct = default)
    {
        var defaults = new VolumeMixerSettings();
        lock (_sync)
        {
            _settings = defaults;
        }

        if (_context is not null)
        {
            await _context.Settings.WriteJsonAsync(JsonSerializer.Serialize(defaults, JsonOptions), ct)
                .ConfigureAwait(false);
        }

        DispatchToUi(() => OnPropertyChanged(nameof(ShowSystemSounds)));
        ApplySnapshot(SnapshotCopy());
        DispatchToUi(() => SettingsChanged?.Invoke(this, EventArgs.Empty));
    }

    private AudioMixerSnapshot SnapshotCopy()
    {
        lock (_sync)
        {
            return _latestSnapshot;
        }
    }

    private void ReconcileSessionRows(IReadOnlyList<AudioSessionSnapshot> incoming)
    {
        var incomingKeys = incoming.Select(s => s.SessionKey).ToHashSet(StringComparer.Ordinal);
        for (var i = Sessions.Count - 1; i >= 0; i--)
        {
            if (!incomingKeys.Contains(Sessions[i].SessionKey))
            {
                Sessions.RemoveAt(i);
            }
        }

        for (var targetIdx = 0; targetIdx < incoming.Count; targetIdx++)
        {
            var snap = incoming[targetIdx];
            var existingIdx = -1;
            for (var j = 0; j < Sessions.Count; j++)
            {
                if (string.Equals(Sessions[j].SessionKey, snap.SessionKey, StringComparison.Ordinal))
                {
                    existingIdx = j;
                    break;
                }
            }

            if (existingIdx >= 0)
            {
                Sessions[existingIdx].UpdateFromSnapshot(snap);
                if (existingIdx != targetIdx)
                {
                    Sessions.Move(existingIdx, targetIdx);
                }
            }
            else
            {
                Sessions.Insert(targetIdx, new AudioSessionRowViewModel(snap));
            }
        }
    }

    private async Task UpdateSettingsAsync(
        Func<VolumeMixerSettings, VolumeMixerSettings> transform,
        CancellationToken ct)
    {
        VolumeMixerSettings updated;
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

        DispatchToUi(() => OnPropertyChanged(nameof(ShowSystemSounds)));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static VolumeMixerSettings ParseSettingsOrDefault(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || string.Equals(rawJson.Trim(), "{}", StringComparison.Ordinal))
        {
            return new VolumeMixerSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<VolumeMixerSettings>(rawJson, JsonOptions) ?? new VolumeMixerSettings();
        }
        catch (JsonException)
        {
            return new VolumeMixerSettings();
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
