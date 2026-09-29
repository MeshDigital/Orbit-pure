using System;
using System.Diagnostics;
using System.IO;
using SLSKDONET.Services.Audio;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>
/// Files Windows Media Foundation can't decode (some FLAC, Opus, Ogg — 123 of the real library)
/// must still play: PlayableAudio falls back to an ffmpeg-decoded WAV.
/// </summary>
public class PlayableAudioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"playable-{Guid.NewGuid():N}");

    public PlayableAudioTests()
    {
        Directory.CreateDirectory(_dir);
        PlayableAudio.CacheDirectory = Path.Combine(_dir, "cache");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* reader handles close asynchronously */ }
    }

    [Fact]
    public void OpusFile_PlaysThroughAnFfmpegDecodedWav()
    {
        var opus = Path.Combine(_dir, "tone.opus");
        if (!TryFfmpeg($"-v error -y -f lavfi -i sine=frequency=440:duration=3 -c:a libopus \"{opus}\"")) return; // no ffmpeg here

        using (var reader = PlayableAudio.Open(opus, out var playable))
        {
            Assert.EndsWith(".wav", playable);
            Assert.InRange(reader.TotalTime.TotalSeconds, 2.9, 3.1);
        }

        // Second open reuses the cached copy.
        using (var again = PlayableAudio.Open(opus, out var playable2))
            Assert.Single(Directory.GetFiles(PlayableAudio.CacheDirectory, "*.wav"));
    }

    [Fact]
    public void DecodableFile_IsOpenedDirectly()
    {
        var wav = Path.Combine(_dir, "plain.wav");
        using (var w = new NAudio.Wave.WaveFileWriter(wav, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(8000, 1)))
            for (int i = 0; i < 8000; i++) w.WriteSample(0f);

        using var reader = PlayableAudio.Open(wav, out var playable);
        Assert.Equal(wav, playable);
        Assert.False(Directory.Exists(PlayableAudio.CacheDirectory) && Directory.GetFiles(PlayableAudio.CacheDirectory).Length > 0);
    }

    private static bool TryFfmpeg(string args)
    {
        try
        {
            var ffmpeg = SLSKDONET.Services.AudioAnalysis.AudioIngestionPipeline.ResolveFfmpegPath();
            using var p = Process.Start(new ProcessStartInfo(ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
            if (p == null) return false;
            p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
