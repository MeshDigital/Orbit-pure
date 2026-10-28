using System;
using System.IO;
using System.Linq;
using NAudio.Wave;
using SLSKDONET.Services.Audio;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

public class ExactSeekTests
{
    [Theory]
    [InlineData("track.flac", true)]
    [InlineData("track.FLAC", true)]
    [InlineData("track.m4a", true)]
    [InlineData("track.ogg", true)]
    [InlineData("track.mp3", false)]
    [InlineData("track.wav", false)]
    [InlineData("track.aiff", false)]
    public void KnowsWhichFormatsNeedDecodeForward(string file, bool expected) =>
        Assert.Equal(expected, ExactSeek.NeedsDecodeForward(file));

    [Fact]
    public void Wav_SeeksExactly_OnAFreshReader()
    {
        // A ramp: every sample's value encodes its own index, so the landing spot is readable.
        var path = Path.Combine(Path.GetTempPath(), $"exactseek-{Guid.NewGuid():N}.wav");
        try
        {
            const int Rate = 8000;
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1)))
                for (int i = 0; i < Rate * 10; i++) writer.WriteSample(i / (float)(Rate * 10));

            using var reader = new AudioFileReader(path);
            ExactSeek.Seek(reader, path, 6.0);
            var block = new float[4];
            reader.Read(block, 0, block.Length);

            Assert.Equal(6 * Rate / (float)(Rate * 10), block[0], 4);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The real bug: FLAC through Media Foundation. Opt-in (ORBIT_LIVE_AUDIO_DIR = a folder of
    /// FLAC files) because it needs real files. Checks ExactSeek lands on exactly the audio a
    /// straight decode reaches at 60 s — where a plain CurrentTime seek plays from 0:00.
    /// </summary>
    [Fact]
    public void Flac_SeeksExactly_WhereCurrentTimeFails()
    {
        var dir = Environment.GetEnvironmentVariable("ORBIT_LIVE_AUDIO_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        foreach (var path in Directory.GetFiles(dir, "*.flac").Take(3))
        {
            var reference = Decode(path, r => Skip(r, 60));
            var exact = Decode(path, r => ExactSeek.Seek(r, path, 60));
            var naive = Decode(path, r => r.CurrentTime = TimeSpan.FromSeconds(60));
            var start = Decode(path, _ => { });

            Assert.Equal(reference, exact);
            Assert.Equal(start, naive); // documents the Media Foundation behaviour ExactSeek works around
        }
    }

    private static float[] Decode(string path, Action<AudioFileReader> position)
    {
        using var r = new AudioFileReader(path);
        position(r);
        var block = new float[2048];
        r.Read(block, 0, block.Length);
        return block;
    }

    private static void Skip(AudioFileReader r, double seconds)
    {
        long remaining = (long)(seconds * r.WaveFormat.SampleRate) * r.WaveFormat.Channels;
        var buf = new float[65536];
        while (remaining > 0) { int n = r.Read(buf, 0, (int)Math.Min(buf.Length, remaining)); if (n == 0) break; remaining -= n; }
    }
}
