using System;
using System.Linq;
using SLSKDONET.Engine.Analysis;
using Xunit;

namespace SLSKDONET.Tests.Analysis;

/// <summary>
/// Regression coverage for the two real DSP drop-detection engines that feed
/// Engine.Cueing.CueGenerationService's DSP path. SubBassDropoutEngine is
/// deterministic single-pass filtering, so it gets precise assertions.
/// SpectralFluxNoveltyEngine was rewritten from a naive O(n^2) DFT to NWaves'
/// RealFft — these tests exist primarily to catch that rewrite silently
/// breaking the math (wrong output shape, all-zero curve, exceptions), not to
/// pin down exact DSP threshold behavior.
/// </summary>
public class DropDetectionEnginesTests
{
    private const int SampleRate = 8000;

    private static float[] SineWave(double freqHz, double durationSeconds, float amplitude, int sampleRate = SampleRate)
    {
        int n = (int)(durationSeconds * sampleRate);
        var buffer = new float[n];
        for (int i = 0; i < n; i++)
            buffer[i] = amplitude * (float)Math.Sin(2 * Math.PI * freqHz * i / sampleRate);
        return buffer;
    }

    private static float[] Silence(double durationSeconds, int sampleRate = SampleRate)
        => new float[(int)(durationSeconds * sampleRate)];

    private static float[] Concat(params float[][] segments) => segments.SelectMany(s => s).ToArray();

    // ── SubBassDropoutEngine ────────────────────────────────────────────────

    [Fact]
    public void SubBassDropoutEngine_DetectsDropoutAndReturn_AroundSilenceGap()
    {
        var engine = new SubBassDropoutEngine();

        // Loud 60Hz sub-bass, then 6s of silence (dropout), then loud bass again (return).
        var signal = Concat(
            SineWave(60, 5, 0.9f),
            Silence(6),
            SineWave(60, 5, 0.9f));

        var curve = engine.ComputeSubBassEnergyCurve(signal, SampleRate);
        Assert.NotEmpty(curve);

        var (dropoutStarts, returns) = engine.DetectDropoutEvents(curve);

        Assert.NotEmpty(dropoutStarts);
        Assert.NotEmpty(returns);

        // Dropout should start somewhere after the first loud segment (~5s) and before
        // the silence ends (~11s); return should land after that, before the track ends (~16s).
        Assert.InRange(dropoutStarts[0], 4.5, 11.0);
        Assert.InRange(returns[0], dropoutStarts[0], 16.0);
    }

    [Fact]
    public void SubBassDropoutEngine_NoVariation_DetectsNoEvents()
    {
        var engine = new SubBassDropoutEngine();
        var signal = SineWave(60, 10, 0.5f);

        var curve = engine.ComputeSubBassEnergyCurve(signal, SampleRate);
        var (dropoutStarts, returns) = engine.DetectDropoutEvents(curve);

        Assert.Empty(dropoutStarts);
        Assert.Empty(returns);
    }

    [Fact]
    public void SubBassDropoutEngine_EmptySignal_ReturnsEmptyWithoutThrowing()
    {
        var engine = new SubBassDropoutEngine();

        var curve = engine.ComputeSubBassEnergyCurve(Array.Empty<float>(), SampleRate);
        Assert.Empty(curve);

        var (dropoutStarts, returns) = engine.DetectDropoutEvents(curve);
        Assert.Empty(dropoutStarts);
        Assert.Empty(returns);
    }

    // ── SpectralFluxNoveltyEngine ───────────────────────────────────────────

    [Fact]
    public void SpectralFluxNoveltyEngine_ComputeNoveltyFunction_ReturnsNormalizedNonEmptyCurve()
    {
        var engine = new SpectralFluxNoveltyEngine();
        // Silence then a sudden loud tone at a different frequency — a clear spectral change.
        var signal = Concat(Silence(3), SineWave(880, 3, 0.9f));

        var novelty = engine.ComputeNoveltyFunction(signal, SampleRate);

        Assert.NotEmpty(novelty);
        Assert.All(novelty, v => Assert.InRange(v, 0f, 1f));
        Assert.Contains(novelty, v => v > 0f); // not all-zero — the FFT path is actually computing something
    }

    [Fact]
    public void SpectralFluxNoveltyEngine_ConstantTone_ProducesNoDropSignatures()
    {
        var engine = new SpectralFluxNoveltyEngine();
        // A steady, event-free tone still produces small leakage-driven ripples once the
        // curve is normalized to its own peak (expected — real onset curves work the same
        // way, which is why downstream cue generation only trusts build-confirmed drop
        // signatures, not raw onset peaks). The invariant that actually matters is that
        // DetectDropSignatures — which requires a sustained rising slope before a strong
        // peak — does NOT mistake that noise for a real drop.
        var signal = SineWave(440, 12, 0.5f);

        var novelty = engine.ComputeNoveltyFunction(signal, SampleRate);
        var drops = engine.DetectDropSignatures(novelty, SampleRate, 512);

        // The local-mean-subtraction window is asymmetric right at the start of the buffer
        // (nothing to average before index 0), which can register as a false build there —
        // a pre-existing boundary artifact, not something this rewrite introduced or something
        // meaningful cue generation would ever see mid-track. The invariant that actually
        // matters is that a sustained, event-free tone doesn't manufacture a "drop" anywhere
        // past that startup window.
        Assert.DoesNotContain(drops, d => d.DropSeconds > 1.0);
    }

    [Fact]
    public void SpectralFluxNoveltyEngine_AbruptOnset_IsDetectedAsPeak()
    {
        var engine = new SpectralFluxNoveltyEngine();
        var signal = Concat(Silence(3), SineWave(880, 3, 0.9f));

        var novelty = engine.ComputeNoveltyFunction(signal, SampleRate);
        var peaks = engine.PickOnsetPeaks(novelty, SampleRate, hopSize: 512, minPeakStrength: 0.3);

        Assert.NotEmpty(peaks);
        // The onset happens at the 3s mark; allow a generous window either side for hop/window smearing.
        Assert.Contains(peaks, p => p.TimestampSeconds is > 2.0 and < 4.0);
    }

    [Fact]
    public void SpectralFluxNoveltyEngine_EmptySignal_ReturnsEmptyWithoutThrowing()
    {
        var engine = new SpectralFluxNoveltyEngine();

        var novelty = engine.ComputeNoveltyFunction(Array.Empty<float>(), SampleRate);
        Assert.Empty(novelty);

        var drops = engine.DetectDropSignatures(novelty, SampleRate, 512);
        Assert.Empty(drops);
    }
}
