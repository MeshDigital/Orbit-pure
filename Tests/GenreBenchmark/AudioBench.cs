using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Similarity;

/// <summary>Model and rhythm inputs computed from audio for one track (cached between runs).</summary>
sealed record AudioInputs(string Hash, List<GenreLabelScore> Jamendo, float[]? DiscogsScores, RhythmSignature? Rhythm, float Bpm, double Seconds);

/// <summary>
/// <c>--audio</c>: decodes a deterministic sample of labelled files (read-only) with the app's own
/// FFmpeg pipeline, re-runs EffNet (embedding + the 400 Discogs style sigmoids + the Jamendo head)
/// and the rhythm signature, then scores fusion with and without the rhythm &amp; tempo gate (GA6)
/// in the same run. Inputs are cached with <c>--audio-cache</c> so tuning reruns skip the decode.
/// </summary>
static class AudioBench
{
    public static async Task<int> RunAsync(Dictionary<string, TruthLabel> truth, Dictionary<string, FeatureRow> features, Options opts)
    {
        var sample = Sample(truth, features, opts.Limit);
        Console.WriteLine($"Audio sample: {sample.Count} tracks (round-robin over buckets, by hash): " +
                          string.Join(", ", sample.GroupBy(s => s.Bucket).Select(g => $"{g.Key} {g.Count()}")));

        var cache = LoadCache(opts.AudioCache);
        var styles = new DiscogsStyleLabels(NullLogger<DiscogsStyleLabels>.Instance);
        var pending = sample.Where(s => !cache.ContainsKey(s.Hash)).ToList();
        if (pending.Count > 0)
        {
            if (!styles.IsUsable) { Console.Error.WriteLine("Discogs style labels not found next to the exe."); return 3; }
            using var extractor = new DiscogsEffnetEmbeddingExtractor(NullLogger<DiscogsEffnetEmbeddingExtractor>.Instance);
            using var heads = new EffnetClassifierHeadService(NullLogger<EffnetClassifierHeadService>.Instance);
            var decoder = new AudioIngestionPipeline(NullLogger<AudioIngestionPipeline>.Instance);
            var started = DateTime.UtcNow;
            var done = 0;
            foreach (var t in pending)
            {
                var row = features[t.Hash];
                var inputs = await ComputeAsync(t, row, decoder, extractor, heads);
                done++;
                if (inputs is not null) cache[t.Hash] = inputs;
                if (done % 10 == 0 || done == pending.Count)
                {
                    var eta = TimeSpan.FromSeconds((DateTime.UtcNow - started).TotalSeconds / done * (pending.Count - done));
                    Console.WriteLine($"  decoded {done}/{pending.Count}  (eta {eta:mm\\:ss})");
                    SaveCache(opts.AudioCache, cache);
                }
            }
        }

        var scoredInputs = sample.Where(s => cache.ContainsKey(s.Hash)).ToList();
        if (scoredInputs.Count == 0) { Console.Error.WriteLine("No track could be decoded."); return 1; }
        var timing = scoredInputs.Select(s => cache[s.Hash].Seconds).Where(x => x > 0).DefaultIfEmpty(0).Average();
        Console.WriteLine($"Scored {scoredInputs.Count} decoded tracks (mean decode+models+rhythm {timing:F1} s/track; " +
                          $"rhythm reliable (≥ 0.5) on {scoredInputs.Count(s => cache[s.Hash].Rhythm?.Reliability >= 0.5)})");
        Console.WriteLine();

        List<Scored> Evaluate(bool gate) => scoredInputs.Select(t =>
        {
            var inputs = cache[t.Hash];
            IReadOnlyList<GenreLabelScore>? discogs = inputs.DiscogsScores is { Length: DiscogsStyleLabels.ExpectedClassCount } d && styles.IsUsable
                ? styles.All.Select((s, i) => new GenreLabelScore(s.Key, d[i])).ToArray()
                : null;
            var fused = GenreFusionEngine.Fuse(new GenreFusionInput(inputs.Jamendo, inputs.Bpm, discogs, gate ? inputs.Rhythm : null));
            var evidence = new List<string>
            {
                "jamendo: " + string.Join(", ", inputs.Jamendo.Take(5).Select(j => $"{j.Label} {j.Score:F2}")),
                "discogs: " + (discogs is null ? "none" : string.Join(", ", discogs.OrderByDescending(x => x.Score).Take(5).Select(x => $"{x.Label} {x.Score:F2}"))),
                "rhythm: " + (inputs.Rhythm is { } r ? $"4x4 {r.FourOnFloorScore:F2} backbeat {r.BackbeatScore:F2} half {r.HalfTimeScore:F2} break {r.BreakIrregularity:F2} {r.TempoLow:F0}/{r.TempoHigh:F0} rel {r.Reliability:F2}" : "none"),
            };
            if (fused?.Gate is { Count: > 0 } g) evidence.Add("gate: " + string.Join("; ", g.Select(e => $"{e.RuleId} {e.Bucket} ×{e.Factor:F2}")));
            if (fused is not null) evidence.Add("fused: " + string.Join(", ", fused.Distribution.Select(kv => $"{kv.Key} {kv.Value:F3}")));
            var prediction = fused is null
                ? new Prediction(null, "", 0, evidence)
                : new Prediction(fused.Bucket, fused.WinnerLabel, fused.Confidence, evidence);
            return new Scored(t, features[t.Hash], prediction);
        }).ToList();

        var noGate = Evaluate(gate: false);
        var withGate = Evaluate(gate: true);
        var noGateSummary = Metrics.Compute(noGate, "audio, no gate");
        var gateSummary = Metrics.Compute(withGate, "audio, gate");

        Report.Print(noGateSummary, "Audio sample: Discogs-400 + Jamendo, no gate");
        Report.Print(gateSummary, "Audio sample: with the rhythm & tempo gate");
        Report.PrintTempoBands(withGate);
        Report.Compare(gateSummary, noGateSummary);

        if (opts.Trace is { } trace)
            foreach (var r in withGate.Where(r => r.Truth.Display.Contains(trace, StringComparison.OrdinalIgnoreCase)).Take(10))
                Report.Trace(r);
        if (opts.Misclassified > 0) Report.Misclassified(withGate, opts.Misclassified);

        if (opts.Compare is { } comparePath)
            Report.Compare(gateSummary, JsonSerializer.Deserialize<Summary>(File.ReadAllText(comparePath), Json.Options)!);
        if (opts.SummaryPath is { } summaryPath)
        {
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(gateSummary, Json.Options));
            Console.WriteLine($"Summary written to {summaryPath}");
        }
        return 0;
    }

    /// <summary>Round-robin over buckets (each ordered by hash) so small buckets are fully represented.</summary>
    private static List<TruthLabel> Sample(Dictionary<string, TruthLabel> truth, Dictionary<string, FeatureRow> features, int limit)
    {
        var queues = truth.Values
            .Where(t => features.ContainsKey(t.Hash) && !string.IsNullOrWhiteSpace(t.FilePath) && File.Exists(t.FilePath))
            .GroupBy(t => t.Bucket)
            .Select(g => new Queue<TruthLabel>(g.OrderBy(t => t.Hash, StringComparer.Ordinal)))
            .ToList();
        var picked = new List<TruthLabel>();
        while (picked.Count < limit && queues.Any(q => q.Count > 0))
            foreach (var q in queues.Where(q => q.Count > 0))
            {
                picked.Add(q.Dequeue());
                if (picked.Count >= limit) break;
            }
        return picked;
    }

    private static async Task<AudioInputs?> ComputeAsync(TruthLabel t, FeatureRow row, AudioIngestionPipeline decoder,
        DiscogsEffnetEmbeddingExtractor extractor, EffnetClassifierHeadService heads)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? wav = null;
        try
        {
            wav = await decoder.DecodeToTempWavAsync(new TrackAudioSource(t.FilePath));
            var (samples, rate, channels) = AudioIngestionPipeline.ReadPcmFloat(wav);
            var mono = new float[samples.Length / Math.Max(1, channels)];
            for (var i = 0; i < mono.Length; i++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += samples[i * channels + c];
                mono[i] = sum / channels;
            }

            var rhythm = RhythmSignatureAnalyzer.Analyze(mono, rate, row.Bpm, BeatGridFitter.ParseTicks(row.BeatGridJson));
            var effnet = await extractor.ExtractAsync(EffnetMelSpectrogramExtractor.ResampleTo16k(mono, rate));
            List<GenreLabelScore> jamendo = new();
            float[]? discogs = null;
            if (effnet is { } e)
            {
                discogs = e.StylePredictions;
                jamendo = heads.Classify(e.Embedding).JamendoGenres.Select(g => new GenreLabelScore(g.Label, g.Probability)).ToList();
            }
            return new AudioInputs(t.Hash, jamendo, discogs, rhythm, row.Bpm, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  skipped {Path.GetFileName(t.FilePath)}: {ex.Message}");
            return null;
        }
        finally
        {
            if (wav is not null)
            {
                try { File.Delete(wav); }
                catch (IOException ex) { Console.Error.WriteLine($"  could not delete temp file {wav}: {ex.Message}"); }
            }
        }
    }

    private static Dictionary<string, AudioInputs> LoadCache(string? path)
    {
        if (path is null || !File.Exists(path)) return new(StringComparer.Ordinal);
        var list = JsonSerializer.Deserialize<List<AudioInputs>>(File.ReadAllText(path), Json.Options) ?? new();
        return list.ToDictionary(x => x.Hash, StringComparer.Ordinal);
    }

    private static void SaveCache(string? path, Dictionary<string, AudioInputs> cache)
    {
        if (path is null) return;
        File.WriteAllText(path, JsonSerializer.Serialize(cache.Values.ToList(), Json.Options));
    }
}
