using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NWaves.Filters.Fda;
using NWaves.Transforms;
using NWaves.Windows;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Similarity;

/// <summary>
/// <c>--mel-experiment N</c>: a dev aid that checks the EffNet input preprocessing against tracks
/// with a known genre. It runs the Discogs-EffNet model (CPU) on a 60 s excerpt of N tracks per club
/// bucket plus Other, with every combination of the three uncertain preprocessing choices: log base,
/// window normalisation and mel-filter normalisation. For each, it reports how often the Discogs-400 bucket vote
/// matches the label and how often a degenerate "noise" style tops the output.
/// </summary>
static class MelExperiment
{
    private sealed record Variant(string Name, bool Log10, bool NormalizedWindow, bool AreaNormalizedMel);

    public static async Task<int> RunAsync(Dictionary<string, TruthLabel> truth, int perBucket)
    {
        var buckets = new[] { GenreBucket.DrumAndBass, GenreBucket.House, GenreBucket.Techno, GenreBucket.Other };
        var picks = buckets.SelectMany(b => truth.Values.Where(t => t.Bucket == b && File.Exists(t.FilePath))
                .OrderBy(t => t.Hash, StringComparer.Ordinal).Take(perBucket)).ToList();
        Console.WriteLine($"Mel experiment on {picks.Count} tracks ({perBucket} per bucket: {string.Join(", ", buckets)})");

        var decoder = new AudioIngestionPipeline(NullLogger<AudioIngestionPipeline>.Instance);
        var clips = new List<(TruthLabel Truth, float[] Audio16k)>();
        foreach (var t in picks)
        {
            string? wav = null;
            try
            {
                wav = await decoder.DecodeToTempWavAsync(new TrackAudioSource(t.FilePath));
                var (samples, rate, channels) = AudioIngestionPipeline.ReadPcmFloat(wav);
                var mono = new float[samples.Length / channels];
                for (var i = 0; i < mono.Length; i++) { float s = 0; for (var c = 0; c < channels; c++) s += samples[i * channels + c]; mono[i] = s / channels; }
                var a16 = EffnetMelSpectrogramExtractor.ResampleTo16k(mono, rate);
                var mid = Math.Max(0, a16.Length / 2 - 30 * 16000);
                clips.Add((t, a16.AsSpan(mid, Math.Min(60 * 16000, a16.Length - mid)).ToArray()));
            }
            catch (Exception ex) { Console.Error.WriteLine($"  skipped {Path.GetFileName(t.FilePath)}: {ex.Message}"); }
            finally { if (wav is not null) try { File.Delete(wav); } catch (IOException) { /* temp file; best effort */ } }
        }

        var labels = new DiscogsStyleLabels(NullLogger<DiscogsStyleLabels>.Instance);
        if (!labels.IsUsable) { Console.Error.WriteLine("Discogs labels missing."); return 3; }
        var model = Path.Combine(AppContext.BaseDirectory, "Tools", "Essentia", "models", "discogs-effnet-bsdynamic-1.onnx");
        using var session = new InferenceSession(model, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });

        var variants = new List<Variant>();
        foreach (var log10 in new[] { false, true })
            foreach (var win in new[] { false, true })
                foreach (var area in new[] { false, true })
                    variants.Add(new Variant($"{(log10 ? "log10" : "ln   ")} {(win ? "win-norm" : "win-raw ")} {(area ? "mel-area" : "mel-unit")}", log10, win, area));

        Console.WriteLine($"{"variant",-28} {"bucket match",12} {"noise-top",10}  mean max   example (first DnB clip)");
        foreach (var v in variants)
        {
            int match = 0, noiseTop = 0; double maxSum = 0; string example = "";
            foreach (var (t, audio) in clips)
            {
                var scores = Predict(session, Patches(audio, v));
                if (scores is null) continue;
                var top = Enumerable.Range(0, scores.Length).OrderByDescending(i => scores[i]).Take(3).ToArray();
                maxSum += scores[top[0]];
                var topKey = labels.All[top[0]].Key;
                if (topKey is "Electronic---Noise" or "Hip Hop---DJ Battle Tool" or "Electronic---Rhythmic Noise") noiseTop++;
                if (BucketVote(scores, labels) == t.Bucket) match++;
                if (example.Length == 0 && t.Bucket == GenreBucket.DrumAndBass)
                    example = string.Join(", ", top.Select(i => $"{labels.All[i].Name} {scores[i]:F2}"));
            }
            Console.WriteLine($"{v.Name,-28} {match,5}/{clips.Count,-6} {noiseTop,5}/{clips.Count,-4} {maxSum / Math.Max(1, clips.Count),8:F3}   {example}");
        }
        return 0;
    }

    /// <summary>The Discogs bucket with the highest noisy-OR, like fusion v2 (floor 0.05).</summary>
    private static GenreBucket? BucketVote(float[] scores, DiscogsStyleLabels labels)
    {
        var miss = new Dictionary<GenreBucket, double>();
        for (var i = 0; i < scores.Length; i++)
        {
            if (scores[i] < 0.05f) continue;
            var m = GenreTaxonomy.MapDiscogsStyle(labels.All[i].Key);
            if (m.IsJunk || m.Weight <= 0) continue;
            miss[m.Bucket] = miss.GetValueOrDefault(m.Bucket, 1.0) * (1 - m.Weight * scores[i]);
        }
        return miss.Count == 0 ? null : miss.OrderBy(kv => kv.Value).First().Key;
    }

    private static float[]? Predict(InferenceSession session, List<float[]> patches)
    {
        if (patches.Count == 0) return null;
        var tensor = new DenseTensor<float>(new[] { patches.Count, 128, 96 });
        for (var p = 0; p < patches.Count; p++)
            for (var i = 0; i < 128 * 96; i++) tensor.Buffer.Span[p * 128 * 96 + i] = patches[p][i];
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor("melspectrogram", tensor) });
        var act = results.First(r => r.Name == "activations").AsTensor<float>();
        var mean = new float[400];
        for (var p = 0; p < patches.Count; p++) for (var c = 0; c < 400; c++) mean[c] += act[p, c] / patches.Count;
        return mean;
    }

    private static List<float[]> Patches(float[] audio, Variant v)
    {
        const int size = 512, hop = 256, bands = 96, frames = 128;
        var window = Window.OfType(WindowType.Hann, size);
        if (v.NormalizedWindow)
        {
            // Essentia Windowing(normalized=true): area 1, then ×2.
            var sum = window.Sum();
            for (var i = 0; i < size; i++) window[i] = 2f * window[i] / sum;
        }
        var bank = FilterBanks.MelBankSlaney(bands, size, 16000, 0, 8000, normalizeGain: v.AreaNormalizedMel);
        var fft = new RealFft(size);
        var buf = new float[size]; var re = new float[size]; var im = new float[size];
        var mel = new List<float[]>();
        for (var off = 0; off + size <= audio.Length; off += hop)
        {
            for (var n = 0; n < size; n++) buf[n] = audio[off + n] * window[n];
            fft.Direct(buf, re, im);
            var row = new float[bands];
            for (var b = 0; b < bands; b++)
            {
                double e = 0;
                for (var k = 0; k <= size / 2; k++) e += bank[b][k] * (re[k] * re[k] + im[k] * im[k]);
                row[b] = (float)(v.Log10 ? Math.Log10(1 + 10000 * e) : Math.Log(1 + 10000 * e));
            }
            mel.Add(row);
        }
        var patches = new List<float[]>();
        for (var p = 0; p + frames <= mel.Count; p += frames)
        {
            var patch = new float[frames * bands];
            for (var t = 0; t < frames; t++) Array.Copy(mel[p + t], 0, patch, t * bands, bands);
            patches.Add(patch);
        }
        return patches;
    }
}
