using System;
using System.IO;
using NAudio.Wave;
using SLSKDONET.Services.Audio;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>#456: IAudioDecoder cross-platform routing, WAV playback, ExactSeek, and mono PCM decode.</summary>
public sealed class AudioDecoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orbit-decoder-test-" + Guid.NewGuid().ToString("N"));

    public AudioDecoderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* intentional: best-effort test cleanup */ }
    }

    private string CreateSineWav(string name, double seconds = 2.0, int sampleRate = 44100)
    {
        var path = Path.Combine(_dir, name);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
        int frames = (int)(seconds * sampleRate);
        var buf = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float s = MathF.Sin(2f * MathF.PI * 440f * i / sampleRate) * 0.5f;
            buf[i * 2] = s;
            buf[i * 2 + 1] = s;
        }
        writer.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    [Theory]
    [InlineData("track.flac", true)]
    [InlineData("track.m4a", true)]
    [InlineData("track.aiff", false)]
    [InlineData("track.mp3", true)]
    [InlineData("track.ogg", true)]
    [InlineData("track.opus", true)]
    [InlineData("track.wav", false)]
    public void NonWindowsDecoder_RequiresFfmpegForNonWavFormats(string file, bool expected)
    {
        var decoder = new CrossPlatformAudioDecoder(isWindows: false);
        Assert.Equal("FFmpeg", decoder.BackendName);
        Assert.Equal(expected, decoder.RequiresFfmpegForPlayback(file));
    }

    [Fact]
    public void NonWindowsDecoder_RoutesFlacThroughFfmpegCopyAndSeeksAccurately()
    {
        var wav = CreateSineWav("decoded.wav", seconds: 2.0);
        var fakeFlac = Path.Combine(_dir, "song.flac");
        File.WriteAllBytes(fakeFlac, [0x66, 0x4C, 0x61, 0x43]); // "fLaC" stub

        string? requestedFfmpegInput = null;
        var decoder = new CrossPlatformAudioDecoder(
            isWindows: false,
            ensureDecodedWav: p => { requestedFfmpegInput = p; return wav; });

        using var reader = decoder.OpenForPlayback(fakeFlac, out var playablePath);
        Assert.Equal(fakeFlac, requestedFfmpegInput);
        Assert.Equal(wav, playablePath);

        decoder.SeekExact(reader, playablePath, 1.0);
        Assert.InRange(reader.CurrentTime.TotalSeconds, 0.99, 1.01);
    }

    [Fact]
    public void DecodeMonoSamples_ReadsManagedWavDirectlyOnAnyOs()
    {
        var wav = CreateSineWav("mono-test.wav", seconds: 1.0, sampleRate: 44100);
        var decoder = new CrossPlatformAudioDecoder(isWindows: false);

        var (samples, sr) = decoder.DecodeMonoSamples(wav, maxSeconds: 2);
        Assert.Equal(44100, sr);
        Assert.Equal(44100, samples.Length);
    }
}
