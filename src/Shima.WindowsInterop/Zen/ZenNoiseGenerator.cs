using NAudio.Wave;
using Shima.Core.Contracts;

namespace Shima.WindowsInterop.Zen;

/// <summary>
/// Pure managed procedural DSP noise generator (White, -3dB/oct Pink, -6dB/oct Brown) implementing
/// <see cref="ISampleProvider"/> with zero disk/network streaming and zero per-read heap allocations.
/// </summary>
public sealed class ZenNoiseSampleProvider : ISampleProvider
{
    public const int DefaultSampleRate = 44100;
    private const int FadeInSamples = 4410; // ~100 ms soft ramp to prevent clicks

    private readonly ZenNoiseType _noiseType;
    private uint _rngState;
    private double _volume;
    private int _samplesGenerated;

    // Paul Kellet pink noise state variables
    private float _b0;
    private float _b1;
    private float _b2;
    private float _b3;
    private float _b4;
    private float _b5;
    private float _b6;

    // Brown noise leaky integrator state
    private float _brownLast;

    public ZenNoiseSampleProvider(ZenNoiseType noiseType, double volume = 0.35, uint? seed = null)
    {
        if (noiseType == ZenNoiseType.None || !Enum.IsDefined(noiseType))
        {
            throw new ArgumentOutOfRangeException(nameof(noiseType), "Noise type must be White, Pink, or Brown.");
        }

        _noiseType = noiseType;
        _volume = Math.Clamp(volume, 0.0, 1.0);
        _rngState = (seed is uint s && s != 0u) ? s : 0x6D2B79F5u;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(DefaultSampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public ZenNoiseType NoiseType => _noiseType;

    public double Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, Math.Clamp(value, 0.0, 1.0));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var gain = (float)Volatile.Read(ref _volume);
        for (var i = 0; i < count; i++)
        {
            var raw = NextRawSample();
            var ramp = _samplesGenerated < FadeInSamples
                ? (_samplesGenerated / (float)FadeInSamples)
                : 1.0f;
            _samplesGenerated++;

            var scaled = raw * gain * ramp;
            if (!float.IsFinite(scaled))
            {
                scaled = 0f;
            }
            else if (scaled > 1.0f)
            {
                scaled = 1.0f;
            }
            else if (scaled < -1.0f)
            {
                scaled = -1.0f;
            }

            buffer[offset + i] = scaled;
        }

        return count;
    }

    private float NextRawSample()
    {
        var white = NextUniformMinusOneToOne();
        return _noiseType switch
        {
            ZenNoiseType.White => white * 0.25f,
            ZenNoiseType.Pink => NextPinkSample(white),
            ZenNoiseType.Brown => NextBrownSample(white),
            _ => 0f
        };
    }

    private float NextPinkSample(float white)
    {
        _b0 = (0.99886f * _b0) + (white * 0.0555179f);
        _b1 = (0.99332f * _b1) + (white * 0.0750759f);
        _b2 = (0.96900f * _b2) + (white * 0.1538520f);
        _b3 = (0.86650f * _b3) + (white * 0.3104856f);
        _b4 = (0.55000f * _b4) + (white * 0.5329522f);
        _b5 = (-0.7616f * _b5) - (white * 0.0168980f);
        var pink = (_b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + (white * 0.5362f)) * 0.045f;
        _b6 = white * 0.115926f;
        return pink;
    }

    private float NextBrownSample(float white)
    {
        _brownLast = (_brownLast + (0.02f * white)) / 1.02f;
        return _brownLast * 1.35f;
    }

    private float NextUniformMinusOneToOne()
    {
        // Xorshift32 PRNG mapped cleanly to [-1, 1] without heap allocation
        var x = _rngState;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rngState = x == 0 ? 0x6D2B79F5u : x;
        return ((_rngState >> 8) * (2.0f / 16777215.0f)) - 1.0f;
    }
}
