using System;
using System.Globalization;
using System.Threading;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

public class IntegratedLoudnessMeterTests
{
    private static float[] Sine(double frequency, double amplitude, int sampleRate, double seconds, int channels)
    {
        int frames = (int)(sampleRate * seconds);
        var buffer = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
            for (int c = 0; c < channels; c++) buffer[i * channels + c] = v;
        }
        return buffer;
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void FullScaleSine_InOneChannel_ReadsMinus3Lufs(int sampleRate)
    {
        // BS.1770-4: a 0 dBFS 1 kHz sine in one channel reads −3.01 LKFS.
        var lufs = IntegratedLoudnessMeter.MeasureIntegratedLufs(Sine(1000, 1.0, sampleRate, 5, 1), sampleRate, 1);
        Assert.NotNull(lufs);
        Assert.InRange(lufs!.Value, -3.11, -2.91);
    }

    [Fact]
    public void StereoSine_AddsBothChannels_AndScalesWithLevel()
    {
        // Same sine in L and R: +3 dB over one channel. −20 dBFS: 20 dB quieter.
        var full = IntegratedLoudnessMeter.MeasureIntegratedLufs(Sine(1000, 1.0, 44100, 5, 2), 44100, 2);
        var quiet = IntegratedLoudnessMeter.MeasureIntegratedLufs(Sine(1000, 0.1, 44100, 5, 2), 44100, 2);
        Assert.InRange(full!.Value, -0.1, 0.1);
        Assert.InRange(quiet!.Value, -20.1, -19.9);
    }

    [Fact]
    public void Silence_IsGatedOut()
    {
        // Half silence, half −20 dBFS tone: the silent blocks fall under the −70 LUFS gate, so the
        // result is the tone's loudness, not an average dragged down by silence.
        var tone = Sine(1000, 0.1, 44100, 4, 1);
        var buffer = new float[tone.Length * 2];
        tone.CopyTo(buffer, tone.Length);
        var lufs = IntegratedLoudnessMeter.MeasureIntegratedLufs(buffer, 44100, 1);
        Assert.InRange(lufs!.Value, -23.2, -22.8);

        Assert.Null(IntegratedLoudnessMeter.MeasureIntegratedLufs(new float[44100 * 2], 44100, 2));
        Assert.Null(IntegratedLoudnessMeter.MeasureIntegratedLufs(new float[100], 44100, 1)); // under one 400 ms block
    }

    [Fact]
    public void Accumulator_FedInOddChunks_MatchesTheOneShotMeasurementExactly()
    {
        // The backfill streams FFmpeg output in arbitrary chunks (one may end mid-frame); the result
        // must be identical to measuring the whole buffer at once, as full analysis does.
        var buffer = Sine(440, 0.3, 44100, 6, 2);
        for (int i = 0; i < buffer.Length; i += 7) buffer[i] *= 0.5f; // not a pure tone
        var oneShot = IntegratedLoudnessMeter.MeasureIntegratedLufs(buffer, 44100, 2);

        var accumulator = new IntegratedLoudnessMeter.Accumulator(44100, 2);
        int offset = 0, size = 1;
        while (offset < buffer.Length)
        {
            int n = Math.Min(size, buffer.Length - offset);
            accumulator.Add(buffer.AsSpan(offset, n));
            offset += n;
            size = size * 3 + 1; // 1, 4, 13, 40, ... odd sizes split frames
        }

        Assert.NotNull(oneShot);
        Assert.Equal(oneShot, accumulator.GetIntegratedLufs());
        Assert.Equal(60, accumulator.SegmentCount); // 6 s of 100 ms segments
    }

    [Theory]
    [InlineData(0.8, null)]       // legacy Essentia average_loudness (0–1), not LUFS
    [InlineData(0.0, null)]       // the NOT NULL column's default = never measured
    [InlineData(-90.0, null)]     // below the absolute gate
    [InlineData(-9.4, -9.4)]
    public void AsLufs_RejectsValuesThatCannotBeIntegratedLoudness(double stored, double? expected)
    {
        Assert.Equal(expected, IntegratedLoudnessMeter.AsLufs(stored));
    }

    [Fact]
    public void Format_UsesAnInvariantDecimalPoint_AndADashForUnknown()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("nl-NL"); // the owner's culture: "0,8"
            Assert.Equal("-9.4 LUFS", IntegratedLoudnessMeter.Format(-9.42));
            Assert.Equal("—", IntegratedLoudnessMeter.Format(0.8));
            Assert.Equal("—", IntegratedLoudnessMeter.Format(null));

            var legacy = new PlaylistTrackViewModel(new PlaylistTrack { Loudness = 0.8, TruePeak = -0.5, DynamicRange = 7.5 });
            Assert.Equal("—", legacy.LoudnessDisplay);
            Assert.Equal("-0.5 dBTP", legacy.TruePeakDisplay);
            Assert.Equal("7.5 LU", legacy.DynamicRangeDisplay);

            var measured = new PlaylistTrackViewModel(new PlaylistTrack { Loudness = -8.4 });
            Assert.Equal("-8.4 LUFS", measured.LoudnessDisplay);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
