using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Engine.Analysis.CueDetr;
using SLSKDONET.Services.AudioAnalysis;
using Xunit;
using Xunit.Abstractions;

namespace SLSKDONET.Tests.Engine;

/// <summary>
/// The C# CUE-DETR pipeline against the Python reference (Tools/cue-detr/reference.py, itself
/// checked to reproduce ETH-DISCO's predict.py exactly). Fixtures live in TestData/CueDetr.
/// </summary>
public class CueDetrParityTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "TestData", "CueDetr");
    private readonly ITestOutputHelper _out;
    public CueDetrParityTests(ITestOutputHelper output) => _out = output;

    private static T[] Read<T>(string name) where T : struct =>
        MemoryMarshal.Cast<byte, T>(File.ReadAllBytes(Path.Combine(Dir, name))).ToArray();

    private static JsonElement Meta => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "meta.json"))).RootElement;

    private static float[] ClipAudio() => Read<short>("clip_22050_s16.raw").Select(s => s / 32768f).ToArray();

    [Fact]
    public void MelDb_MatchesLibrosa()
    {
        var expected = Read<float>("clip_mel_db_f32.bin");
        var mel = CueDetrFrontEnd.MelDb(ClipAudio(), out int frames);

        Assert.Equal(Meta.GetProperty("mel_shape")[1].GetInt32(), frames);
        Assert.Equal(expected.Length, mel.Length);
        double maxDiff = expected.Zip(mel, (a, b) => Math.Abs(a - b)).Max();
        _out.WriteLine($"max |dB diff| = {maxDiff:E2}");
        Assert.True(maxDiff < 0.01, $"mel dB differs from librosa by up to {maxDiff}");
    }

    [Fact]
    public void Image_FromLibrosaMel_IsBitExact()
    {
        var mel = Read<float>("clip_mel_db_f32.bin");
        int frames = Meta.GetProperty("mel_shape")[1].GetInt32();
        Assert.Equal(Read<byte>("clip_rgb_u8.bin"), CueDetrFrontEnd.ToImage(mel, frames));
    }

    [Fact]
    public void Image_FromOwnMel_MatchesReference()
    {
        var expected = Read<byte>("clip_rgb_u8.bin");
        var mel = CueDetrFrontEnd.MelDb(ClipAudio(), out int frames);
        var image = CueDetrFrontEnd.ToImage(mel, frames);
        int differing = expected.Where((b, i) => b != image[i]).Count();
        _out.WriteLine($"{differing} of {expected.Length} bytes differ");
        // Last-ulp float differences can move a pixel across a colormap step; nothing more.
        Assert.True(differing < expected.Length / 1000, $"{differing} bytes differ");
    }

    [Fact]
    public void Windows_AndRampPadding_AreBitExact()
    {
        var image = Read<byte>("clip_rgb_u8.bin");
        int frames = Meta.GetProperty("mel_shape")[1].GetInt32();
        var borders = CueDetrFrontEnd.WindowBorders(frames);
        Assert.Equal(Meta.GetProperty("borders").EnumerateArray().Select(e => e.GetInt32()), borders);

        var expected = Read<byte>("clip_windows_u8.bin");
        int size = CueDetrFrontEnd.Mels * CueDetrFrontEnd.WindowWidth * 3;
        for (int w = 0; w < borders.Length; w++)
            Assert.True(expected.AsSpan(w * size, size).SequenceEqual(CueDetrFrontEnd.Window(image, frames, borders[w])),
                $"window {w} (left {borders[w]}) differs");
    }

    [Fact]
    public void PostProcessing_MatchesReferenceCueFrames()
    {
        var track = Meta.GetProperty("track");
        var shape = track.GetProperty("logits_shape").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var borders = track.GetProperty("borders").EnumerateArray().Select(e => e.GetInt32()).ToArray();

        var cues = CueDetrPostProcessor.Process(Read<float>("track_logits_f32.bin"), Read<float>("track_boxes_f32.bin"),
            borders, shape[1], shape[2]);

        Assert.Equal(track.GetProperty("cue_frames").EnumerateArray().Select(e => e.GetInt32()), cues.Select(c => c.Frame));
        Assert.All(cues, c => Assert.InRange(c.Score, 0.9, 1.0));
    }

    [Theory]
    [InlineData(new[] { 0, 1, 0, 2, 0, 3, 0.0 }, 1, new[] { 1, 3, 5 })]
    [InlineData(new[] { 0, 1, 0, 2, 0, 3, 0.0 }, 3, new[] { 1, 5 })]
    [InlineData(new[] { 0, 1, 1, 1, 0, 0.95, 0.95, 0, 1 }, 1, new[] { 2, 5 })]              // plateaus → middle; edge sample never a peak
    [InlineData(new[] { 0, 0.95, 0.2, 0.99, 0.1, 0.92, 0, 1.0, 0 }, 3, new[] { 3, 7 })]      // higher peaks win the distance contest
    [InlineData(new[] { 0.5, 1, 1, 0.5, 1, 1, 1, 0.2, 0.91, 0.3 }, 3, new[] { 1, 5, 8 })]
    public void FindPeaks_MatchesScipy(double[] x, int distance, int[] expected) =>
        Assert.Equal(expected, CueDetrPostProcessor.FindPeaks(x, 0.9, distance));

    [Fact]
    public void FindPeaks_MatchesScipy_OnRandomSeries()
    {
        var x = Read<double>("peaks_random_f64.bin");
        var expected = Meta.GetProperty("peaks_random").GetProperty("peaks").EnumerateArray().Select(e => e.GetInt32());
        Assert.Equal(expected, CueDetrPostProcessor.FindPeaks(x, 0.9, 16));
    }

    [Fact]
    public void ResampleTo22050_HalvesLength_AndKeepsInBandToneLevel()
    {
        const int rate = 44100;
        var tone = Enumerable.Range(0, rate * 2).Select(i => (float)Math.Sin(2 * Math.PI * 1000 * i / rate)).ToArray();
        var alias = Enumerable.Range(0, rate * 2).Select(i => (float)Math.Sin(2 * Math.PI * 16000 * i / rate)).ToArray();

        var y = CueDetrFrontEnd.ResampleTo22050(tone, rate);
        var a = CueDetrFrontEnd.ResampleTo22050(alias, rate);

        Assert.Equal(rate, y.Length);
        static double Rms(float[] s) => Math.Sqrt(s.Skip(2000).Take(s.Length - 4000).Average(v => (double)v * v));
        Assert.InRange(Rms(y), 0.70, 0.72);   // 1 kHz passes (≈ 1/√2)
        Assert.True(Rms(a) < 1e-3, $"16 kHz should be removed before decimation, rms {Rms(a)}"); // would alias to 6.05 kHz
    }

    [Fact]
    public async Task Service_RunsTheBundledModel_WhenPresent()
    {
        var modelPath = FindModel();
        if (modelPath == null) { _out.WriteLine("cue-detr.onnx not found — skipped"); return; }

        using var service = new CueDetrService(new OutputLogger(_out), modelPath);
        Assert.True(service.IsAvailable);
        // 12 s clip → 8 windows; the synthetic loop has no real structure, so only check it runs.
        var cues = await service.DetectAsync(ClipAudio(), CueDetrFrontEnd.SampleRate);
        Assert.NotNull(cues);
        _out.WriteLine($"{service.Provider}: {cues!.Count} cues");

        // Manual end-to-end check against reference.py on a real file (not part of CI):
        //   set ORBIT_CUEDETR_TRACK=D:\Music\some track.flac
        if (Environment.GetEnvironmentVariable("ORBIT_CUEDETR_TRACK") is { Length: > 0 } track && File.Exists(track))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var real = await service.DetectFileAsync(track);
            _out.WriteLine($"{Path.GetFileName(track)}: {string.Join(", ", real!.Select(c => $"{c.Seconds:0.00}s (frame {c.Frame}, {c.Score:0.000})"))} in {sw.ElapsedMilliseconds} ms");
        }
    }

    private static string? FindModel()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, CueDetrService.DefaultModelRelativePath);
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 1_000_000) return candidate; // not an LFS pointer
        }
        return null;
    }

    private sealed class OutputLogger : Microsoft.Extensions.Logging.ILogger<CueDetrService>
    {
        private readonly ITestOutputHelper _o;
        public OutputLogger(ITestOutputHelper o) => _o = o;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel l) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel l, Microsoft.Extensions.Logging.EventId id, TState st, Exception? ex, Func<TState, Exception?, string> f)
            => _o.WriteLine($"[{l}] {f(st, ex)} {ex}");
    }
}
