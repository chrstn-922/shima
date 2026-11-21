namespace Shima.Widgets.NowPlaying;

public sealed record NowPlayingSettings
{
    public bool ShowCompactActivePill { get; init; } = true;

    public bool ShowAlbumArtInCompactPill { get; init; } = true;
}
