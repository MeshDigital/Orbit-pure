using System;
using System.Linq;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

public class MusicStartDetectorTests
{
    private const int Sr = 44100;

    private static float[] Silence(int seconds) => new float[seconds * Sr];

    private static float[] Sine(int seconds, double freq, double amplitude)
    {
        var buf = new float[seconds * Sr];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = (float)(amplitude * Math.Sin(2 * Math.PI * freq * i / Sr));
        return buf;
    }

    private static float[] Concat(params float[][] arrays) => arrays.SelectMany(a => a).ToArray();

    [Fact]
    public void TwoSecondsSilenceThenMusic_DetectsAboutTwoSeconds()
    {
        var pcm = Concat(Silence(2), Sine(1, 440.0, 0.5));

        double? start = MusicStartDetector.Detect(pcm, Sr, thresholdDbFs: -50.0);

        Assert.NotNull(start);
        Assert.InRange(start!.Value, 1.98, 2.02); // ±20 ms
    }

    [Fact]
    public void MinusSixtyDbHiss_IsIgnored()
    {
        var rng = new Random(0);
        var hiss = new float[3 * Sr];
        double amplitude = Math.Pow(10, -60.0 / 20.0); // -60 dBFS
        for (int i = 0; i < hiss.Length; i++)
            hiss[i] = (float)(amplitude * (rng.NextDouble() * 2 - 1));

        Assert.Null(MusicStartDetector.Detect(hiss, Sr, thresholdDbFs: -50.0));
    }

    [Fact]
    public void PadsBeforeKick_DetectBeforeTheKick()
    {
        // A quiet pad from 0s is "music" and should be detected well before a loud kick at 3s.
        var pcm = Concat(Sine(3, 220.0, 0.05), Sine(1, 440.0, 1.0));

        double? start = MusicStartDetector.Detect(pcm, Sr, thresholdDbFs: -50.0);

        Assert.NotNull(start);
        Assert.True(start!.Value < 0.5, $"expected an early music start, got {start:F3}s");
    }

    [Fact]
    public void SilentBuffer_ReturnsNull()
    {
        Assert.Null(MusicStartDetector.Detect(Silence(2), Sr, thresholdDbFs: -50.0));
    }

    [Fact]
    public void ThresholdDbFs_TracksIntegratedLoudness()
    {
        Assert.Equal(-50.0, MusicStartDetector.ThresholdDbFs(null), 6);          // no loudness -> floor
        Assert.Equal(-50.0, MusicStartDetector.ThresholdDbFs(-20.0), 6);         // max(-50, -55)
        Assert.Equal(-43.0, MusicStartDetector.ThresholdDbFs(-8.0), 6);          // max(-50, -43)
    }
}

public class MusicStartIntroAnchorTests
{
    [Fact]
    public void MusicStartMoreThanOneBeatBeforeBarOne_AnchorsOnMusicStart()
    {
        // Bar 1 at 2.0 s, music starts at 0.5 s (well over one 0.5s beat before).
        Assert.Equal(0.5, MusicStartIntroAnchor.Resolve(0.5, 2.0, 120.0), 6);
    }

    [Fact]
    public void MusicStartWithinOneBeatOfBarOne_KeepsBarOne()
    {
        // Music start 0.4 s before Bar 1 at 120 BPM (0.5s beat) is within one beat.
        Assert.Equal(2.0, MusicStartIntroAnchor.Resolve(1.6, 2.0, 120.0), 6);
    }

    [Fact]
    public void NoMusicStart_KeepsBarOne()
    {
        Assert.Equal(2.0, MusicStartIntroAnchor.Resolve(null, 2.0, 120.0), 6);
    }
}
