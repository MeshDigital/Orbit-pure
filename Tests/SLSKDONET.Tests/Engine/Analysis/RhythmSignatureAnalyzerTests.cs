using System;
using System.Diagnostics;
using SLSKDONET.Engine.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests.Engine.Analysis;

/// <summary>GA4 / #248: synthetic drum patterns → rhythm signature.</summary>
public class RhythmSignatureAnalyzerTests
{
    private const int Rate = 44100;
    private readonly ITestOutputHelper _output;

    public RhythmSignatureAnalyzerTests(ITestOutputHelper output) => _output = output;

    // Patterns: hit positions in beats within a 4-beat bar.
    private static readonly double[] FourKicks = { 0, 1, 2, 3 };
    private static readonly double[] ClapsTwoAndFour = { 1, 3 };
    private static readonly double[] TwoStepKicks = { 0, 2.5 };
    private static readonly double[] HalfTimeKick = { 0 };
    private static readonly double[] HalfTimeSnare = { 2 };

    public static TheoryData<string> Variants => new() { "plain", "kick-intro", "offset" };

    [Theory, MemberData(nameof(Variants))]
    public void FourOnTheFloor_128_WithClaps(string variant)
    {
        var sig = Analyze(DrumTrack.Build(128, FourKicks, ClapsTwoAndFour, variant), 128);
        Log("4x4 @128", variant, sig);
        Assert.True(sig.FourOnFloorScore > 0.8, $"FourOnFloor {sig.FourOnFloorScore:F2}");
        Assert.True(sig.BackbeatScore > 0.6, $"Backbeat {sig.BackbeatScore:F2}");
        Assert.True(sig.HalfTimeScore < 0.3, $"HalfTime {sig.HalfTimeScore:F2}");
        Assert.Equal(128, sig.TempoHigh, 1);
    }

    [Theory, MemberData(nameof(Variants))]
    public void TwoStepDnB_174(string variant)
    {
        var sig = Analyze(DrumTrack.Build(174, TwoStepKicks, ClapsTwoAndFour, variant), 174);
        Log("DnB 2-step @174", variant, sig);
        Assert.True(sig.FourOnFloorScore < 0.3, $"FourOnFloor {sig.FourOnFloorScore:F2}");
        Assert.True(sig.BackbeatScore > 0.7, $"Backbeat {sig.BackbeatScore:F2}");
        Assert.True(sig.BreakIrregularity > 0.5, $"BreakIrregularity {sig.BreakIrregularity:F2}");
    }

    [Theory, MemberData(nameof(Variants))]
    public void HalfTime_140(string variant)
    {
        var sig = Analyze(DrumTrack.Build(140, HalfTimeKick, HalfTimeSnare, variant), 140);
        Log("half-time @140", variant, sig);
        Assert.True(sig.HalfTimeScore > 0.7, $"HalfTime {sig.HalfTimeScore:F2}");
        Assert.True(sig.FourOnFloorScore < 0.3, $"FourOnFloor {sig.FourOnFloorScore:F2}");
        Assert.True(sig.BackbeatScore < 0.3, $"Backbeat {sig.BackbeatScore:F2}");
    }

    [Theory, MemberData(nameof(Variants))]
    public void Hardcore_FourOnTheFloor_170(string variant)
    {
        // Same tempo band as DnB: proves tempo alone isn't the signal.
        var sig = Analyze(DrumTrack.Build(170, FourKicks, ClapsTwoAndFour, variant), 170);
        Log("hardcore 4x4 @170", variant, sig);
        Assert.True(sig.FourOnFloorScore > 0.8, $"FourOnFloor {sig.FourOnFloorScore:F2}");
    }

    [Fact]
    public void BeatlessPad_IsUnreliable()
    {
        var sig = Analyze(DrumTrack.Pad(seconds: 180), 120);
        Log("pad", "plain", sig);
        Assert.True(sig.Reliability < 0.3, $"Reliability {sig.Reliability:F2}");
        Assert.True(sig.FourOnFloorScore < 0.3, $"no confident guess without onsets: {sig.FourOnFloorScore:F2}");
    }

    [Fact]
    public void ReportedTempoOctave_DoesNotChangeTheVerdict()
    {
        // The tracker may report 87 for a 174 track: the grid is folded back to 174.
        var pcm = DrumTrack.Build(174, TwoStepKicks, ClapsTwoAndFour, "plain");
        var atHalf = Analyze(pcm, 87);
        Assert.Equal(174, atHalf.TempoHigh, 1);
        Assert.Equal(87, atHalf.TempoLow, 1);
        Assert.True(atHalf.FourOnFloorScore < 0.3);
        Assert.True(atHalf.BackbeatScore > 0.7);
    }

    [Fact]
    public void BeatTicks_GiveThePeriod_WhenBpmIsMissing()
    {
        var ticks = new double[64];
        for (var i = 0; i < ticks.Length; i++) ticks[i] = 0.3 + i * 60.0 / 128;
        var sig = RhythmSignatureAnalyzer.Analyze(DrumTrack.Build(128, FourKicks, ClapsTwoAndFour, "plain"), Rate, bpm: 0, beatTicks: ticks);
        Assert.Equal(128, sig.TempoHigh, 1);
        Assert.True(sig.FourOnFloorScore > 0.8);
    }

    [Fact]
    public void NoTempo_OrTooShort_IsUnknown()
    {
        Assert.Equal(0, RhythmSignatureAnalyzer.Analyze(new float[Rate * 10], Rate, bpm: 0).Reliability);
        Assert.Equal(0, RhythmSignatureAnalyzer.Analyze(new float[Rate], Rate, bpm: 128).Reliability);
        Assert.Equal(0, RhythmSignatureAnalyzer.Analyze(new float[Rate * 60], Rate, bpm: 128).Reliability); // silence
    }

    [Theory]
    [InlineData(174, 87, 174)]
    [InlineData(87, 87, 174)]
    [InlineData(140, 70, 140)]
    [InlineData(70, 70, 140)]
    [InlineData(128, 64, 128)]
    [InlineData(256, 64, 128)]
    [InlineData(200, 50, 100)]
    public void FoldTempo_GivesBothOctaves(double bpm, double low, double high)
    {
        var (l, h) = RhythmSignatureAnalyzer.FoldTempo(bpm);
        Assert.Equal(low, l, 6);
        Assert.Equal(high, h, 6);
    }

    [Fact, Trait("Category", "Perf")]
    public void SixMinuteTrack_Timing()
    {
        var pcm = DrumTrack.Build(128, FourKicks, ClapsTwoAndFour, "plain", dropBars: 192);
        Analyze(pcm, 128); // warm-up (JIT)
        var sw = Stopwatch.StartNew();
        var sig = Analyze(pcm, 128);
        sw.Stop();
        _output.WriteLine($"{pcm.Length / (double)Rate:F0} s of audio analysed in {sw.ElapsedMilliseconds} ms ({sig.AnalyzedBars} bars)");
        Assert.True(sig.FourOnFloorScore > 0.8);
    }

    private RhythmFeatures? _last;

    private RhythmSignature Analyze(float[] pcm, double bpm)
    {
        var (signature, features) = RhythmSignatureAnalyzer.AnalyzeDetailed(pcm, Rate, bpm);
        _last = features;
        return signature;
    }

    private void Log(string name, string variant, RhythmSignature s) =>
        _output.WriteLine($"{name} [{variant}]: 4x4 {s.FourOnFloorScore:F2} · backbeat {s.BackbeatScore:F2} · half {s.HalfTimeScore:F2} · " +
                          $"break {s.BreakIrregularity:F2} · {s.TempoLow:F1}/{s.TempoHigh:F1} · bars {s.AnalyzedBars} · rel {s.Reliability:F2} · {_last}");

    /// <summary>Deterministic synthetic drums: kick, snare/clap, closed hats on 8ths, and a bass line in the drop.</summary>
    private static class DrumTrack
    {
        public static float[] Build(double bpm, double[] kicks, double[] snares, string variant, int dropBars = 48)
        {
            var beat = 60.0 / bpm;
            var bar = 4 * beat;
            var introBars = variant == "kick-intro" ? 32 : 0;
            var offset = variant == "offset" ? 0.37 * beat : 0.0; // downbeat not at 0
            var total = (int)((offset + (introBars + dropBars) * bar + 1) * Rate);
            var pcm = new float[total];
            var rng = new Random(42);

            for (var b = 0; b < introBars; b++)
                foreach (var k in FourKicks) Kick(pcm, offset + b * bar + k * beat, 0.6);

            var dropStart = offset + introBars * bar;
            for (var b = 0; b < dropBars; b++)
            {
                var t0 = dropStart + b * bar;
                foreach (var k in kicks) Kick(pcm, t0 + k * beat, 0.8);
                foreach (var s in snares) Snare(pcm, t0 + s * beat, 0.5, rng);
                for (var h = 0; h < 8; h++) Hat(pcm, t0 + h * beat / 2, 0.08, rng);
            }

            // Sustained bass through the drop: raises its RMS above the kick-only intro, makes no onsets.
            var from = (int)(dropStart * Rate);
            for (var i = from; i < pcm.Length; i++)
                pcm[i] += (float)(0.25 * Math.Sin(2 * Math.PI * 55 * (i - from) / Rate));
            return pcm;
        }

        public static float[] Pad(int seconds)
        {
            var pcm = new float[seconds * Rate];
            for (var i = 0; i < pcm.Length; i++)
            {
                var t = (double)i / Rate;
                var swell = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 0.05 * t);
                pcm[i] = (float)(0.2 * swell * (Math.Sin(2 * Math.PI * 220 * t) + Math.Sin(2 * Math.PI * 277.2 * t) + Math.Sin(2 * Math.PI * 329.6 * t)));
            }
            return pcm;
        }

        private static void Kick(float[] pcm, double at, double gain)
        {
            var start = (int)(at * Rate);
            double phase = 0;
            for (var i = 0; i < (int)(0.25 * Rate) && start + i < pcm.Length; i++)
            {
                var t = (double)i / Rate;
                var f = 45 + 75 * Math.Exp(-t / 0.03);
                phase += 2 * Math.PI * f / Rate;
                var attack = Math.Min(1.0, t / 0.003); // 3 ms attack: a click, not a hard step
                pcm[start + i] += (float)(gain * attack * Math.Exp(-t / 0.15) * Math.Sin(phase));
            }
        }

        private static void Snare(float[] pcm, double at, double gain, Random rng)
        {
            var start = (int)(at * Rate);
            double prev = 0;
            for (var i = 0; i < (int)(0.2 * Rate) && start + i < pcm.Length; i++)
            {
                var t = (double)i / Rate;
                var noise = rng.NextDouble() * 2 - 1;
                var bright = noise - prev; // first difference: tilts the noise up
                prev = noise;
                var body = Math.Sin(2 * Math.PI * 200 * t) * Math.Exp(-t / 0.05);
                pcm[start + i] += (float)(gain * (0.6 * bright * Math.Exp(-t / 0.08) + 0.5 * body));
            }
        }

        private static void Hat(float[] pcm, double at, double gain, Random rng)
        {
            var start = (int)(at * Rate);
            double p1 = 0, p2 = 0;
            for (var i = 0; i < (int)(0.04 * Rate) && start + i < pcm.Length; i++)
            {
                var n = rng.NextDouble() * 2 - 1;
                var hp = n - 2 * p1 + p2; // second difference: mostly > 6 kHz
                p2 = p1; p1 = n;
                pcm[start + i] += (float)(gain * hp * Math.Exp(-(double)i / Rate / 0.015));
            }
        }
    }
}
