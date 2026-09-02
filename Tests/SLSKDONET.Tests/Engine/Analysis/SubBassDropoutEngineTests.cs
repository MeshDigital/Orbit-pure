using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Engine.Analysis;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

/// <summary>
/// SubBassDropoutEngine previously used a single 0-120 Hz low-pass, lumping true sub-bass
/// together with kick fundamental/punch — content that often stays present through a DnB
/// breakdown, diluting the dropout signal. These tests pin the fixed behavior: a genuine
/// 30-100 Hz bandpass that isolates a sub-bass tone while attenuating both sub-sonic rumble
/// and kick-punch-range content.
/// </summary>
public class SubBassDropoutEngineTests
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

    private static float Rms(float[] signal)
    {
        double sumSq = 0.0;
        foreach (var s in signal) sumSq += s * (double)s;
        return (float)Math.Sqrt(sumSq / signal.Length);
    }

    [Fact]
    public void ComputeSubBassEnergyCurve_IsolatesSubBassTone_FromRumbleAndKickPunch()
    {
        var engine = new SubBassDropoutEngine();

        // 15 Hz "DC rumble", 50 Hz "true sub-bass", 150 Hz "kick punch" — mixed together.
        double duration = 2.0;
        var rumble = SineWave(15, duration, 0.6f);
        var subBass = SineWave(50, duration, 0.6f);
        var kickPunch = SineWave(150, duration, 0.6f);
        var mixed = new float[rumble.Length];
        for (int i = 0; i < mixed.Length; i++)
            mixed[i] = rumble[i] + subBass[i] + kickPunch[i];

        var mixedCurve = engine.ComputeSubBassEnergyCurve(mixed, SampleRate);
        var subBassOnlyCurve = engine.ComputeSubBassEnergyCurve(subBass, SampleRate);

        Assert.NotEmpty(mixedCurve);
        Assert.NotEmpty(subBassOnlyCurve);

        // The bandpassed mixed signal should read close to the sub-bass-only signal's own
        // filtered level — i.e. the rumble/kick-punch components are substantially rejected,
        // not just "somewhat attenuated." Allow generous tolerance for filter ripple/settling.
        float mixedMean = mixedCurve.Average();
        float subBassOnlyMean = subBassOnlyCurve.Average();
        Assert.True(mixedMean < subBassOnlyMean * 1.6f,
            $"Expected mixed-signal energy ({mixedMean}) to stay close to the sub-bass-only reference ({subBassOnlyMean}), indicating rumble/kick-punch were rejected — bandpass isolation regressed.");
    }

    [Fact]
    public void ComputeSubBassEnergyCurve_RejectsOutOfBandTone()
    {
        var engine = new SubBassDropoutEngine();

        var inBand = SineWave(50, 2.0, 0.8f);   // inside 30-100 Hz
        var outOfBand = SineWave(300, 2.0, 0.8f); // well above the 100 Hz cutoff

        var inBandCurve = engine.ComputeSubBassEnergyCurve(inBand, SampleRate);
        var outOfBandCurve = engine.ComputeSubBassEnergyCurve(outOfBand, SampleRate);

        Assert.True(inBandCurve.Average() > outOfBandCurve.Average() * 3,
            "A 50 Hz tone (in-band) should read far stronger than a 300 Hz tone (well outside the 30-100 Hz passband) after bandpass filtering.");
    }

    [Fact]
    public void FindStrongestBeatIndex_PicksCandidateWithRealSubBassEnergy()
    {
        // 4 one-second slots: only slot 2 has real 50 Hz sub-bass energy, the others are silent
        // or carry only a high-frequency click — simulating "only one of the first few beat
        // ticks is near the true kick/sub-bass hit."
        var silence = new float[SampleRate];
        var highFreqClick = SineWave(2000, 1.0, 0.5f);
        var subBassHit = SineWave(50, 1.0, 0.9f);

        var signal = silence.Concat(highFreqClick).Concat(subBassHit).Concat(silence).ToArray();
        var beatTimestamps = new List<double> { 0.5, 1.5, 2.5, 3.5 };

        int strongest = SubBassDropoutEngine.FindStrongestBeatIndex(signal, SampleRate, beatTimestamps, candidateCount: 4);

        Assert.Equal(2, strongest);
    }

    [Fact]
    public void FindStrongestBeatIndex_EmptyInputs_ReturnsZeroWithoutThrowing()
    {
        Assert.Equal(0, SubBassDropoutEngine.FindStrongestBeatIndex(Array.Empty<float>(), SampleRate, new List<double>()));
        Assert.Equal(0, SubBassDropoutEngine.FindStrongestBeatIndex(null!, SampleRate, null!));
    }
}
