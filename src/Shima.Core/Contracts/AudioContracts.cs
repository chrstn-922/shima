namespace Shima.Core.Contracts;

public sealed record RawBgraImage(int Width, int Height, byte[] Pixels)
{
    public bool IsValid => Width > 0 && Height > 0 && Pixels is not null && Pixels.Length == Width * Height * 4;
}

public enum AudioSessionActivityState
{
    Inactive,
    Active,
    Expired
}

public sealed record AudioSessionSnapshot
{
    public required string SessionKey { get; init; }

    public uint ProcessId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string? ProcessName { get; init; }

    public string? ExecutablePath { get; init; }

    public float Volume { get; init; }

    public bool IsMuted { get; init; }

    public bool IsSystemSounds { get; init; }

    public AudioSessionActivityState State { get; init; }

    public RawBgraImage? IconImage { get; init; }
}

public sealed record AudioMixerSnapshot
{
    public static AudioMixerSnapshot Unavailable { get; } = new()
    {
        DeviceId = string.Empty,
        DeviceName = "No audio output device",
        MasterVolume = 0f,
        IsMasterMuted = false,
        IsAvailable = false,
        Sessions = Array.Empty<AudioSessionSnapshot>()
    };

    public string DeviceId { get; init; } = string.Empty;

    public string DeviceName { get; init; } = string.Empty;

    public float MasterVolume { get; init; }

    public bool IsMasterMuted { get; init; }

    public bool IsAvailable { get; init; }

    public IReadOnlyList<AudioSessionSnapshot> Sessions { get; init; } = Array.Empty<AudioSessionSnapshot>();
}

public interface IAudioMixerAdapter : IDisposable
{
    AudioMixerSnapshot CurrentSnapshot { get; }

    event EventHandler<AudioMixerSnapshot>? MixerChanged;

    Task StartAsync(CancellationToken ct = default);

    void Stop();

    Task<bool> SetMasterVolumeAsync(float volume, CancellationToken ct = default);

    Task<bool> SetMasterMuteAsync(bool isMuted, CancellationToken ct = default);

    Task<bool> SetSessionVolumeAsync(string sessionKey, float volume, CancellationToken ct = default);

    Task<bool> SetSessionMuteAsync(string sessionKey, bool isMuted, CancellationToken ct = default);
}
