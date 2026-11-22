using System.Text.Json;
using FluentAssertions;
using Shima.Core.Contracts;
using Shima.WindowsInterop.Zen;
using Xunit;

namespace Shima.WindowsInterop.Tests;

public sealed class ZenAdaptersTests : IDisposable
{
    private readonly string _tempRoot;

    public ZenAdaptersTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "shima-zen-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void ZenNoiseSampleProvider_GeneratesFiniteBoundedDspNoiseWithExpectedSpectralTilt()
    {
        var white = GenerateSamples(ZenNoiseType.White, 0.8, 16384);
        var pink = GenerateSamples(ZenNoiseType.Pink, 0.8, 16384);
        var brown = GenerateSamples(ZenNoiseType.Brown, 0.8, 16384);

        AssertValidNoiseBuffer(white);
        AssertValidNoiseBuffer(pink);
        AssertValidNoiseBuffer(brown);

        // Low-frequency smoothing raises lag-1 autocorrelation monotonically: White < Pink < Brown
        var corrWhite = ComputeLag1Correlation(white, 4500);
        var corrPink = ComputeLag1Correlation(pink, 4500);
        var corrBrown = ComputeLag1Correlation(brown, 4500);

        corrWhite.Should().BeLessThan(corrPink);
        corrPink.Should().BeLessThan(corrBrown);
        corrBrown.Should().BeGreaterThan(0.9f);

        // Zero volume produces silence
        var silent = GenerateSamples(ZenNoiseType.Pink, 0.0, 1024);
        silent.Should().AllSatisfy(s => s.Should().Be(0f));
    }

    [Fact]
    public void ZenAudioSynthAdapter_ManagesPlaybackLifecycleAndLiveVolumeWithoutDeadlocks()
    {
        var backend = new FakeAudioBackend();
        using var adapter = new ZenAudioSynthAdapter(backend);

        adapter.IsPlaying.Should().BeFalse();
        adapter.Start(ZenNoiseType.White, 0.4);
        adapter.IsPlaying.Should().BeTrue();
        adapter.ActiveNoiseType.Should().Be(ZenNoiseType.White);
        backend.StartCount.Should().Be(1);

        // Same noise type only updates volume without restarting WASAPI device
        adapter.Start(ZenNoiseType.White, 0.75);
        backend.StartCount.Should().Be(1);
        backend.LastSession!.Volume.Should().BeApproximately(0.75, 0.001);

        // Switching noise type disposes old session cleanly and opens new one
        var firstSession = backend.LastSession!;
        adapter.Start(ZenNoiseType.Brown, 0.5);
        firstSession.IsDisposed.Should().BeTrue();
        backend.StartCount.Should().Be(2);
        adapter.ActiveNoiseType.Should().Be(ZenNoiseType.Brown);

        adapter.Stop();
        adapter.IsPlaying.Should().BeFalse();
        adapter.ActiveNoiseType.Should().Be(ZenNoiseType.None);
        backend.LastSession!.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void ZenDisplayEffectAdapter_WritesPreEffectJournal_AppliesMatrix_AndRecoversCleanlyAfterCrash()
    {
        // Verify matrix builder coefficients (BT.709 grayscale + dim, dim-only, identity)
        var grayDim = ZenDisplayEffectAdapter.BuildColorMatrix(new ZenDisplayOptions(true, 0.25));
        grayDim[0].Should().BeApproximately(0.2126f * 0.75f, 0.001f);
        grayDim[5].Should().BeApproximately(0.7152f * 0.75f, 0.001f);
        grayDim[10].Should().BeApproximately(0.0722f * 0.75f, 0.001f);
        grayDim[18].Should().BeApproximately(1.0f, 0.001f);

        var dimOnly = ZenDisplayEffectAdapter.BuildColorMatrix(new ZenDisplayOptions(false, 0.40));
        dimOnly[0].Should().BeApproximately(0.60f, 0.001f);
        dimOnly[6].Should().BeApproximately(0.60f, 0.001f);
        dimOnly[12].Should().BeApproximately(0.60f, 0.001f);

        // Simulate real on-disk runtime-journal.json + crash before shutdown cleanup
        var now = new DateTimeOffset(2026, 11, 22, 14, 0, 0, TimeSpan.Zero);
        var native = new FakeZenDisplayNativeBackend();
        var journalStore = new FileRuntimeJournalStore(_tempRoot);
        var adapter = new ZenDisplayEffectAdapter(native, journalStore, () => now);

        adapter.RecoverFromStartupIfNeeded().Should().BeFalse();
        adapter.WasCrashRecoveredOnStartup.Should().BeFalse();

        // Applying effect MUST persist ZenDisplayEffectActive=true BEFORE touching DWM Magnification
        var applied = adapter.TryApplyEffect(new ZenDisplayOptions(GrayscaleEnabled: true, DimOpacity: 0.30));
        applied.Should().BeTrue();
        adapter.IsEffectActive.Should().BeTrue();
        native.SawJournalActiveBeforeApply.Should().BeTrue();
        native.IsModified.Should().BeTrue();

        var journalPath = Path.Combine(_tempRoot, "config", "runtime-journal.json");
        File.Exists(journalPath).Should().BeTrue();
        var activeJournal = JsonSerializer.Deserialize<RuntimeRecoveryJournal>(
            File.ReadAllText(journalPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        activeJournal!.ZenDisplayEffectActive.Should().BeTrue();
        activeJournal.LastExitClean.Should().BeFalse();

        // Fullscreen pauses native matrix while keeping effect armed; exiting fullscreen restores it
        adapter.SetPausedByFullscreen(true);
        adapter.IsEffectActive.Should().BeFalse();
        native.IsModified.Should().BeFalse();

        adapter.SetPausedByFullscreen(false);
        adapter.IsEffectActive.Should().BeTrue();
        native.IsModified.Should().BeTrue();

        // Simulate abrupt kill (skip Dispose/MarkCleanExit) -> new process launches and recovers cleanly
        var relaunchedAdapter = new ZenDisplayEffectAdapter(native, new FileRuntimeJournalStore(_tempRoot), () => now.AddMinutes(1));
        relaunchedAdapter.RecoverFromStartupIfNeeded().Should().BeTrue();
        relaunchedAdapter.WasCrashRecoveredOnStartup.Should().BeTrue();
        native.IsModified.Should().BeFalse();

        var recoveredJournal = JsonSerializer.Deserialize<RuntimeRecoveryJournal>(
            File.ReadAllText(journalPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        recoveredJournal!.ZenDisplayEffectActive.Should().BeFalse();
        recoveredJournal.LastExitClean.Should().BeTrue();

        // Subsequent clean startup sees no dirty recovery flag
        var cleanAdapter = new ZenDisplayEffectAdapter(native, new FileRuntimeJournalStore(_tempRoot), () => now.AddMinutes(2));
        cleanAdapter.RecoverFromStartupIfNeeded().Should().BeFalse();

        native.JournalSnapshotReader = () => new FileRuntimeJournalStore(_tempRoot).Read(out _);
        cleanAdapter.MarkCleanExit();
    }

    private static float[] GenerateSamples(ZenNoiseType type, double volume, int count)
    {
        var provider = new ZenNoiseSampleProvider(type, volume, seed: 12345u);
        var buffer = new float[count];
        provider.Read(buffer, 0, count).Should().Be(count);
        return buffer;
    }

    private static void AssertValidNoiseBuffer(float[] samples)
    {
        samples.Should().AllSatisfy(s =>
        {
            float.IsFinite(s).Should().BeTrue();
            Math.Abs(s).Should().BeLessOrEqualTo(1.0f);
        });

        var energy = samples.Skip(4500).Sum(s => (double)s * s);
        energy.Should().BeGreaterThan(0.01);
    }

    private static float ComputeLag1Correlation(float[] samples, int startIndex)
    {
        double num = 0;
        double den = 0;
        for (var i = startIndex + 1; i < samples.Length; i++)
        {
            num += samples[i] * samples[i - 1];
            den += samples[i] * samples[i];
        }

        return den <= 1e-12 ? 0f : (float)(num / den);
    }

    private sealed class FakeAudioBackend : IZenAudioPlaybackBackend
    {
        public int StartCount { get; private set; }
        public FakeAudioSession? LastSession { get; private set; }

        public IZenAudioPlaybackSession StartSession(ZenNoiseType noiseType, double volume)
        {
            StartCount++;
            LastSession = new FakeAudioSession(volume);
            return LastSession;
        }
    }

    private sealed class FakeAudioSession(double initialVolume) : IZenAudioPlaybackSession
    {
        public double Volume { get; private set; } = initialVolume;
        public bool IsDisposed { get; private set; }

        public void UpdateVolume(double volume) => Volume = volume;

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeZenDisplayNativeBackend : IZenDisplayNativeBackend
    {
        public bool IsModified { get; private set; }
        public bool SawJournalActiveBeforeApply { get; private set; }
        public Func<RuntimeRecoveryJournal?>? JournalSnapshotReader { get; set; }

        public bool IsColorEffectModified() => IsModified;

        public bool ApplyColorMatrix(float[] matrix25)
        {
            IsModified = !ZenDisplayEffectAdapter.IsIdentityMatrix(matrix25);
            SawJournalActiveBeforeApply = JournalSnapshotReader?.Invoke()?.ZenDisplayEffectActive ?? true;
            return true;
        }

        public bool ResetColorMatrix()
        {
            IsModified = false;
            return true;
        }
    }
}
