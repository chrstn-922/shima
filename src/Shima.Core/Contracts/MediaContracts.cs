namespace Shima.Core.Contracts;

public enum MediaPlaybackState
{
    Closed,
    Opened,
    Changing,
    Stopped,
    Playing,
    Paused
}

public sealed record MediaSessionSnapshot
{
    public string SourceAppId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public string AlbumTitle { get; init; } = string.Empty;

    public MediaPlaybackState PlaybackState { get; init; } = MediaPlaybackState.Closed;

    public bool CanPlayPause { get; init; }

    public bool CanSkipNext { get; init; }

    public bool CanSkipPrevious { get; init; }

    public bool IsPlaybackPositionEnabled { get; init; }

    public TimeSpan Position { get; init; }

    public TimeSpan Duration { get; init; }

    public DateTimeOffset LastUpdatedUtc { get; init; }

    public byte[]? ThumbnailBytes { get; init; }

    public string? ThumbnailContentType { get; init; }

    public bool IsPlaying => PlaybackState == MediaPlaybackState.Playing;

    public bool CanSeek => IsPlaybackPositionEnabled && Duration > TimeSpan.Zero;
}

public interface IMediaSessionAdapter : IDisposable
{
    MediaSessionSnapshot? CurrentSession { get; }

    event EventHandler<MediaSessionSnapshot?>? SessionChanged;

    Task StartAsync(CancellationToken ct = default);

    void Stop();

    Task<bool> TogglePlayPauseAsync(CancellationToken ct = default);

    Task<bool> NextTrackAsync(CancellationToken ct = default);

    Task<bool> PreviousTrackAsync(CancellationToken ct = default);

    Task<bool> SeekAsync(TimeSpan position, CancellationToken ct = default);
}
