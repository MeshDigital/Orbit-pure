using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Models;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Audio.Separation;

/// <summary>
/// #285 sanity check for the on-demand vocal detector: separates each file, reads the Demucs vocal
/// stem, and prints density, span, class and intensity. No database. Run it on a known vocal track
/// and a known instrumental to confirm they separate.
/// Usage: GenreBenchmark --vocal-stem file1.flac file2.mp3 …
/// </summary>
internal static class VocalStemBench
{
    public static async Task<int> RunAsync(string[] files)
    {
        var model = new DemucsModelManager();
        var inner = new DemucsOnnxSeparator(model);
        var separator = new CachedStemSeparator(
            inner,
            new StemCacheService(NullLogger<StemCacheService>.Instance),
            NullLogger<CachedStemSeparator>.Instance);

        if (!separator.IsAvailable)
        {
            Console.Error.WriteLine(model.UnusableReason() ?? $"Demucs model not usable at '{model.ModelPath}'.");
            return 2;
        }

        Console.WriteLine("density  start    end      type          intensity  file");
        foreach (var file in files)
        {
            string? dir = null;
            try
            {
                if (!File.Exists(file))
                {
                    Console.WriteLine($"   —                                        (missing)  {file}");
                    continue;
                }

                dir = Path.Combine(Path.GetTempPath(), "orbit_vocal_bench", Guid.NewGuid().ToString("N"));
                var stems = await separator.SeparateAsync(file, dir).ConfigureAwait(false);
                if (!stems.TryGetValue(StemType.Vocals, out var vocal) || !File.Exists(vocal))
                {
                    Console.WriteLine($"   —                                        (no vocal stem)  {Path.GetFileName(file)}");
                    continue;
                }

                var rms = ReadRms(vocal, VocalActivityService.WindowSeconds);
                var r = VocalActivity.FromStemRms(rms, VocalActivityService.WindowSeconds);
                static string T(float? s) => s is float v ? TimeSpan.FromSeconds(v).ToString(@"m\:ss", CultureInfo.InvariantCulture) : "—";
                Console.WriteLine($"{r.Density,6:0.00}   {T(r.StartSeconds),-7}  {T(r.EndSeconds),-7}  {r.Type,-12}  {r.Intensity,8:0.00}   {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  skipped {Path.GetFileName(file)}: {ex.Message}");
            }
            finally
            {
                if (dir is not null)
                {
                    try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
                }
            }
        }

        return 0;
    }

    private static float[] ReadRms(string wav, double windowSeconds)
    {
        float[] samples;
        int rate, channels;
        using (var reader = new AudioFileReader(wav))
        {
            rate = reader.WaveFormat.SampleRate;
            channels = reader.WaveFormat.Channels;
            var total = (int)(reader.Length / sizeof(float));
            var buffer = new float[total];
            var read = reader.Read(buffer, 0, total);
            samples = buffer[..read];
        }

        var windowFrames = Math.Max(1, (int)Math.Round(rate * windowSeconds));
        var frames = samples.Length / channels;
        var windowCount = (int)Math.Ceiling(frames / (double)windowFrames);
        var rms = new float[windowCount];
        for (var w = 0; w < windowCount; w++)
        {
            var startFrame = w * windowFrames;
            var endFrame = Math.Min(frames, startFrame + windowFrames);
            double sum = 0.0;
            for (var f = startFrame; f < endFrame; f++)
            {
                float s = 0f;
                for (var c = 0; c < channels; c++) s += samples[f * channels + c];
                s /= channels;
                sum += (double)s * s;
            }
            var count = endFrame - startFrame;
            rms[w] = count > 0 ? (float)Math.Sqrt(sum / count) : 0f;
        }
        return rms;
    }
}
