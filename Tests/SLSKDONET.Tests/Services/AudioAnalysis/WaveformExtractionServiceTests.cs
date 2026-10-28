using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

public sealed class WaveformExtractionServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "orbit-waveform-tests", Guid.NewGuid().ToString("N"));

    public WaveformExtractionServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ExtractAsync_MapsLateDropToTheActualEndOfTheTrack()
    {
        const int sampleRate = 44100;
        const int durationSeconds = 8;
        var path = Path.Combine(_directory, "late-drop.wav");
        WriteStereoWav(path, sampleRate, durationSeconds, seconds =>
        {
            // A quiet bassline followed by a much louder drop in the final quarter.
            var amplitude = seconds >= 6.0 ? 0.8f : 0.05f;
            return (amplitude, 80.0);
        });

        var features = await ExtractAsync(path);
        var (low, _, _) = SplitBands(features);
        int dropStart = low.Length * 3 / 4;
        var quietMean = low.Take(dropStart).Average(v => v);
        var dropMean = low.Skip(dropStart).Average(v => v);

        Assert.True(dropMean > 100, $"The late drop was not represented (mean {dropMean:0.0}).");
        Assert.True(dropMean > quietMean * 2,
            $"Drop contrast was lost: quiet={quietMean:0.0}, drop={dropMean:0.0}.");
    }

    [Fact]
    public async Task ExtractAsync_UsesOneLoudnessScaleAcrossAllBands()
    {
        const int sampleRate = 44100;
        var path = Path.Combine(_directory, "band-balance.wav");
        WriteStereoWav(path, sampleRate, 4, _ => (0.8f, 80.0), highAmplitude: 0.05f);

        var features = await ExtractAsync(path);
        var (low, mid, high) = SplitBands(features);
        var lowMean = low.Average(v => v);
        var highMean = high.Average(v => v);

        Assert.True(lowMean > 150, $"Bass was not retained (mean {lowMean:0.0}).");
        Assert.True(highMean < lowMean * 0.55,
            $"Independent band normalization exaggerated quiet highs: low={lowMean:0.0}, high={highMean:0.0}.");
        Assert.True(mid.Average(v => v) < lowMean * 0.55, "Low-frequency content leaked into the mid band.");
    }

    [Fact]
    public async Task ExtractAsync_SilenceProducesZeroesWithoutAmplifyingNoise()
    {
        var path = Path.Combine(_directory, "silence.wav");
        WriteStereoWav(path, 44100, 2, _ => (0f, 80.0));

        var features = await ExtractAsync(path);

        Assert.All(features.WaveformBlob!, value => Assert.Equal(0, value));
    }

    private async Task<AudioFeaturesEntity> ExtractAsync(string path)
    {
        var target = new AudioFeaturesEntity { TrackUniqueHash = "test" };
        await new WaveformExtractionService(NullLogger<WaveformExtractionService>.Instance)
            .ExtractAsync(path, target);
        Assert.NotNull(target.WaveformBlob);
        Assert.True(target.WaveformBlobSampleCount > 0);
        return target;
    }

    private static (byte[] Low, byte[] Mid, byte[] High) SplitBands(AudioFeaturesEntity features)
    {
        int count = features.WaveformBlobSampleCount;
        var blob = features.WaveformBlob!;
        return (
            blob.Take(count).ToArray(),
            blob.Skip(count).Take(count).ToArray(),
            blob.Skip(count * 2).Take(count).ToArray());
    }

    private static void WriteStereoWav(
        string path,
        int sampleRate,
        int durationSeconds,
        Func<double, (float Amplitude, double Frequency)> low,
        float highAmplitude = 0f)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
        for (int i = 0; i < sampleRate * durationSeconds; i++)
        {
            double seconds = i / (double)sampleRate;
            var (amplitude, frequency) = low(seconds);
            float bass = amplitude * MathF.Sin(2f * MathF.PI * (float)frequency * i / sampleRate);
            float treble = highAmplitude * MathF.Sin(2f * MathF.PI * 6000f * i / sampleRate);
            writer.WriteSample(bass + treble);
            writer.WriteSample(bass + treble);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
