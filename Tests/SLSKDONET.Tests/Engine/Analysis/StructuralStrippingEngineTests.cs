using System;
using SLSKDONET.Engine.Analysis;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

/// <summary>
/// Coverage for the core differentiator StructuralStrippingEngine exists to provide: telling a
/// genuine kick-absent breakdown apart from a merely-quiet section that still has a periodic
/// kick — something a pure energy-level check (SubBassDropoutEngine) cannot do.
/// </summary>
public class StructuralStrippingEngineTests
{
    private const int SampleRate = 8000;

    /// <summary>
    /// Builds a mono PCM signal from a sequence of regions. Each region is `durationSeconds`
    /// long; if `kickAmplitude` is non-null, an 80 Hz sine burst (100ms on, comfortably inside
    /// the engine's 250 Hz low-pass) is placed once per second at that amplitude — silence
    /// otherwise, so consecutive 0.5s analysis windows alternate loud/quiet and produce real
    /// window-to-window novelty for the onset-coverage check to detect. Null amplitude means
    /// true silence throughout the region (no periodic content at all).
    /// </summary>
    private static float[] BuildSignal(params (double DurationSeconds, float? KickAmplitude)[] regions)
    {
        int totalSamples = 0;
        foreach (var r in regions) totalSamples += (int)(r.DurationSeconds * SampleRate);
        var signal = new float[totalSamples];

        int offset = 0;
        foreach (var (durationSeconds, kickAmplitude) in regions)
        {
            int regionSamples = (int)(durationSeconds * SampleRate);
            if (kickAmplitude is { } amplitude)
            {
                int kickPeriodSamples = SampleRate; // one kick per second
                int kickLengthSamples = SampleRate / 10; // 100ms burst
                for (int i = 0; i < regionSamples; i++)
                {
                    int posInKick = i % kickPeriodSamples;
                    if (posInKick < kickLengthSamples)
                    {
                        double t = posInKick / (double)SampleRate;
                        signal[offset + i] = amplitude * (float)Math.Sin(2 * Math.PI * 80.0 * t);
                    }
                }
            }
            offset += regionSamples;
        }

        return signal;
    }

    [Fact]
    public void DetectStructuralStripping_GenuineKickAbsentBreakdown_DetectsStartAndReturn()
    {
        var signal = BuildSignal(
            (8.0, 1.0f),   // loud, kicking
            (10.0, null),  // true silence — a genuine breakdown
            (8.0, 1.0f));  // loud, kicking resumes

        var engine = new StructuralStrippingEngine();
        var (starts, returns) = engine.DetectStructuralStripping(signal, SampleRate);

        Assert.NotEmpty(starts);
        Assert.NotEmpty(returns);
        // The detected start should land within the true-silence region (roughly 8-18s).
        Assert.Contains(starts, t => t >= 7.0 && t <= 10.5);
        // The detected return should land at or after the silence region ends (~18s).
        Assert.Contains(returns, t => t >= 17.0 && t <= 20.0);
    }

    [Fact]
    public void DetectStructuralStripping_QuietButStillPeriodicKick_DoesNotFlagAsBreakdown()
    {
        var signal = BuildSignal(
            (8.0, 1.0f),    // loud, kicking
            (8.0, 0.15f),   // quiet — energy dips well below 25% of track mean...
            (8.0, 1.0f));   // ...but the kick never actually stopped, so this must NOT be flagged

        var engine = new StructuralStrippingEngine();
        var (starts, returns) = engine.DetectStructuralStripping(signal, SampleRate);

        Assert.Empty(starts);
        Assert.Empty(returns);
    }

    [Fact]
    public void DetectStructuralStripping_EmptySignal_ReturnsEmptyWithoutThrowing()
    {
        var engine = new StructuralStrippingEngine();

        var (starts, returns) = engine.DetectStructuralStripping(Array.Empty<float>(), SampleRate);

        Assert.Empty(starts);
        Assert.Empty(returns);
    }

    [Fact]
    public void DetectStructuralStripping_NullSignal_ReturnsEmptyWithoutThrowing()
    {
        var engine = new StructuralStrippingEngine();

        var (starts, returns) = engine.DetectStructuralStripping(null!, SampleRate);

        Assert.Empty(starts);
        Assert.Empty(returns);
    }

    [Fact]
    public void DetectStructuralStripping_ZeroSampleRate_ReturnsEmptyWithoutThrowing()
    {
        var engine = new StructuralStrippingEngine();
        var signal = BuildSignal((4.0, 1.0f));

        var (starts, returns) = engine.DetectStructuralStripping(signal, sampleRate: 0);

        Assert.Empty(starts);
        Assert.Empty(returns);
    }
}
