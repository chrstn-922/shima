using System.IO;
using Shima.Core.Contracts;
using Windows.Media.Control;

namespace Shima.WindowsInterop.Media;

internal sealed record RawMediaProperties(
    string Title,
    string Artist,
    string AlbumTitle,
    byte[]? ThumbnailBytes,
    string? ThumbnailContentType);

internal sealed record RawPlaybackInfo(
    MediaPlaybackState PlaybackState,
    bool CanPlayPause,
    bool CanSkipNext,
    bool CanSkipPrevious,
    bool IsPlaybackPositionEnabled);

internal sealed record RawTimelineInfo(
    TimeSpan StartTime,
    TimeSpan EndTime,
    TimeSpan Position,
    DateTimeOffset LastUpdatedUtc);

internal interface IMediaSessionHandle : IDisposable
{
    string SourceAppId { get; }

    event EventHandler? MediaPropertiesChanged;

    event EventHandler? PlaybackInfoChanged;

    event EventHandler? TimelinePropertiesChanged;

    Task<RawMediaProperties?> TryGetMediaPropertiesAsync(CancellationToken ct = default);

    RawPlaybackInfo? GetPlaybackInfo();

    RawTimelineInfo? GetTimelineProperties();

    Task<bool> TryTogglePlayPauseAsync(CancellationToken ct = default);

    Task<bool> TrySkipNextAsync(CancellationToken ct = default);

    Task<bool> TrySkipPreviousAsync(CancellationToken ct = default);

    Task<bool> TrySeekAsync(long positionTicks, CancellationToken ct = default);
}

internal interface IMediaSessionSource : IDisposable
{
    event EventHandler? CurrentSessionChanged;

    Task<bool> InitializeAsync(CancellationToken ct = default);

    IMediaSessionHandle? GetCurrentSession();

    void Shutdown();
}

internal sealed class WinRtMediaSessionSource : IMediaSessionSource
{
    private readonly object _sync = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _disposed;

    public event EventHandler? CurrentSessionChanged;

    public async Task<bool> InitializeAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct).ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed)
                {
                    return false;
                }

                DetachManagerLocked();
                _manager = manager;
                _manager.CurrentSessionChanged += OnManagerCurrentSessionChanged;
                _manager.SessionsChanged += OnManagerSessionsChanged;
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public IMediaSessionHandle? GetCurrentSession()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_sync)
        {
            manager = _manager;
        }

        if (manager is null)
        {
            return null;
        }

        try
        {
            var session = manager.GetCurrentSession();
            return session is null ? null : new WinRtMediaSessionHandle(session);
        }
        catch
        {
            return null;
        }
    }

    public void Shutdown()
    {
        lock (_sync)
        {
            DetachManagerLocked();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Shutdown();
    }

    private void OnManagerCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args) =>
        CurrentSessionChanged?.Invoke(this, EventArgs.Empty);

    private void OnManagerSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args) =>
        CurrentSessionChanged?.Invoke(this, EventArgs.Empty);

    private void DetachManagerLocked()
    {
        if (_manager is null)
        {
            return;
        }

        try
        {
            _manager.CurrentSessionChanged -= OnManagerCurrentSessionChanged;
            _manager.SessionsChanged -= OnManagerSessionsChanged;
        }
        catch
        {
            // Ignore COM teardown issues if WinRT runtime is shutting down.
        }

        _manager = null;
    }
}

internal sealed class WinRtMediaSessionHandle : IMediaSessionHandle
{
    private const int MaxThumbnailBytes = 2 * 1024 * 1024;

    private readonly GlobalSystemMediaTransportControlsSession _session;
    private bool _disposed;

    public WinRtMediaSessionHandle(GlobalSystemMediaTransportControlsSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        SourceAppId = SafeReadSourceAppId(session);
        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
    }

    public string SourceAppId { get; }

    public event EventHandler? MediaPropertiesChanged;

    public event EventHandler? PlaybackInfoChanged;

    public event EventHandler? TimelinePropertiesChanged;

    public async Task<RawMediaProperties?> TryGetMediaPropertiesAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return null;
        }

        try
        {
            var props = await _session.TryGetMediaPropertiesAsync().AsTask(ct).ConfigureAwait(false);
            if (props is null)
            {
                return null;
            }

            byte[]? thumbnailBytes = null;
            string? thumbnailContentType = null;

            if (props.Thumbnail is not null)
            {
                try
                {
                    using var ras = await props.Thumbnail.OpenReadAsync().AsTask(ct).ConfigureAwait(false);
                    thumbnailContentType = ras.ContentType;
                    using var stream = ras.AsStreamForRead();
                    using var ms = new MemoryStream();
                    var buffer = new byte[8192];
                    int read;
                    while (ms.Length < MaxThumbnailBytes
                           && (read = await stream.ReadAsync(
                               buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxThumbnailBytes - ms.Length)),
                               ct).ConfigureAwait(false)) > 0)
                    {
                        ms.Write(buffer, 0, read);
                    }

                    if (ms.Length > 0 && ms.Length <= MaxThumbnailBytes)
                    {
                        thumbnailBytes = ms.ToArray();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    thumbnailBytes = null;
                    thumbnailContentType = null;
                }
            }

            return new RawMediaProperties(
                props.Title ?? string.Empty,
                props.Artist ?? string.Empty,
                props.AlbumTitle ?? string.Empty,
                thumbnailBytes,
                thumbnailContentType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public RawPlaybackInfo? GetPlaybackInfo()
    {
        if (_disposed)
        {
            return null;
        }

        try
        {
            var info = _session.GetPlaybackInfo();
            if (info is null)
            {
                return null;
            }

            var controls = info.Controls;
            var canPlayPause = controls is not null
                && (controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled);

            return new RawPlaybackInfo(
                MapPlaybackStatus(info.PlaybackStatus),
                canPlayPause,
                controls?.IsNextEnabled == true,
                controls?.IsPreviousEnabled == true,
                controls?.IsPlaybackPositionEnabled == true);
        }
        catch
        {
            return null;
        }
    }

    public RawTimelineInfo? GetTimelineProperties()
    {
        if (_disposed)
        {
            return null;
        }

        try
        {
            var timeline = _session.GetTimelineProperties();
            if (timeline is null)
            {
                return null;
            }

            var updated = timeline.LastUpdatedTime == default
                ? DateTimeOffset.UtcNow
                : timeline.LastUpdatedTime.ToUniversalTime();

            return new RawTimelineInfo(
                timeline.StartTime,
                timeline.EndTime,
                timeline.Position,
                updated);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> TryTogglePlayPauseAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            if (await _session.TryTogglePlayPauseAsync().AsTask(ct).ConfigureAwait(false))
            {
                return true;
            }

            var playback = _session.GetPlaybackInfo();
            if (playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return await _session.TryPauseAsync().AsTask(ct).ConfigureAwait(false);
            }

            return await _session.TryPlayAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TrySkipNextAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            return await _session.TrySkipNextAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TrySkipPreviousAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            return await _session.TrySkipPreviousAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TrySeekAsync(long positionTicks, CancellationToken ct = default)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            return await _session.TryChangePlaybackPositionAsync(positionTicks).AsTask(ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch
        {
            // Ignore WinRT teardown failures.
        }
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) =>
        MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) =>
        PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args) =>
        TimelinePropertiesChanged?.Invoke(this, EventArgs.Empty);

    private static string SafeReadSourceAppId(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.SourceAppUserModelId ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static MediaPlaybackState MapPlaybackStatus(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status) =>
        status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackState.Opened,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackState.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
            _ => MediaPlaybackState.Closed
        };
}

/// <summary>
/// Event-driven WinRT GSMTC adapter providing track metadata, artwork stream decoding, playback transport,
/// and guarded timeline seeking with zero polling timers.
/// </summary>
public sealed class MediaSessionAdapter : IMediaSessionAdapter
{
    private readonly IMediaSessionSource _source;
    private readonly ILogger? _logger;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private IMediaSessionHandle? _activeHandle;
    private RawMediaProperties? _cachedProperties;
    private MediaSessionSnapshot? _currentSession;
    private TimeSpan _lastTimelineStartTime;
    private long _refreshGeneration;
    private bool _started;
    private bool _disposed;

    public MediaSessionAdapter(ILogger? logger = null)
        : this(new WinRtMediaSessionSource(), logger, null)
    {
    }

    internal MediaSessionAdapter(
        IMediaSessionSource source,
        ILogger? logger = null,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logger = logger;
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public MediaSessionSnapshot? CurrentSession
    {
        get
        {
            lock (_sync)
            {
                return _currentSession;
            }
        }
    }

    public event EventHandler<MediaSessionSnapshot?>? SessionChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _source.CurrentSessionChanged += OnSourceCurrentSessionChanged;
        }

        var initialized = await _source.InitializeAsync(ct).ConfigureAwait(false);
        if (!initialized)
        {
            _logger?.Warn("GSMTC media session manager unavailable; Now Playing will stay idle until available.");
            return;
        }

        await RefreshActiveSessionAsync(forceRefreshProperties: true, ct).ConfigureAwait(false);
    }

    public void Stop()
    {
        bool hadSession;
        lock (_sync)
        {
            if (!_started && _activeHandle is null && _currentSession is null)
            {
                return;
            }

            _started = false;
            _refreshGeneration++;
            _source.CurrentSessionChanged -= OnSourceCurrentSessionChanged;
            DetachActiveHandleLocked();
            hadSession = _currentSession is not null;
            _currentSession = null;
            _cachedProperties = null;
            _lastTimelineStartTime = TimeSpan.Zero;
        }

        try
        {
            _source.Shutdown();
        }
        catch (Exception ex)
        {
            _logger?.Warn("Failed while shutting down GSMTC source.", ex);
        }

        if (hadSession)
        {
            SessionChanged?.Invoke(this, null);
        }
    }

    public async Task<bool> TogglePlayPauseAsync(CancellationToken ct = default)
    {
        var handle = GetActiveHandleSnapshot();
        return handle is not null && await handle.TryTogglePlayPauseAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> NextTrackAsync(CancellationToken ct = default)
    {
        var handle = GetActiveHandleSnapshot();
        return handle is not null && await handle.TrySkipNextAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> PreviousTrackAsync(CancellationToken ct = default)
    {
        var handle = GetActiveHandleSnapshot();
        return handle is not null && await handle.TrySkipPreviousAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        IMediaSessionHandle? handle;
        MediaSessionSnapshot? snapshot;
        TimeSpan startTime;
        lock (_sync)
        {
            handle = _activeHandle;
            snapshot = _currentSession;
            startTime = _lastTimelineStartTime;
        }

        if (handle is null
            || snapshot is null
            || !snapshot.IsPlaybackPositionEnabled
            || snapshot.Duration <= TimeSpan.Zero)
        {
            return false;
        }

        var clamped = position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > snapshot.Duration
                ? snapshot.Duration
                : position;

        var targetTicks = (startTime + clamped).Ticks;
        return await handle.TrySeekAsync(targetTicks, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _source.Dispose();
        _refreshGate.Dispose();
    }

    private IMediaSessionHandle? GetActiveHandleSnapshot()
    {
        lock (_sync)
        {
            return _disposed || !_started ? null : _activeHandle;
        }
    }

    private void OnSourceCurrentSessionChanged(object? sender, EventArgs e) =>
        _ = RefreshActiveSessionSafeAsync(forceRefreshProperties: true);

    private void OnHandleMediaPropertiesChanged(object? sender, EventArgs e) =>
        _ = RefreshSnapshotFromHandleSafeAsync(sender as IMediaSessionHandle, refreshProperties: true);

    private void OnHandlePlaybackInfoChanged(object? sender, EventArgs e) =>
        _ = RefreshSnapshotFromHandleSafeAsync(sender as IMediaSessionHandle, refreshProperties: false);

    private void OnHandleTimelinePropertiesChanged(object? sender, EventArgs e) =>
        _ = RefreshSnapshotFromHandleSafeAsync(sender as IMediaSessionHandle, refreshProperties: false);

    private async Task RefreshActiveSessionSafeAsync(bool forceRefreshProperties)
    {
        try
        {
            await RefreshActiveSessionAsync(forceRefreshProperties, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Warn("Error refreshing active GSMTC media session.", ex);
        }
    }

    private async Task RefreshSnapshotFromHandleSafeAsync(IMediaSessionHandle? senderHandle, bool refreshProperties)
    {
        try
        {
            await RefreshSnapshotFromHandleAsync(senderHandle, refreshProperties, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Warn("Error refreshing GSMTC media session metadata.", ex);
        }
    }

    private async Task RefreshActiveSessionAsync(bool forceRefreshProperties, CancellationToken ct)
    {
        if (_disposed)
        {
            return;
        }

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        MediaSessionSnapshot? emitted = null;
        var shouldEmit = false;
        try
        {
            bool started;
            lock (_sync)
            {
                started = _started && !_disposed;
            }

            if (!started)
            {
                return;
            }

            var nextHandle = _source.GetCurrentSession();
            long generation;
            lock (_sync)
            {
                DetachActiveHandleLocked();
                _activeHandle = nextHandle;
                _cachedProperties = null;
                _lastTimelineStartTime = TimeSpan.Zero;
                generation = ++_refreshGeneration;

                if (nextHandle is not null)
                {
                    nextHandle.MediaPropertiesChanged += OnHandleMediaPropertiesChanged;
                    nextHandle.PlaybackInfoChanged += OnHandlePlaybackInfoChanged;
                    nextHandle.TimelinePropertiesChanged += OnHandleTimelinePropertiesChanged;
                }
            }

            if (nextHandle is null)
            {
                lock (_sync)
                {
                    shouldEmit = _currentSession is not null;
                    _currentSession = null;
                    emitted = null;
                }
            }
            else
            {
                (emitted, shouldEmit) = await BuildAndCommitSnapshotAsync(
                    nextHandle,
                    generation,
                    refreshProperties: forceRefreshProperties,
                    ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _refreshGate.Release();
        }

        if (shouldEmit)
        {
            SessionChanged?.Invoke(this, emitted);
        }
    }

    private async Task RefreshSnapshotFromHandleAsync(
        IMediaSessionHandle? senderHandle,
        bool refreshProperties,
        CancellationToken ct)
    {
        if (_disposed || senderHandle is null)
        {
            return;
        }

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        MediaSessionSnapshot? emitted = null;
        var shouldEmit = false;
        try
        {
            long generation;
            lock (_sync)
            {
                if (_disposed || !_started || !ReferenceEquals(_activeHandle, senderHandle))
                {
                    return;
                }

                generation = _refreshGeneration;
            }

            (emitted, shouldEmit) = await BuildAndCommitSnapshotAsync(
                senderHandle,
                generation,
                refreshProperties,
                ct).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }

        if (shouldEmit)
        {
            SessionChanged?.Invoke(this, emitted);
        }
    }

    private async Task<(MediaSessionSnapshot? Snapshot, bool ShouldEmit)> BuildAndCommitSnapshotAsync(
        IMediaSessionHandle handle,
        long expectedGeneration,
        bool refreshProperties,
        CancellationToken ct)
    {
        RawMediaProperties? props;
        lock (_sync)
        {
            props = _cachedProperties;
        }

        if (refreshProperties || props is null)
        {
            props = await handle.TryGetMediaPropertiesAsync(ct).ConfigureAwait(false)
                ?? new RawMediaProperties(string.Empty, string.Empty, string.Empty, null, null);
        }

        var playback = handle.GetPlaybackInfo()
            ?? new RawPlaybackInfo(MediaPlaybackState.Closed, false, false, false, false);
        var timeline = handle.GetTimelineProperties();

        var startTime = timeline?.StartTime ?? TimeSpan.Zero;
        var duration = timeline is not null && timeline.EndTime > timeline.StartTime
            ? timeline.EndTime - timeline.StartTime
            : TimeSpan.Zero;

        var rawPos = timeline is not null ? timeline.Position - startTime : TimeSpan.Zero;
        var position = duration > TimeSpan.Zero
            ? (rawPos < TimeSpan.Zero ? TimeSpan.Zero : rawPos > duration ? duration : rawPos)
            : (rawPos < TimeSpan.Zero ? TimeSpan.Zero : rawPos);

        var updatedUtc = timeline?.LastUpdatedUtc ?? _utcNowProvider();

        var snapshot = new MediaSessionSnapshot
        {
            SourceAppId = handle.SourceAppId ?? string.Empty,
            Title = props.Title ?? string.Empty,
            Artist = props.Artist ?? string.Empty,
            AlbumTitle = props.AlbumTitle ?? string.Empty,
            PlaybackState = playback.PlaybackState,
            CanPlayPause = playback.CanPlayPause,
            CanSkipNext = playback.CanSkipNext,
            CanSkipPrevious = playback.CanSkipPrevious,
            IsPlaybackPositionEnabled = playback.IsPlaybackPositionEnabled,
            Position = position,
            Duration = duration,
            LastUpdatedUtc = updatedUtc,
            ThumbnailBytes = props.ThumbnailBytes,
            ThumbnailContentType = props.ThumbnailContentType
        };

        lock (_sync)
        {
            if (_disposed || !_started || !ReferenceEquals(_activeHandle, handle) || _refreshGeneration != expectedGeneration)
            {
                return (null, false);
            }

            _cachedProperties = props;
            _lastTimelineStartTime = startTime;
            _currentSession = snapshot;
            return (snapshot, true);
        }
    }

    private void DetachActiveHandleLocked()
    {
        if (_activeHandle is null)
        {
            return;
        }

        _activeHandle.MediaPropertiesChanged -= OnHandleMediaPropertiesChanged;
        _activeHandle.PlaybackInfoChanged -= OnHandlePlaybackInfoChanged;
        _activeHandle.TimelinePropertiesChanged -= OnHandleTimelinePropertiesChanged;
        _activeHandle.Dispose();
        _activeHandle = null;
    }
}
