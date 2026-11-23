using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Models;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

/// <summary>#331: a missing FFmpeg skips only the decode check, says so once, and analysis can still decode.</summary>
public sealed class MissingFfmpegTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetDirectoryName(SLSKDONET.Data.OrbitPaths.LibraryDbPath)!, "noffmpeg-" + Guid.NewGuid().ToString("N"));
    private const string NoFfmpeg = "orbit-missing-ffmpeg-for-test.exe";

    public MissingFfmpegTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* intentional: best-effort cleanup of the isolated test folder */ }
    }

    private string SineWav(int sampleRate, int channels, double seconds)
    {
        var path = Path.Combine(_dir, $"sine-{sampleRate}-{channels}.wav");
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
        var frames = (int)(sampleRate * seconds);
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            for (var c = 0; c < channels; c++) writer.WriteSample(v);
        }
        return path;
    }

    [Fact]
    public async Task Scanner_SkipsTheDecodeCheck_AndNotifiesOnce()
    {
        using var bus = new EventBusService();
        var notifications = new List<NotificationEvent>();
        using var sub = bus.GetEvent<NotificationEvent>().Subscribe(notifications.Add);
        var scanner = new AudioCorruptionScannerService(
            new AudioIngestionPipeline(NullLogger<AudioIngestionPipeline>.Instance, NoFfmpeg),
            NullLogger<AudioCorruptionScannerService>.Instance, bus, ffmpegAvailable: () => false);
        var file = SineWav(44100, 2, 1);

        var (status, details) = await scanner.ScanAsync(file);
        await scanner.ScanAsync(file);

        Assert.Equal(CorruptionStatus.Unknown, status);                    // "not checked", not Fatal: analysis continues
        Assert.Equal(AudioCorruptionScannerService.FfmpegMissingDetails, details);
        var note = Assert.Single(notifications);                           // once per session, not per track
        Assert.Equal("Settings", note.OpenPage);
    }

    [Fact]
    public async Task Scanner_FfmpegThatCantStart_IsTreatedAsMissing()
    {
        var scanner = new AudioCorruptionScannerService(
            new AudioIngestionPipeline(NullLogger<AudioIngestionPipeline>.Instance, NoFfmpeg),
            NullLogger<AudioCorruptionScannerService>.Instance, eventBus: null, ffmpegAvailable: () => true);

        var (status, details) = await scanner.ScanAsync(SineWav(44100, 2, 1));
        Assert.Equal(CorruptionStatus.Unknown, status);
        Assert.Equal(AudioCorruptionScannerService.FfmpegMissingDetails, details);
    }

    [Fact]
    public async Task Decode_WithoutFfmpeg_ProducesThe44kStereoFloatContract()
    {
        var pipeline = new AudioIngestionPipeline(NullLogger<AudioIngestionPipeline>.Instance, NoFfmpeg);
        var wav = await pipeline.DecodeToTempWavAsync(new TrackAudioSource(SineWav(22050, 1, 2)));
        try
        {
            using var reader = new WaveFileReader(wav);
            Assert.Equal(AudioIngestionPipeline.TargetSampleRate, reader.WaveFormat.SampleRate);
            Assert.Equal(AudioIngestionPipeline.TargetChannels, reader.WaveFormat.Channels);
            Assert.Equal(WaveFormatEncoding.IeeeFloat, reader.WaveFormat.Encoding);
            Assert.InRange(reader.TotalTime.TotalSeconds, 1.9, 2.1);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task Decode_WithoutFfmpeg_AnUnreadableFile_SaysWhy()
    {
        var bogus = Path.Combine(_dir, "not-audio.flac");
        await File.WriteAllTextAsync(bogus, "this is not audio");
        var pipeline = new AudioIngestionPipeline(NullLogger<AudioIngestionPipeline>.Instance, NoFfmpeg);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.DecodeToTempWavAsync(new TrackAudioSource(bogus)));
        Assert.Contains("FFmpeg not found", error.Message);
        Assert.Contains("Settings", error.Message);
    }
}
