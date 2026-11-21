using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Shima.Core.Contracts;

namespace Shima.Widgets.NowPlaying;

/// <summary>
/// Observable view-model backing Now Playing expanded controls, compact active pill telemetry,
/// and widget settings with artwork decoding and seek-unsupported fallbacks.
/// </summary>
public sealed class NowPlayingWidgetViewModel : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IMediaSessionAdapter _mediaAdapter;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly object _sync = new();

    private IWidgetContext? _context;
    private Action<Action>? _uiDispatcher;
    private NowPlayingSettings _settings = new();
    private MediaSessionSnapshot? _session;
    private byte[]? _cachedThumbnailBytes;
    private BitmapSource? _cachedArtworkBitmap;
    private bool _isUserScrubbing;

    private bool _hasSession;
    private bool _hasArtwork;
    private bool _isPlaying;
    private bool _canPlayPause;
    private bool _canSkipNext;
    private bool _canSkipPrevious;
    private bool _canSeek;
    private bool _showSeekUnsupportedHint;
    private string _title = "Nothing playing";
    private string _artistSubtitle = "Play media in any supported app";
    private string _statusLabel = "Idle";
    private string _compactTitle = string.Empty;
    private string _elapsedText = "0:00";
    private string _durationText = "0:00";
    private double _progressSeconds;
    private double _durationSeconds = 1.0;
    private BitmapSource? _artworkBitmap;
    private string _playPauseActionLabel = "Play media";
    private string _accessibleStatus = "Now Playing: no active media session";

    public NowPlayingWidgetViewModel(
        IMediaSessionAdapter mediaAdapter,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        _mediaAdapter = mediaAdapter ?? throw new ArgumentNullException(nameof(mediaAdapter));
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public bool HasSession
    {
        get => _hasSession;
        private set
        {
            if (SetField(ref _hasSession, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public bool ShowEmptyState => !_hasSession;

    public bool HasArtwork
    {
        get => _hasArtwork;
        private set
        {
            if (SetField(ref _hasArtwork, value))
            {
                OnPropertyChanged(nameof(ShowArtworkInCompactPill));
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetField(ref _isPlaying, value);
    }

    public bool CanPlayPause
    {
        get => _canPlayPause;
        private set => SetField(ref _canPlayPause, value);
    }

    public bool CanSkipNext
    {
        get => _canSkipNext;
        private set => SetField(ref _canSkipNext, value);
    }

    public bool CanSkipPrevious
    {
        get => _canSkipPrevious;
        private set => SetField(ref _canSkipPrevious, value);
    }

    public bool CanSeek
    {
        get => _canSeek;
        private set => SetField(ref _canSeek, value);
    }

    public bool ShowSeekUnsupportedHint
    {
        get => _showSeekUnsupportedHint;
        private set => SetField(ref _showSeekUnsupportedHint, value);
    }

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public string ArtistSubtitle
    {
        get => _artistSubtitle;
        private set => SetField(ref _artistSubtitle, value);
    }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => SetField(ref _statusLabel, value);
    }

    public string CompactTitle
    {
        get => _compactTitle;
        private set => SetField(ref _compactTitle, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetField(ref _elapsedText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetField(ref _durationText, value);
    }

    public double ProgressSeconds
    {
        get => _progressSeconds;
        private set => SetField(ref _progressSeconds, value);
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set => SetField(ref _durationSeconds, value);
    }

    public BitmapSource? ArtworkBitmap
    {
        get => _artworkBitmap;
        private set => SetField(ref _artworkBitmap, value);
    }

    public string PlayPauseActionLabel
    {
        get => _playPauseActionLabel;
        private set => SetField(ref _playPauseActionLabel, value);
    }

    public string AccessibleStatus
    {
        get => _accessibleStatus;
        private set => SetField(ref _accessibleStatus, value);
    }

    public bool IsUserScrubbing
    {
        get
        {
            lock (_sync)
            {
                return _isUserScrubbing;
            }
        }
    }

    public bool ShowCompactActivePill
    {
        get
        {
            lock (_sync)
            {
                return _settings.ShowCompactActivePill;
            }
        }
    }

    public bool ShowAlbumArtInCompactPill
    {
        get
        {
            lock (_sync)
            {
                return _settings.ShowAlbumArtInCompactPill;
            }
        }
    }

    public bool ShowArtworkInCompactPill => HasArtwork && ShowAlbumArtInCompactPill;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SettingsChanged;

    public void AttachUiDispatcher(Action<Action> dispatch)
    {
        _uiDispatcher = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        ApplySession(_mediaAdapter.CurrentSession, _utcNowProvider());
    }

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var rawJson = await context.Settings.ReadJsonAsync(ct).ConfigureAwait(false);
        var loaded = ParseSettingsOrDefault(rawJson);

        lock (_sync)
        {
            _settings = loaded;
        }

        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(ShowCompactActivePill));
            OnPropertyChanged(nameof(ShowAlbumArtInCompactPill));
            OnPropertyChanged(nameof(ShowArtworkInCompactPill));
        });
    }

    public void ApplySession(MediaSessionSnapshot? snapshot, DateTimeOffset utcNow)
    {
        BitmapSource? nextArtwork;
        lock (_sync)
        {
            _session = snapshot;
            if (snapshot?.ThumbnailBytes is { Length: > 0 } thumb)
            {
                if (!ReferenceEquals(thumb, _cachedThumbnailBytes))
                {
                    _cachedThumbnailBytes = thumb;
                    _cachedArtworkBitmap = TryDecodeArtwork(thumb);
                }

                nextArtwork = _cachedArtworkBitmap;
            }
            else
            {
                _cachedThumbnailBytes = null;
                _cachedArtworkBitmap = null;
                nextArtwork = null;
            }
        }

        if (snapshot is null || snapshot.PlaybackState == MediaPlaybackState.Closed)
        {
            DispatchToUi(() =>
            {
                HasSession = false;
                HasArtwork = false;
                ArtworkBitmap = null;
                IsPlaying = false;
                CanPlayPause = false;
                CanSkipNext = false;
                CanSkipPrevious = false;
                CanSeek = false;
                ShowSeekUnsupportedHint = false;
                Title = "Nothing playing";
                ArtistSubtitle = "Play media in any supported app";
                StatusLabel = "Idle";
                CompactTitle = string.Empty;
                ElapsedText = "0:00";
                DurationText = "0:00";
                DurationSeconds = 1.0;
                ProgressSeconds = 0.0;
                PlayPauseActionLabel = "Play media";
                AccessibleStatus = "Now Playing: no active media session";
            });
            return;
        }

        var displayTitle = string.IsNullOrWhiteSpace(snapshot.Title) ? "Unknown Track" : snapshot.Title.Trim();
        var artistPart = snapshot.Artist?.Trim() ?? string.Empty;
        var albumPart = snapshot.AlbumTitle?.Trim() ?? string.Empty;
        var subtitle = !string.IsNullOrEmpty(artistPart) && !string.IsNullOrEmpty(albumPart)
            ? $"{artistPart} — {albumPart}"
            : !string.IsNullOrEmpty(artistPart)
                ? artistPart
                : !string.IsNullOrEmpty(albumPart)
                    ? albumPart
                    : string.IsNullOrWhiteSpace(snapshot.SourceAppId) ? "Active Media Session" : snapshot.SourceAppId;

        var statusLabel = snapshot.PlaybackState switch
        {
            MediaPlaybackState.Playing => "Playing",
            MediaPlaybackState.Paused => "Paused",
            MediaPlaybackState.Stopped => "Stopped",
            MediaPlaybackState.Changing => "Buffering",
            _ => "Ready"
        };

        var compact = string.IsNullOrEmpty(artistPart)
            ? displayTitle
            : $"{displayTitle} · {artistPart}";
        var computedPos = ComputeInterpolatedPosition(snapshot, utcNow);
        var hasDuration = snapshot.Duration > TimeSpan.Zero;
        var canSeek = snapshot.IsPlaybackPositionEnabled && hasDuration;

        DispatchToUi(() =>
        {
            HasSession = true;
            ArtworkBitmap = nextArtwork;
            HasArtwork = nextArtwork is not null;
            IsPlaying = snapshot.IsPlaying;
            CanPlayPause = snapshot.CanPlayPause;
            CanSkipNext = snapshot.CanSkipNext;
            CanSkipPrevious = snapshot.CanSkipPrevious;
            CanSeek = canSeek;
            ShowSeekUnsupportedHint = !canSeek;
            Title = displayTitle;
            ArtistSubtitle = subtitle;
            StatusLabel = statusLabel;
            CompactTitle = compact;
            DurationSeconds = hasDuration ? snapshot.Duration.TotalSeconds : 1.0;
            DurationText = hasDuration ? FormatTime(snapshot.Duration) : "--:--";
            if (!IsUserScrubbing)
            {
                ProgressSeconds = hasDuration ? computedPos.TotalSeconds : 0.0;
                ElapsedText = FormatTime(computedPos);
            }

            PlayPauseActionLabel = snapshot.IsPlaying ? "Pause playback" : "Play track";
            AccessibleStatus = $"Now Playing: {displayTitle}, {subtitle}, {statusLabel}";
        });
    }

    public void TickInterpolatedPosition(DateTimeOffset utcNow)
    {
        MediaSessionSnapshot? snapshot;
        lock (_sync)
        {
            if (_isUserScrubbing || _session is not { IsPlaying: true } active || active.Duration <= TimeSpan.Zero)
            {
                return;
            }

            snapshot = active;
        }

        var pos = ComputeInterpolatedPosition(snapshot, utcNow);
        DispatchToUi(() =>
        {
            if (!IsUserScrubbing)
            {
                ProgressSeconds = pos.TotalSeconds;
                ElapsedText = FormatTime(pos);
            }
        });
    }

    public void BeginScrub()
    {
        lock (_sync)
        {
            _isUserScrubbing = true;
        }
    }

    public void PreviewScrubSeconds(double seconds)
    {
        var clamped = Math.Max(0.0, Math.Min(seconds, DurationSeconds));
        DispatchToUi(() =>
        {
            ProgressSeconds = clamped;
            ElapsedText = FormatTime(TimeSpan.FromSeconds(clamped));
        });
    }

    public async Task CommitScrubAsync(double seconds, CancellationToken ct = default)
    {
        lock (_sync)
        {
            _isUserScrubbing = false;
        }

        if (!CanSeek)
        {
            return;
        }

        var clamped = Math.Clamp(seconds, 0.0, DurationSeconds);
        await SeekAsync(TimeSpan.FromSeconds(clamped), ct).ConfigureAwait(false);
    }

    public Task<bool> TogglePlayPauseAsync(CancellationToken ct = default) =>
        _mediaAdapter.TogglePlayPauseAsync(ct);

    public Task<bool> NextTrackAsync(CancellationToken ct = default) =>
        _mediaAdapter.NextTrackAsync(ct);

    public Task<bool> PreviousTrackAsync(CancellationToken ct = default) =>
        _mediaAdapter.PreviousTrackAsync(ct);

    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (!CanSeek)
        {
            return false;
        }

        var ok = await _mediaAdapter.SeekAsync(position, ct).ConfigureAwait(false);
        if (ok)
        {
            var clamped = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0.0, DurationSeconds));
            DispatchToUi(() =>
            {
                ProgressSeconds = clamped.TotalSeconds;
                ElapsedText = FormatTime(clamped);
            });
        }

        return ok;
    }

    public Task SetShowCompactActivePillAsync(bool enabled, CancellationToken ct = default) =>
        UpdateSettingsAsync(s => s with { ShowCompactActivePill = enabled }, ct);

    public Task SetShowAlbumArtInCompactPillAsync(bool enabled, CancellationToken ct = default) =>
        UpdateSettingsAsync(s => s with { ShowAlbumArtInCompactPill = enabled }, ct);

    public async Task ResetToDefaultsAsync(CancellationToken ct = default)
    {
        var defaults = new NowPlayingSettings();
        lock (_sync)
        {
            _settings = defaults;
        }

        if (_context is not null)
        {
            await _context.Settings.WriteJsonAsync(JsonSerializer.Serialize(defaults, JsonOptions), ct)
                .ConfigureAwait(false);
        }

        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(ShowCompactActivePill));
            OnPropertyChanged(nameof(ShowAlbumArtInCompactPill));
            OnPropertyChanged(nameof(ShowArtworkInCompactPill));
        });

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string? BuildCompactBadgeText(MediaSessionSnapshot? snapshot, NowPlayingSettings settings)
    {
        if (!settings.ShowCompactActivePill || snapshot is not { IsPlaying: true })
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Title))
        {
            return snapshot.Title.Trim();
        }

        return string.IsNullOrWhiteSpace(snapshot.Artist) ? "Playing" : snapshot.Artist.Trim();
    }

    internal static TimeSpan ComputeInterpolatedPosition(MediaSessionSnapshot snapshot, DateTimeOffset utcNow)
    {
        var basePos = snapshot.Position < TimeSpan.Zero ? TimeSpan.Zero : snapshot.Position;
        if (!snapshot.IsPlaying || snapshot.LastUpdatedUtc == default)
        {
            return snapshot.Duration > TimeSpan.Zero && basePos > snapshot.Duration
                ? snapshot.Duration
                : basePos;
        }

        var delta = utcNow - snapshot.LastUpdatedUtc;
        if (delta > TimeSpan.Zero)
        {
            basePos += delta;
        }

        if (snapshot.Duration > TimeSpan.Zero && basePos > snapshot.Duration)
        {
            return snapshot.Duration;
        }

        return basePos < TimeSpan.Zero ? TimeSpan.Zero : basePos;
    }

    internal static string FormatTime(TimeSpan value)
    {
        var safe = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        if (safe.TotalHours >= 1.0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1:00}:{2:00}",
                (int)safe.TotalHours,
                safe.Minutes,
                safe.Seconds);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1:00}",
            safe.Minutes,
            safe.Seconds);
    }

    private async Task UpdateSettingsAsync(
        Func<NowPlayingSettings, NowPlayingSettings> transform,
        CancellationToken ct)
    {
        NowPlayingSettings updated;
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

        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(ShowCompactActivePill));
            OnPropertyChanged(nameof(ShowAlbumArtInCompactPill));
            OnPropertyChanged(nameof(ShowArtworkInCompactPill));
        });

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static BitmapSource? TryDecodeArtwork(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static NowPlayingSettings ParseSettingsOrDefault(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || string.Equals(rawJson.Trim(), "{}", StringComparison.Ordinal))
        {
            return new NowPlayingSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<NowPlayingSettings>(rawJson, JsonOptions) ?? new NowPlayingSettings();
        }
        catch (JsonException)
        {
            return new NowPlayingSettings();
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
