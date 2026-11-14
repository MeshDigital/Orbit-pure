// Genre accuracy benchmark (epic #245, GA3 / #249). Read-only.
//
// Labels tracks from trusted genre metadata in a COPY of the library database (the user's own
// edits, file tags, high-confidence Beatport/Discogs lookups) and scores ORBIT's genre decision
// against them. It opens the copy in SQLite ReadOnly mode and refuses the live database.
//
// Modes:
//   --stored   score what is already in AudioFeatures.DetectedSubGenre (the as-shipped baseline)
//   --offline  (default) recompute fusion from persisted rows with the app's own GenreFusionEngine:
//              the Jamendo head re-run on the stored embedding + the stored Discogs-400 scores.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Similarity;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var opts = Options.Parse(args);
if (opts is null) { Options.PrintUsage(); return 2; }
Metrics.Threshold = opts.ConfidentThreshold;

// ── Safety: never the live database ─────────────────────────────────────────
var livePath = Path.GetFullPath(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "library.db"));
var dbPath = Path.GetFullPath(opts.DbPath);
if (string.Equals(dbPath, livePath, StringComparison.OrdinalIgnoreCase) ||
    string.Equals(dbPath, Path.GetFullPath(OrbitPaths.LibraryDbPath), StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Refusing to open the live ORBIT database. Make a copy first (see README.md).");
    return 2;
}
if (!File.Exists(dbPath)) { Console.Error.WriteLine($"Database copy not found: {dbPath}"); return 2; }

var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(cs).Options);

// ── Ground truth ────────────────────────────────────────────────────────────
var truth = GroundTruth.Load(db, opts, out var truthStats);
Console.WriteLine($"Ground truth: {truth.Count} labelled tracks  ({truthStats})");
foreach (var g in truth.Values.GroupBy(t => t.Bucket).OrderBy(g => g.Key))
    Console.WriteLine($"  {g.Key,-12} {g.Count(),6}   by source: " +
                      string.Join(", ", g.GroupBy(t => t.Source).OrderByDescending(s => s.Count()).Select(s => $"{s.Key} {s.Count()}")));

// ── Predictions ─────────────────────────────────────────────────────────────
var hashes = truth.Keys.ToList();
var features = new Dictionary<string, FeatureRow>(StringComparer.Ordinal);
foreach (var chunk in hashes.Chunk(500))
{
    foreach (var f in db.AudioFeatures.AsNoTracking().Where(f => chunk.Contains(f.TrackUniqueHash)))
        features[f.TrackUniqueHash] = new FeatureRow(f.TrackUniqueHash, f.Bpm, f.DetectedSubGenre, f.SubGenreConfidence,
            f.Embedding, f.EmbeddingModelTag, f.StyleScores);
}

var predictor = opts.Mode == "stored" ? (IPredictor)new StoredPredictor() : new OfflinePredictor(opts);
if (predictor is OfflinePredictor op && !op.Ready(out var why)) { Console.Error.WriteLine(why); return 3; }

var results = new List<Scored>();
var skipped = new Dictionary<string, int>();
var sw = System.Diagnostics.Stopwatch.StartNew();
foreach (var (hash, t) in truth)
{
    if (!features.TryGetValue(hash, out var row)) { Count(skipped, "not analysed"); continue; }
    var p = predictor.Predict(row, out var skip);
    if (skip is not null) { Count(skipped, skip); continue; }
    results.Add(new Scored(t, row, p!));
}
sw.Stop();
Console.WriteLine($"Mode: {opts.Mode}  ·  scored {results.Count} tracks in {sw.Elapsed.TotalSeconds:F1} s" +
                  (skipped.Count > 0 ? "  ·  skipped: " + string.Join(", ", skipped.Select(kv => $"{kv.Key} {kv.Value}")) : ""));
Console.WriteLine($"Inputs: embedding stored for {results.Count(r => r.Row.Embedding is { Length: > 0 })}, " +
                  $"Discogs-400 style scores stored for {results.Count(r => r.Row.StyleScores is { Length: 400 })} of {results.Count} scored tracks");
Console.WriteLine();

if (results.Count == 0) { Console.Error.WriteLine("Nothing to score."); return 1; }

// ── Report ──────────────────────────────────────────────────────────────────
var summary = Metrics.Compute(results, opts.Mode);
Report.Print(summary, "All tracks");
Report.PrintTempoBands(results);
Report.PrintSplit(results, opts.Mode);

if (opts.Trace is { } trace)
    foreach (var r in results.Where(r => r.Truth.Display.Contains(trace, StringComparison.OrdinalIgnoreCase)).Take(10))
        Report.Trace(r);

if (opts.Misclassified > 0)
    Report.Misclassified(results, opts.Misclassified);

if (opts.Unlabelled > 0)
    foreach (var r in results.Where(r => r.Prediction.Bucket is null).Take(opts.Unlabelled))
        Report.Trace(r);

if (opts.Compare is { } comparePath)
    Report.Compare(summary, JsonSerializer.Deserialize<Summary>(File.ReadAllText(comparePath), Json.Options)!);

if (opts.SummaryPath is { } summaryPath)
{
    File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, Json.Options));
    Console.WriteLine($"Summary written to {summaryPath}");
}
return 0;

static void Count(Dictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;

// ════════════════════════════════════════════════════════════════════════════

sealed record Options(string DbPath, string Mode, string? SummaryPath, string? Compare, string? Trace,
    int Misclassified, bool IncludeUntracked, double ConfidentThreshold, int Unlabelled)
{
    public static Options? Parse(string[] args)
    {
        string? db = null, summary = null, compare = null, trace = null;
        var mode = "offline"; var mis = 0; var untracked = false; var threshold = 0.7; var unlabelled = 0;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--db": db = Next(); break;
                case "--stored": mode = "stored"; break;
                case "--offline": mode = "offline"; break;
                case "--summary": summary = Next(); break;
                case "--compare": compare = Next(); break;
                case "--trace": trace = Next(); break;
                case "--misclassified": mis = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--unlabelled": unlabelled = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--include-untracked": untracked = true; break;
                case "--confident": threshold = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                default: Console.Error.WriteLine($"Unknown argument {args[i]}"); return null;
            }
        }
        return db is null ? null : new Options(db, mode, summary, compare, trace, mis, untracked, threshold, unlabelled);
    }

    public static void PrintUsage() => Console.Error.WriteLine(
        "Usage: GenreBenchmark --db <library copy> [--stored | --offline] [--summary out.json] [--compare baseline.json]\n" +
        "                      [--trace \"<artist/title/file part>\"] [--misclassified N] [--unlabelled N] [--include-untracked] [--confident 0.7]");
}

/// <summary>One labelled track. <c>Source</c> is where the label came from (user, tags, beatport, discogs, untracked).</summary>
sealed record TruthLabel(string Hash, GenreBucket Bucket, string Text, string Source, string Display);

sealed record FeatureRow(string Hash, float Bpm, string StoredLabel, float StoredConfidence,
    float[]? Embedding, string? EmbeddingTag, float[]? StyleScores);

/// <summary>A prediction: bucket (null = no label), the label text, its confidence and evidence lines for --trace.</summary>
sealed record Prediction(GenreBucket? Bucket, string Label, double Confidence, IReadOnlyList<string> Evidence);

sealed record Scored(TruthLabel Truth, FeatureRow Row, Prediction Prediction)
{
    public bool Correct => Prediction.Bucket == Truth.Bucket;
}

static class GroundTruth
{
    // Higher wins when a track has several provenance rows for "genre" (the primary key is per field, so normally one).
    private static readonly Dictionary<string, int> SourceRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user"] = 4, ["tags"] = 3, ["beatport"] = 2, ["discogs"] = 1,
    };

    public static Dictionary<string, TruthLabel> Load(AppDbContext db, Options opts, out string stats)
    {
        // Genres written from a model's suggestion (Track Inspector "Use as genre", maintenance
        // "Fill empty Genre from AI style") are circular: never ground truth.
        var modelWritten = db.TagChangeLog.AsNoTracking()
            .Where(c => c.Field == "Genre" && (c.Rule == "UseAsGenre" || c.Rule == "GenreFill") && c.RevertedAt == null && c.Applied > 0)
            .Select(c => c.TrackUniqueHash)
            .ToHashSet(StringComparer.Ordinal);

        var provenance = db.MetadataProvenance.AsNoTracking()
            .Where(p => p.Field == "genre")
            .ToList()
            .GroupBy(p => p.TrackUniqueHash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => SourceRank.GetValueOrDefault(p.Source)).First(), StringComparer.Ordinal);

        var entries = db.LibraryEntries.AsNoTracking()
            .Where(e => (e.Genres != null && e.Genres != "" && e.Genres != "[]") || (e.PrimaryGenre != null && e.PrimaryGenre != ""))
            .Select(e => new { e.UniqueHash, e.Genres, e.PrimaryGenre, e.Artist, e.Title, e.FilePath })
            .ToList();

        var result = new Dictionary<string, TruthLabel>(StringComparer.Ordinal);
        int circular = 0, untrusted = 0, junk = 0, conflict = 0, untracked = 0;
        foreach (var e in entries)
        {
            if (modelWritten.Contains(e.UniqueHash)) { circular++; continue; }

            string source;
            if (provenance.TryGetValue(e.UniqueHash, out var prov))
            {
                source = prov.Source.ToLowerInvariant();
                var trusted = source switch
                {
                    "user" or "tags" => true,
                    "beatport" => prov.Confidence >= 0.85,
                    "discogs" => prov.Confidence >= 0.85 && SplitGenres(e.Genres).Count <= 3, // release-level: weak on VA/many styles
                    _ => false, // maintenance, deezer/spotify (album/artist genres), musicbrainz…
                };
                if (!trusted) { untrusted++; continue; }
            }
            else if (opts.IncludeUntracked)
            {
                source = "untracked"; untracked++;
            }
            else { untrusted++; continue; }

            var texts = SplitGenres(e.Genres);
            if (texts.Count == 0 && !string.IsNullOrWhiteSpace(e.PrimaryGenre)) texts.Add(e.PrimaryGenre!);

            var buckets = texts
                .Select(t => (Text: t, Map: GenreTaxonomy.MapFreeText(t)))
                .Where(m => !m.Map.IsJunk && m.Map.Weight > 0)
                .ToList();
            if (buckets.Count == 0) { junk++; continue; }
            if (buckets.Select(b => b.Map.Bucket).Distinct().Count() > 1) { conflict++; continue; }

            var display = $"{e.Artist} – {e.Title} ({Path.GetFileName(e.FilePath)})";
            result[e.UniqueHash] = new TruthLabel(e.UniqueHash, buckets[0].Map.Bucket, string.Join(" / ", buckets.Select(b => b.Text)), source, display);
        }

        stats = $"excluded: {circular} model-written, {untrusted} untrusted source, {junk} junk-only, {conflict} conflicting" +
                (opts.IncludeUntracked ? $"; {untracked} untracked included" : "");
        return result;
    }

    /// <summary>Genres may be a JSON array or a ';'/','-separated string. '/' is kept: Beatport uses it inside names.</summary>
    public static List<string> SplitGenres(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        var s = raw.Trim();
        if (s.StartsWith('['))
        {
            try { return JsonSerializer.Deserialize<List<string>>(s)?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList() ?? new(); }
            catch (JsonException) { /* not JSON after all: fall through to splitting */ }
        }
        return s.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}

interface IPredictor
{
    /// <summary>Null prediction with a skip reason when the row can't be scored in this mode.</summary>
    Prediction? Predict(FeatureRow row, out string? skipReason);
}

/// <summary>What the app stored at analysis time (DetectedSubGenre + SubGenreConfidence).</summary>
sealed class StoredPredictor : IPredictor
{
    public Prediction? Predict(FeatureRow row, out string? skipReason)
    {
        skipReason = null;
        if (string.IsNullOrWhiteSpace(row.StoredLabel))
            return new Prediction(null, "", 0, new[] { "no stored genre" });
        var map = GenreTaxonomy.MapFreeText(row.StoredLabel);
        GenreBucket? bucket = map.IsJunk || map.Weight <= 0 ? null : map.Bucket;
        return new Prediction(bucket, row.StoredLabel, row.StoredConfidence, new[] { $"stored: {row.StoredLabel} @ {row.StoredConfidence:F2}" });
    }
}

/// <summary>
/// Re-runs the app's fusion on persisted inputs: the Jamendo head on the stored embedding and the
/// stored Discogs-400 scores, through <see cref="GenreFusionEngine"/> — the same code the app runs.
/// </summary>
sealed class OfflinePredictor : IPredictor
{
    private readonly EffnetClassifierHeadService _heads = new(NullLogger<EffnetClassifierHeadService>.Instance);
    private readonly DiscogsStyleLabels _styles = new(NullLogger<DiscogsStyleLabels>.Instance);
    private readonly string? _currentTag;

    public OfflinePredictor(Options opts)
    {
        var model = Path.Combine(AppContext.BaseDirectory, "Tools", "Essentia", "models", "discogs-effnet-bsdynamic-1.onnx");
        // The app's own tag (model hash + preprocessing version, #270), so old embeddings are skipped.
        if (File.Exists(model) && new FileInfo(model).Length > 1_000_000)
            _currentTag = DiscogsEffnetEmbeddingExtractor.BuildModelTag(model);
    }

    public bool Ready(out string reason)
    {
        if (_currentTag is null) { reason = "Embedding model missing or an LFS pointer (run `git lfs pull`)."; return false; }
        if (!_heads.IsAvailable) { reason = "Jamendo genre head not available (models missing next to the benchmark exe?)."; return false; }
        reason = ""; return true;
    }

    public Prediction? Predict(FeatureRow row, out string? skipReason)
    {
        skipReason = null;
        if (row.Embedding is not { Length: > 0 }) { skipReason = "no embedding"; return null; }
        if (!string.Equals(row.EmbeddingTag, _currentTag, StringComparison.Ordinal)) { skipReason = "embedding from another model version"; return null; }

        var head = _heads.Classify(row.Embedding);
        var jamendo = head.JamendoGenres.Select(g => new GenreLabelScore(g.Label, g.Probability)).ToArray();

        IReadOnlyList<GenreLabelScore>? discogs = null;
        if (row.StyleScores is { Length: DiscogsStyleLabels.ExpectedClassCount } scores && _styles.IsUsable)
            discogs = _styles.All.Select((s, i) => new GenreLabelScore(s.Key, scores[i])).ToArray();

        var evidence = new List<string>
        {
            "jamendo: " + string.Join(", ", jamendo.Take(5).Select(j => $"{j.Label} {j.Score:F2}")),
            "discogs: " + (discogs is null ? "none stored" : string.Join(", ", discogs.OrderByDescending(d => d.Score).Take(5).Select(d => $"{d.Label} {d.Score:F2}"))),
        };

        var fused = GenreFusionEngine.Fuse(new GenreFusionInput(jamendo, row.Bpm, discogs));
        if (fused is null)
        {
            evidence.Add("fusion: no winner");
            return new Prediction(null, "", 0, evidence);
        }

        evidence.Add("fused: " + string.Join(", ", fused.Distribution.Select(kv => $"{kv.Key} {kv.Value:F3}")) + (fused.RanHeuristic ? " (BPM heuristic)" : ""));
        // v2+ results carry their bucket; v1 only had text ("Other" alone would read as junk).
        GenreBucket? bucket = fused.Bucket;
        if (GenreFusionEngine.FusionVersion < 2)
        {
            var map = GenreTaxonomy.MapFreeText(fused.WinnerLabel);
            bucket = map.IsJunk || map.Weight <= 0 ? null : map.Bucket;
        }
        return new Prediction(bucket, fused.WinnerLabel, fused.Confidence, evidence);
    }
}

sealed record BucketStats(int Truth, int Predicted, int TruePositives, double Precision, double Recall, double F1, int ConfidentWrong);

sealed record BandStats(string Band, int N, double Accuracy, double ConfidentWrongRate);

sealed record Summary(
    string Mode, int N, double Accuracy, double Coverage, double ConfidentCoverage, double ConfidentWrongRate,
    double MacroF1Club, Dictionary<string, BucketStats> Buckets, Dictionary<string, Dictionary<string, int>> Confusion,
    List<BandStats> TempoBands, double ConfidentThreshold);

static class Metrics
{
    public static readonly GenreBucket[] Club = { GenreBucket.DrumAndBass, GenreBucket.Dubstep, GenreBucket.Techno, GenreBucket.House };
    public static double Threshold = 0.7;

    public static bool IsConfidentWrong(Scored r) => r.Prediction.Bucket is not null && !r.Correct && r.Prediction.Confidence >= Threshold;

    public static Summary Compute(IReadOnlyList<Scored> results, string mode)
    {
        var n = results.Count;
        var buckets = new Dictionary<string, BucketStats>();
        foreach (var b in Enum.GetValues<GenreBucket>())
        {
            var truth = results.Count(r => r.Truth.Bucket == b);
            var predicted = results.Count(r => r.Prediction.Bucket == b);
            var tp = results.Count(r => r.Truth.Bucket == b && r.Prediction.Bucket == b);
            var p = predicted > 0 ? (double)tp / predicted : 0;
            var rec = truth > 0 ? (double)tp / truth : 0;
            var f1 = p + rec > 0 ? 2 * p * rec / (p + rec) : 0;
            var cw = results.Count(r => r.Truth.Bucket == b && IsConfidentWrong(r));
            buckets[b.ToString()] = new BucketStats(truth, predicted, tp, p, rec, f1, cw);
        }

        var confusion = new Dictionary<string, Dictionary<string, int>>();
        foreach (var t in Enum.GetValues<GenreBucket>())
        {
            var row = new Dictionary<string, int>();
            foreach (var p in Enum.GetValues<GenreBucket>())
                row[p.ToString()] = results.Count(r => r.Truth.Bucket == t && r.Prediction.Bucket == p);
            row["none"] = results.Count(r => r.Truth.Bucket == t && r.Prediction.Bucket is null);
            confusion[t.ToString()] = row;
        }

        var clubF1 = Club.Where(b => buckets[b.ToString()].Truth > 0).Select(b => buckets[b.ToString()].F1).DefaultIfEmpty(0).Average();

        return new Summary(mode, n,
            Accuracy: n > 0 ? results.Count(r => r.Correct) / (double)n : 0,
            Coverage: n > 0 ? results.Count(r => r.Prediction.Bucket is not null) / (double)n : 0,
            ConfidentCoverage: n > 0 ? results.Count(r => r.Prediction.Bucket is not null && r.Prediction.Confidence >= Threshold) / (double)n : 0,
            ConfidentWrongRate: n > 0 ? results.Count(IsConfidentWrong) / (double)n : 0,
            MacroF1Club: clubF1, Buckets: buckets, Confusion: confusion,
            TempoBands: TempoBands(results), ConfidentThreshold: Threshold);
    }

    private static readonly (string Name, double Lo, double Hi)[] Bands =
    {
        ("<=110", 0, 110), ("110-135", 110, 135), ("135-150", 135, 150),
        ("150-165", 150, 165), ("165-185", 165, 185), (">185", 185, 10000),
    };

    public static List<BandStats> TempoBands(IReadOnlyList<Scored> results) =>
        Bands.Select(b =>
        {
            var inBand = results.Where(r => r.Row.Bpm > b.Lo && r.Row.Bpm <= b.Hi).ToList();
            return new BandStats(b.Name, inBand.Count,
                inBand.Count > 0 ? inBand.Count(r => r.Correct) / (double)inBand.Count : 0,
                inBand.Count > 0 ? inBand.Count(IsConfidentWrong) / (double)inBand.Count : 0);
        }).ToList();

    /// <summary>Deterministic 80/20 hold-out by track hash (FNV-1a), stable across runs and machines.</summary>
    public static bool IsHoldOut(string hash)
    {
        uint h = 2166136261;
        foreach (var c in Encoding.UTF8.GetBytes(hash)) { h ^= c; h *= 16777619; }
        return h % 5 == 0;
    }
}

static class Report
{
    private static string Pct(double v) => (100 * v).ToString("F1", CultureInfo.InvariantCulture) + "%";

    public static void Print(Summary s, string title)
    {
        Console.WriteLine($"── {title} ({s.N} tracks, mode {s.Mode}) ──");
        Console.WriteLine($"Accuracy {Pct(s.Accuracy)} · coverage {Pct(s.Coverage)} · confident (≥{s.ConfidentThreshold:F2}) {Pct(s.ConfidentCoverage)} · " +
                          $"CONFIDENT-WRONG {Pct(s.ConfidentWrongRate)} · club macro-F1 {s.MacroF1Club:F3}");
        Console.WriteLine();
        Console.WriteLine($"{"bucket",-12} {"truth",6} {"pred",6} {"prec",7} {"recall",7} {"F1",6} {"conf-wrong",10}");
        foreach (var (name, b) in s.Buckets)
            Console.WriteLine($"{name,-12} {b.Truth,6} {b.Predicted,6} {b.Precision,7:F3} {b.Recall,7:F3} {b.F1,6:F3} {b.ConfidentWrong,10}" +
                              (b.Truth is > 0 and < 20 ? "  (few tracks: don't over-read)" : ""));
        Console.WriteLine();
        var cols = Enum.GetNames<GenreBucket>().Append("none").ToArray();
        Console.WriteLine("confusion (rows = truth, columns = predicted)");
        Console.WriteLine($"{"",-12}" + string.Concat(cols.Select(c => $"{Short(c),8}")));
        foreach (var (t, row) in s.Confusion)
            Console.WriteLine($"{t,-12}" + string.Concat(cols.Select(c => $"{row[c],8}")));
        Console.WriteLine();
    }

    private static string Short(string c) => c switch { "DrumAndBass" => "DnB", "Dubstep" => "Dubstep", _ => c };

    public static void PrintTempoBands(IReadOnlyList<Scored> results)
    {
        Console.WriteLine("by tempo band (stored BPM)");
        foreach (var b in Metrics.TempoBands(results).Where(b => b.N > 0))
            Console.WriteLine($"  {b.Band,-8} n={b.N,5}  accuracy {Pct(b.Accuracy),6}  confident-wrong {Pct(b.ConfidentWrongRate),6}");
        Console.WriteLine();
    }

    public static void PrintSplit(IReadOnlyList<Scored> results, string mode)
    {
        var holdOut = results.Where(r => Metrics.IsHoldOut(r.Truth.Hash)).ToList();
        var train = results.Where(r => !Metrics.IsHoldOut(r.Truth.Hash)).ToList();
        foreach (var (name, part) in new[] { ("train 80%", train), ("hold-out 20%", holdOut) })
        {
            if (part.Count == 0) continue;
            var s = Metrics.Compute(part, mode);
            Console.WriteLine($"{name,-13} n={s.N,5}  accuracy {Pct(s.Accuracy)}  confident-wrong {Pct(s.ConfidentWrongRate)}  club macro-F1 {s.MacroF1Club:F3}");
        }
        Console.WriteLine();
    }

    public static void Trace(Scored r)
    {
        Console.WriteLine($"── trace: {r.Truth.Display}");
        Console.WriteLine($"   truth: {r.Truth.Bucket} ← \"{r.Truth.Text}\" ({r.Truth.Source})   bpm {r.Row.Bpm:F1}");
        Console.WriteLine($"   predicted: {r.Prediction.Bucket?.ToString() ?? "none"} ← \"{r.Prediction.Label}\" @ {r.Prediction.Confidence:F2}  {(r.Correct ? "OK" : "WRONG")}");
        foreach (var line in r.Prediction.Evidence) Console.WriteLine("   " + line);
        Console.WriteLine();
    }

    public static void Misclassified(IReadOnlyList<Scored> results, int count)
    {
        Console.WriteLine($"── worst confident-wrongs (top {count}) ──");
        foreach (var r in results.Where(Metrics.IsConfidentWrong).OrderByDescending(r => r.Prediction.Confidence).Take(count))
            Console.WriteLine($"  {r.Prediction.Confidence:F2}  truth {r.Truth.Bucket,-11} pred {r.Prediction.Bucket,-11} bpm {r.Row.Bpm,6:F1}  \"{r.Truth.Text}\"  {r.Truth.Display}");
        Console.WriteLine();
    }

    public static void Compare(Summary now, Summary before)
    {
        static string D(double v, bool pct = true) => (v >= 0 ? "+" : "") + (pct ? (100 * v).ToString("F1", CultureInfo.InvariantCulture) + " pp" : v.ToString("F3", CultureInfo.InvariantCulture));
        Console.WriteLine($"── compare with baseline ({before.Mode}, {before.N} tracks) ──");
        Console.WriteLine($"accuracy {D(now.Accuracy - before.Accuracy)} · coverage {D(now.Coverage - before.Coverage)} · " +
                          $"confident-wrong {D(now.ConfidentWrongRate - before.ConfidentWrongRate)} · club macro-F1 {D(now.MacroF1Club - before.MacroF1Club, pct: false)}");
        foreach (var (name, b) in now.Buckets)
        {
            if (!before.Buckets.TryGetValue(name, out var o)) continue;
            Console.WriteLine($"  {name,-12} F1 {D(b.F1 - o.F1, pct: false)}  recall {D(b.Recall - o.Recall)}  confident-wrong {b.ConfidentWrong - o.ConfidentWrong:+0;-0;0}");
        }
        Console.WriteLine();
    }
}

static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
