using NAudio.CoreAudioApi;
using NAudio.Wave;
using Shima.Core.Contracts;

namespace Shima.WindowsInterop.Zen;

internal interface IZenAudioPlaybackSession : IDisposable
{
    void UpdateVolume(double volume);
}

internal interface IZenAudioPlaybackBackend
{
    IZenAudioPlaybackSession StartSession(ZenNoiseType noiseType, double volume);
}

internal sealed class WasapiZenAudioPlaybackBackend : IZenAudioPlaybackBackend
{
    public IZenAudioPlaybackSession StartSession(ZenNoiseType noiseType, double volume)
    {
        var provider = new ZenNoiseSampleProvider(noiseType, volume);
        var output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 100);
        try
        {
            output.Init(provider);
            output.Play();
            return new WasapiSession(output, provider);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private sealed class WasapiSession(WasapiOut output, ZenNoiseSampleProvider provider) : IZenAudioPlaybackSession
    {
        private bool _disposed;

        public void UpdateVolume(double volume)
        {
            if (!_disposed)
            {
                provider.Volume = volume;
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
                output.Stop();
            }
            catch
            {
                // Best-effort cleanup if device unplugged.
            }

            output.Dispose();
        }
    }
}

/// <summary>
/// WASAPI DSP noise synthesizer adapter (White, Pink, Brown) that leaves zero active audio devices
/// or worker threads when stopped.
/// </summary>
public sealed class ZenAudioSynthAdapter : IZenAudioSynthAdapter
{
    private readonly IZenAudioPlaybackBackend _backend;
    private readonly ILogger? _logger;
    private readonly object _sync = new();

    private IZenAudioPlaybackSession? _activeSession;
    private ZenNoiseType _activeNoiseType = ZenNoiseType.None;
    private double _volume = 0.35;
    private bool _disposed;

    public ZenAudioSynthAdapter(ILogger? logger = null)
        : this(new WasapiZenAudioPlaybackBackend(), logger)
    {
    }

    internal ZenAudioSynthAdapter(IZenAudioPlaybackBackend backend, ILogger? logger = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger;
    }

    public bool IsPlaying
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && _activeSession is not null;
            }
        }
    }

    public ZenNoiseType ActiveNoiseType
    {
        get
        {
            lock (_sync)
            {
                return _activeNoiseType;
            }
        }
    }

    public double Volume
    {
        get
        {
            lock (_sync)
            {
                return _volume;
            }
        }
    }

    public void Start(ZenNoiseType noiseType, double volume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clampedVolume = Math.Clamp(volume, 0.0, 1.0);

        if (noiseType == ZenNoiseType.None)
        {
            lock (_sync)
            {
                _volume = clampedVolume;
            }

            Stop();
            return;
        }

        IZenAudioPlaybackSession? sessionToDispose = null;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _volume = clampedVolume;
            if (_activeSession is not null && _activeNoiseType == noiseType)
            {
                _activeSession.UpdateVolume(clampedVolume);
                return;
            }

            sessionToDispose = _activeSession;
            _activeSession = null;
            _activeNoiseType = ZenNoiseType.None;
        }

        sessionToDispose?.Dispose();

        try
        {
            var created = _backend.StartSession(noiseType, clampedVolume);
            lock (_sync)
            {
                if (_disposed)
                {
                    created.Dispose();
                    return;
                }

                _activeSession = created;
                _activeNoiseType = noiseType;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Failed to start Zen {noiseType} ambient noise WASAPI stream.", ex);
            lock (_sync)
            {
                _activeSession = null;
                _activeNoiseType = ZenNoiseType.None;
            }
        }
    }

    public void UpdateVolume(double volume)
    {
        var clamped = Math.Clamp(volume, 0.0, 1.0);
        IZenAudioPlaybackSession? session;
        lock (_sync)
        {
            _volume = clamped;
            session = _activeSession;
        }

        session?.UpdateVolume(clamped);
    }

    public void Stop()
    {
        IZenAudioPlaybackSession? session;
        lock (_sync)
        {
            session = _activeSession;
            _activeSession = null;
            _activeNoiseType = ZenNoiseType.None;
        }

        session?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
