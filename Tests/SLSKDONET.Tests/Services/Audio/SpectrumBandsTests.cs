using System;
using System.Linq;
using SLSKDONET.Services.Audio;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

public class SpectrumBandsTests
{
    private const int FftSize = 2048;
    private const double SampleRate = 44100;

    /// <summary>Magnitudes (unscaled, Hann-windowed) of a full-scale sine at <paramref name="hz"/>.</summary>
    private static float[] Tone(double hz, double amplitude = 1.0)
    {
        var mags = new float[FftSize / 2];
        int bin = (int)Math.Round(hz / (SampleRate / FftSize));
        mags[bin] = (float)(amplitude * FftSize / 4.0);
        return mags;
    }

    private static SpectrumBands Settle(float[] mags, int frames = 120)
    {
        var bands = new SpectrumBands(64, SampleRate);
        for (int i = 0; i < frames; i++) bands.Push(mags, 1 / 60.0);
        return bands;
    }

    [Fact]
    public void BassTone_LightsUpLowBands_NotHighOnes()
    {
        var bands = Settle(Tone(60));
        int loudest = Array.IndexOf(bands.Levels, bands.Levels.Max());

        Assert.InRange(bands.BandCentre(loudest), 40, 90);
        Assert.True(bands.Bass > bands.Treble + 0.3f, $"bass {bands.Bass} treble {bands.Treble}");
    }

    [Fact]
    public void LogSpacing_GivesBassAsManyBandsAsTreble()
    {
        var bands = new SpectrumBands(64, SampleRate);
        // Half the bands sit below ~700 Hz (geometric middle of 30 Hz–16 kHz), as hearing does.
        Assert.InRange(bands.BandCentre(31), 550, 800);
    }

    [Fact]
    public void AutoGain_QuietAndLoudTonesReachSimilarHeight()
    {
        var loud = Settle(Tone(1000, 1.0), 400);
        var quiet = Settle(Tone(1000, 0.05), 400); // -26 dB

        Assert.InRange(loud.Levels.Max(), 0.8f, 1f);
        Assert.InRange(quiet.Levels.Max(), 0.75f, 1f);
    }

    [Fact]
    public void Silence_DecaysToZero_AndPeaksFallAfterHolding()
    {
        var bands = Settle(Tone(1000));
        float peak = bands.Peaks.Max();
        for (int i = 0; i < 240; i++) bands.Decay(1 / 60.0);

        Assert.True(bands.Levels.Max() < 0.02f);
        Assert.True(bands.Peaks.Max() < peak);
    }

    [Fact]
    public void GarbageInput_DoesNotThrowOrProduceNaN()
    {
        var bands = new SpectrumBands(32, SampleRate);
        bands.Push(null, 1 / 60.0);
        bands.Push(new float[4], 1 / 60.0);
        bands.Push(new float[FftSize / 2], 1 / 60.0);
        Assert.All(bands.Levels, v => Assert.True(float.IsFinite(v) && v >= 0 && v <= 1));
    }
}
