using System;
using System.Linq;
using SLSKDONET.Services.Audio;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>
/// Regression coverage for a real, live-verified bug: the Mix editor's "Wave" and "Melt" presets
/// (WaveDuck/EchoOut) had no case in TransitionEngine.CalculateAutomation, so both fell through to
/// the same plain-crossfade math as "Fade"/"Auto" — the waveform overlay curve AND the actual live
/// playback gain (AudioPlayerService.AdvanceCrossfade) were indistinguishable across presets,
/// exactly matching the report "the buttons dont all work ... there is no difference."
/// </summary>
public class TransitionEngineAutomationTests
{
    private const int SamplePoints = 1000;

    private static TransitionRegion MakeRegion(TransitionType type) => new()
    {
        OutgoingTrackId = "A",
        IncomingTrackId = "B",
        StartSample = 0,
        EndSample = SamplePoints,
        Type = type,
        Curve = TransitionCurve.SCurve,
    };

    [Fact]
    public void WaveDuck_ProducesRhythmicDips_DistinctFromCrossfade()
    {
        var engine = new TransitionEngine();
        var crossfadeRegion = MakeRegion(TransitionType.Crossfade);
        var waveRegion = MakeRegion(TransitionType.WaveDuck);

        var crossfadeCurve = Enumerable.Range(0, SamplePoints)
            .Select(i => engine.CalculateAutomation(crossfadeRegion, i).OutgoingGain).ToArray();
        var waveCurve = Enumerable.Range(0, SamplePoints)
            .Select(i => engine.CalculateAutomation(waveRegion, i).OutgoingGain).ToArray();

        // Not just a scaled/shifted copy of the crossfade curve — meaningfully different shape.
        double maxAbsDiff = crossfadeCurve.Zip(waveCurve, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(maxAbsDiff > 0.05, $"WaveDuck curve should visibly diverge from Crossfade; max diff was {maxAbsDiff}");

        // The duck pulses should produce more direction changes (local dips) than a smooth taper.
        int waveDirectionChanges = CountDirectionChanges(waveCurve);
        int crossfadeDirectionChanges = CountDirectionChanges(crossfadeCurve);
        Assert.True(waveDirectionChanges > crossfadeDirectionChanges,
            $"WaveDuck should show more rhythmic dips ({waveDirectionChanges}) than the smooth Crossfade taper ({crossfadeDirectionChanges}).");
    }

    [Fact]
    public void EchoOut_ProducesScallopedDecay_DistinctFromCrossfade()
    {
        var engine = new TransitionEngine();
        var crossfadeRegion = MakeRegion(TransitionType.Crossfade);
        var echoRegion = MakeRegion(TransitionType.EchoOut);

        var crossfadeCurve = Enumerable.Range(0, SamplePoints)
            .Select(i => engine.CalculateAutomation(crossfadeRegion, i).OutgoingGain).ToArray();
        var echoCurve = Enumerable.Range(0, SamplePoints)
            .Select(i => engine.CalculateAutomation(echoRegion, i).OutgoingGain).ToArray();

        double maxAbsDiff = crossfadeCurve.Zip(echoCurve, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(maxAbsDiff > 0.05, $"EchoOut curve should visibly diverge from Crossfade; max diff was {maxAbsDiff}");

        int echoDirectionChanges = CountDirectionChanges(echoCurve);
        int crossfadeDirectionChanges = CountDirectionChanges(crossfadeCurve);
        Assert.True(echoDirectionChanges > crossfadeDirectionChanges,
            $"EchoOut's tap dips should show more direction changes ({echoDirectionChanges}) than the smooth Crossfade taper ({crossfadeDirectionChanges}).");
    }

    [Fact]
    public void WaveDuck_And_EchoOut_AreDistinctFromEachOther()
    {
        var engine = new TransitionEngine();
        var waveRegion = MakeRegion(TransitionType.WaveDuck);
        var echoRegion = MakeRegion(TransitionType.EchoOut);

        var waveCurve = Enumerable.Range(0, SamplePoints)
            .Select(i => engine.CalculateAutomation(waveRegion, i).OutgoingGain).ToArray();
        var echoCurve = Enumerable.Range(0, SamplePoints)
            .Select(i => engine.CalculateAutomation(echoRegion, i).OutgoingGain).ToArray();

        double maxAbsDiff = waveCurve.Zip(echoCurve, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(maxAbsDiff > 0.05, $"WaveDuck and EchoOut should not collapse to the same curve; max diff was {maxAbsDiff}");
    }

    [Theory]
    [InlineData(TransitionType.WaveDuck)]
    [InlineData(TransitionType.EchoOut)]
    public void NewCurves_StayWithinSafeGainBounds(TransitionType type)
    {
        var engine = new TransitionEngine();
        var region = MakeRegion(type);

        for (int i = 0; i <= SamplePoints; i += 10)
        {
            var automation = engine.CalculateAutomation(region, i);
            Assert.InRange(automation.OutgoingGain, 0.0f, 1.0f);
            Assert.InRange(automation.IncomingGain, 0.0f, 1.0f);
        }
    }

    [Theory]
    [InlineData(TransitionType.WaveDuck)]
    [InlineData(TransitionType.EchoOut)]
    public void NewCurves_TrendFromOutgoingTowardIncoming(TransitionType type)
    {
        // Rhythmic pulses mean the instantaneous gain at the exact final sample can itself be
        // mid-dip (matching the real DSP providers, which duck/tap right up to the boundary) —
        // so assert the overall trend across the window, not the literal endpoint value.
        var engine = new TransitionEngine();
        var region = MakeRegion(type);

        double startWindowAvgOut = Average(engine, region, 0, 50, a => a.OutgoingGain);
        double endWindowAvgOut = Average(engine, region, SamplePoints - 50, SamplePoints, a => a.OutgoingGain);
        double startWindowAvgIn = Average(engine, region, 0, 50, a => a.IncomingGain);
        double endWindowAvgIn = Average(engine, region, SamplePoints - 50, SamplePoints, a => a.IncomingGain);

        Assert.True(endWindowAvgOut < startWindowAvgOut, "Outgoing gain should trend down toward the end of the transition.");
        Assert.True(endWindowAvgIn > startWindowAvgIn, "Incoming gain should trend up toward the end of the transition.");
    }

    private static double Average(TransitionEngine engine, TransitionRegion region, int fromInclusive, int toExclusive, Func<TransitionAutomation, float> select)
    {
        double sum = 0;
        int count = 0;
        for (int i = fromInclusive; i < toExclusive; i++)
        {
            sum += select(engine.CalculateAutomation(region, i));
            count++;
        }
        return sum / count;
    }

    private static int CountDirectionChanges(float[] curve)
    {
        int changes = 0;
        int lastSign = 0;
        for (int i = 1; i < curve.Length; i++)
        {
            double delta = curve[i] - curve[i - 1];
            int sign = Math.Sign(delta);
            if (sign != 0 && lastSign != 0 && sign != lastSign) changes++;
            if (sign != 0) lastSign = sign;
        }
        return changes;
    }
}
