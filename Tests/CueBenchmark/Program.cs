// Cue accuracy benchmark — open-work plan item H0 (DOCS/OPEN_WORK_PLAN_2026-09-27.md).
//
// Regenerates ORBIT's auto cues (Engine.Cueing.CueGenerationService, the same code path the app
// uses) for every library track that also has real hand-placed Rekordbox cues, and reports how
// close the generated cues land. Strictly read-only: it opens a COPY of library.db in SQLite
// ReadOnly mode and refuses to touch the live database.
//
// Ground truth: the DJ's own cues are "countdown" cues — 16 and 8 bars before each drop, never on
// the drop itself (confirmed against 312 real tracks, see CueGenerationService.BuildDropApproachCueSet).
// So a real drop is inferred as "second cue of a pair spaced 8 bars apart, plus 8 bars".

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Cueing;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Rekordbox;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var opts = Options.Parse(args);
if (opts is null) { Options.PrintUsage(); return 2; }

// ── Safety: never the live database ─────────────────────────────────────────
string livePath = Path.GetFullPath(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "library.db"));
string dbPath = Path.GetFullPath(opts.DbPath);
if (string.Equals(dbPath, livePath, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Refusing to open the live ORBIT database. Make a copy first (see README.md).");
    return 2;
}
if (!File.Exists(dbPath)) { Console.Error.WriteLine($"Database copy not found: {dbPath}"); return 2; }

// ── --fit-stats: dry run of BeatGridFitter over the whole library copy ─────────
if (args.Contains("--fit-stats"))
{
    var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
    using var statsDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(cs).Options);
    var all = statsDb.AudioFeatures.AsNoTracking().Where(f => f.Bpm > 0 && f.BeatGridJson.Length > 2).ToList();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int fitted = 0, changed = 0, anchorMoved = 0;
    var before = new List<float>(); var after = new List<float>();
    foreach (var f in all)
    {
        float oldBpm = f.Bpm; double oldAnchor = f.DownbeatOffsetSeconds;
        before.Add(oldBpm);
        if (BeatGridFitter.ApplyTo(f, BeatGridFitter.ParseTicks(f.BeatGridJson), BeatGridFitter.StructuralEventsFrom(f)))
        {
            fitted++;
            if (Math.Abs(f.Bpm - oldBpm) >= 0.005) changed++;
            if (Math.Abs(f.DownbeatOffsetSeconds - oldAnchor) >= 0.001) anchorMoved++;
        }
        after.Add(f.Bpm);
    }
    string Top(List<float> xs) => string.Join(", ", xs.GroupBy(x => Math.Round(x, 2)).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} ({g.Count()})"));
    Console.WriteLine($"{all.Count} tracks, {fitted} fitted, {changed} BPM changed, {anchorMoved} downbeat moved, {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"integer BPMs before {before.Count(x => Math.Abs(x - Math.Round(x)) < 1e-4)}, after {after.Count(x => Math.Abs(x - Math.Round(x)) < 1e-4)}");
    Console.WriteLine($"most common before: {Top(before)}");
    Console.WriteLine($"most common after:  {Top(after)}");
    return 0;
}

// ── Reference cues ─────────────────────────────────────────────────────────
List<ReferenceTrack> reference;
if (opts.RekordboxAnalysis)
{
    reference = ReferenceScanner.ScanRekordboxAnalysis();
    Console.WriteLine($"Reference: {reference.Count} tracks with a Rekordbox beat grid + phrase analysis (local ANLZ cache)");
}
else if (opts.ReferenceJson is not null)
{
    reference = JsonSerializer.Deserialize<List<ReferenceTrack>>(File.ReadAllText(opts.ReferenceJson), Json.Options) ?? new();
    Console.WriteLine($"Reference: {reference.Count} tracks from {opts.ReferenceJson}");
}
else
{
    reference = ReferenceScanner.ScanAnlzRoots();
    Console.WriteLine($"Reference: {reference.Count} tracks with saved cues from Rekordbox ANLZ folders");
    if (opts.DumpReference is not null)
    {
        File.WriteAllText(opts.DumpReference, JsonSerializer.Serialize(reference, Json.Options));
        Console.WriteLine($"Wrote reference snapshot to {opts.DumpReference}");
    }
}
if (reference.Count == 0)
{
    Console.Error.WriteLine("No reference cues found. Attach the Rekordbox export drive, or pass --reference <json>.");
    return 1;
}

// ── Library copy ───────────────────────────────────────────────────────────
var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
using var db = new AppDbContext(dbOptions);

var byFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
foreach (var e in db.LibraryEntries.AsNoTracking().Select(e => new { e.UniqueHash, e.FilePath }).ToList())
{
    if (string.IsNullOrWhiteSpace(e.FilePath)) continue;
    var name = Path.GetFileName(e.FilePath.Replace('\\', '/'));
    if (!byFileName.TryGetValue(name, out var list)) byFileName[name] = list = new();
    list.Add(e.UniqueHash);
}

var candidateHashes = reference
    .SelectMany(r => byFileName.TryGetValue(r.FileName, out var h) ? h : new List<string>())
    .ToHashSet();
var features = db.AudioFeatures.AsNoTracking()
    .Where(f => candidateHashes.Contains(f.TrackUniqueHash))
    .ToList()
    .GroupBy(f => f.TrackUniqueHash)
    .ToDictionary(g => g.Key, g => g.First());

// Same construction as the app's DI registrations (App.axaml.cs).
var generator = new CueGenerationService(
    new NoDbContextFactory(),
    new BreakbeatAnalysisStrategy(new DnBTransientDetectionService(NullLogger<DnBTransientDetectionService>.Instance)),
    new FourOnTheFloorAnalysisStrategy());

var rawTicksByHash = new Dictionary<string, double[]>();
Dictionary<string, IReadOnlyList<RekordboxBeat>>? rekordboxGrids = null;
if (opts.RekordboxGrid)
{
    rekordboxGrids = ReferenceScanner.ScanRekordboxGrids();
    Console.WriteLine($"Rekordbox grids available: {rekordboxGrids.Count}");
}

// ── Evaluate ───────────────────────────────────────────────────────────────
var results = new List<TrackResult>();
var skipped = new Dictionary<string, int>();
void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;

foreach (var refTrack in reference)
{
    var refTimes = Metrics.DistinctCueTimes(refTrack.Cues);
    if (refTimes.Count < opts.MinCues) { Skip($"fewer than {opts.MinCues} reference cues"); continue; }
    if (!byFileName.TryGetValue(refTrack.FileName, out var hashes)) { Skip("no library match by filename"); continue; }

    var f = hashes.Select(h => features.GetValueOrDefault(h)).FirstOrDefault(x => x is { Bpm: > 0, TrackDuration: > 0 });
    if (f is null) { Skip("no analysis features / BPM"); continue; }
    if (refTimes[^1] > f.TrackDuration + 5) { Skip("reference cue past track end (different file version)"); continue; }
    // Rekordbox-analysis reference: tracks whose ORBIT cues are themselves built from Rekordbox's
    // phrases would be scored against their own input.
    if (refTrack.Drops is not null && f.PhraseSegmentsSource == "RekordboxPSSI") { Skip("ORBIT already uses Rekordbox phrases as input (circular)"); continue; }

    // --refit: apply BeatGridFitter to the stored ticks in memory (nothing is written), to measure a
    // grid change before re-analysing or recomputing the real library.
    rawTicksByHash[f.TrackUniqueHash] = BeatGridFitter.ParseTicks(f.BeatGridJson);
    if (opts.Refit && !BeatGridFitter.ApplyTo(f, BeatGridFitter.ParseTicks(f.BeatGridJson),
            opts.RefitDownbeat ? BeatGridFitter.StructuralEventsFrom(f) : null))
        Skip("refit: ticks could not be fitted (kept stored grid)");

    // --rekordbox-grid: as the app now does, adopt Rekordbox's own grid when it matches this audio.
    // (Scored against Rekordbox's own analysis this is circular — use it with the hand-cue reference.)
    if (rekordboxGrids is not null && rekordboxGrids.TryGetValue(refTrack.FileName, out var rbGrid))
    {
        var rawTicks = rawTicksByHash.GetValueOrDefault(f.TrackUniqueHash) ?? BeatGridFitter.ParseTicks(f.BeatGridJson);
        if (BeatGridFitter.ApplyRekordboxGrid(f, rbGrid, rawTicks)) Skip("(info) used Rekordbox grid");
        else Skip("(info) Rekordbox grid rejected (didn't match this audio)");
    }

    // Experiment switch: ORBIT_BENCH_PHRASE_SOURCES=EDMFormer,RekordboxPSSI limits which stored phrase
    // sources may drive Path 1 (default: all, as the app does).
    var allowedSources = Environment.GetEnvironmentVariable("ORBIT_BENCH_PHRASE_SOURCES");
    if (!string.IsNullOrEmpty(allowedSources) && !allowedSources.Split(',').Contains(f.PhraseSegmentsSource))
        f.PhraseSegmentsSource = "";

    var analysis = AnalysisPipelineResultBuilder.Build(f);
    double anchor = f.DownbeatOffsetSeconds > 0 ? f.DownbeatOffsetSeconds : 0.0;
    var (cues, path) = generator.GenerateCuesWithPath(f.TrackUniqueHash, analysis, anchor);
    if (cues.Count == 0) { Skip("generator produced no cues"); continue; }

    results.Add(Metrics.Evaluate(refTrack, f, analysis, path, refTimes, cues));

    if (opts.Trace is not null && refTrack.FileName.Contains(opts.Trace, StringComparison.OrdinalIgnoreCase))
    {
        double bar = 240.0 / f.Bpm;
        string Bars(double t) => $"{t,7:F2}s (bar {(t - anchor) / bar + 1,6:F1})";
        Console.WriteLine($"\n── {refTrack.FileName}  {f.Bpm:F1}bpm  anchor {anchor:F2}s  path {path}");
        Console.WriteLine("   Rekordbox cues:");
        foreach (var c in refTrack.Cues.OrderBy(c => c.TimeSeconds))
            Console.WriteLine($"     {Bars(c.TimeSeconds)}  {(c.IsHot ? $"hot {(char)('A' + c.HotCueNumber - 1)}" : "memory"),-7}{(c.IsLoop ? " loop" : "")}");
        Console.WriteLine("   Inferred real drops: " + string.Join(", ", Metrics.InferDrops(refTimes, f.Bpm, f.TrackDuration).Select(Bars)));
        Console.WriteLine("   Generated:");
        foreach (var c in cues) Console.WriteLine($"     {Bars(c.TimestampInSeconds)}  {c.Label}");
    }
}

var report = Report.Build(results, skipped, opts);
report.Print(opts.Worst);

if (opts.CsvOut is not null) { Report.WriteCsv(results, opts.CsvOut); Console.WriteLine($"\nPer-track CSV: {opts.CsvOut}"); }
if (opts.SummaryOut is not null)
{
    File.WriteAllText(opts.SummaryOut, JsonSerializer.Serialize(report.Summary, Json.Options));
    Console.WriteLine($"Summary JSON: {opts.SummaryOut}");
}
if (opts.CompareTo is not null)
{
    var baseline = JsonSerializer.Deserialize<Summary>(File.ReadAllText(opts.CompareTo), Json.Options);
    if (baseline is not null) Report.PrintComparison(baseline, report.Summary);
}
return 0;

// ════════════════════════════════════════════════════════════════════════════

sealed record Options(
    string DbPath, string? ReferenceJson, string? DumpReference, string? CsvOut,
    string? SummaryOut, string? CompareTo, int MinCues, int Worst, string? Trace, bool Refit, bool RefitDownbeat, bool RekordboxAnalysis, bool RekordboxGrid)
{
    public static Options? Parse(string[] args)
    {
        string? db = null, refJson = null, dump = null, csv = null, summary = null, compare = null, trace = null;
        bool refit = false, refitDownbeat = false, rbAnalysis = false, rbGrid = false;
        int minCues = 3, worst = 15;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--db": db = Next(); break;
                case "--reference": refJson = Next(); break;
                case "--dump-reference": dump = Next(); break;
                case "--csv": csv = Next(); break;
                case "--summary": summary = Next(); break;
                case "--compare": compare = Next(); break;
                case "--min-cues": minCues = int.Parse(Next()); break;
                case "--worst": worst = int.Parse(Next()); break;
                case "--trace": trace = Next(); break;
                case "--refit": refit = true; break;
                case "--fit-stats": break; // handled before reference loading
                case "--rekordbox-analysis": rbAnalysis = true; break;
                case "--rekordbox-grid": rbGrid = true; break;
                case "--refit-downbeat": refit = true; refitDownbeat = true; break;
                default: return null;
            }
        }
        return db is null ? null : new Options(db, refJson, dump, csv, summary, compare, minCues, worst, trace, refit, refitDownbeat, rbAnalysis, rbGrid);
    }

    public static void PrintUsage() => Console.WriteLine("""
        Usage: dotnet run --project Tests/CueBenchmark -- --db <library.db COPY> [options]
          --reference <json>       Reference cues from a snapshot (default: scan Rekordbox ANLZ folders)
          --dump-reference <json>  Save the scanned ANLZ cues as a snapshot for later runs
          --min-cues <n>           Only score tracks with at least n distinct reference cues (default 3)
          --csv <file>             Per-track results
          --summary <file>         Summary JSON (use as a baseline for --compare)
          --compare <file>         Print deltas against a previous --summary baseline
          --worst <n>              How many worst tracks to list (default 15)
          --trace <text>           Print reference vs generated cues for tracks whose filename contains text
          --refit                  Re-fit BPM/grid from stored beat ticks (BeatGridFitter) in memory first
          --refit-downbeat         --refit, and also re-pick the downbeat from stored structural events
          --fit-stats              Dry-run the fitter over every analysed track in the copy (no reference needed)
          --rekordbox-analysis     Reference = Rekordbox's own beat grid + phrase analysis from its local ANLZ cache
          --rekordbox-grid         Adopt Rekordbox's grid where it matches the audio (as the app does); use with hand cues
        """);
}

static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// Snapshot format matches the earlier C:\tmp\rekordbox_cues.json dump (snake_case).
sealed class ReferenceTrack
{
    public string SourcePath { get; set; } = "";
    [JsonPropertyName("filename")] public string FileName { get; set; } = "";
    public List<ReferenceCue> Cues { get; set; } = new();
    /// <summary>Known drop times (Rekordbox-analysis mode). Null = infer from countdown cue pairs.</summary>
    public List<double>? Drops { get; set; }
    /// <summary>Known BPM (Rekordbox's own grid). Null = infer from cue spacing.</summary>
    public double? Bpm { get; set; }
}

sealed class ReferenceCue
{
    public bool IsHot { get; set; }
    public int HotCueNumber { get; set; }
    public bool IsLoop { get; set; }
    public double TimeSeconds { get; set; }
    public double? LoopEndSeconds { get; set; }
    public string? Comment { get; set; }
    public int? ColorId { get; set; }
}

static class ReferenceScanner
{
    /// <summary>Filename → Rekordbox PQTZ beat grid, from every ANLZ root's .DAT files.</summary>
    public static Dictionary<string, IReadOnlyList<RekordboxBeat>> ScanRekordboxGrids()
    {
        var grids = new Dictionary<string, IReadOnlyList<RekordboxBeat>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in RekordboxAnlzLocator.FindRoots())
            foreach (var dat in Directory.EnumerateFiles(root, "*.DAT", SearchOption.AllDirectories))
            {
                RekordboxAnlzResult? d;
                try { d = RekordboxAnlzParser.TryParse(File.ReadAllBytes(dat)); } catch { continue; }
                if (d?.SourcePath is { Length: > 0 } src && d.BeatGrid.Count > 0)
                    grids.TryAdd(Path.GetFileName(src.Replace('\\', '/')), d.BeatGrid);
            }
        return grids;
    }

    /// <summary>
    /// Rekordbox's own analysis from its local cache (%AppData%\Pioneer\rekordbox\share\PIONEER\USBANLZ):
    /// each track folder has ANAL0000.DAT (PPTH source path + PQTZ beat grid) and ANAL0000.EXT (PSSI
    /// phrases). Produces, per track: phrase starts as reference "cues" (they sit on Rekordbox's
    /// phrase lines, like a DJ's phrase markers), Chorus-run starts as the real drops, and Rekordbox's
    /// BPM. Automatic analysis, not hand-placed — but independent of ORBIT and industry-standard.
    /// </summary>
    public static List<ReferenceTrack> ScanRekordboxAnalysis()
    {
        var result = new List<ReferenceTrack>();
        foreach (var root in RekordboxAnlzLocator.FindRoots())
        {
            foreach (var dat in Directory.EnumerateFiles(root, "*.DAT", SearchOption.AllDirectories))
            {
                var ext = Path.ChangeExtension(dat, ".EXT");
                if (!File.Exists(ext)) continue;
                RekordboxAnlzResult? d, x;
                try { d = RekordboxAnlzParser.TryParse(File.ReadAllBytes(dat)); x = RekordboxAnlzParser.TryParse(File.ReadAllBytes(ext)); }
                catch { continue; }
                if (d?.SourcePath is not { Length: > 0 } source || d.BeatGrid.Count < 64 || x is null || x.Phrases.Count < 3) continue;

                var beats = d.BeatGrid;
                double? TimeOfBeat(int beat) => beat >= 1 && beat <= beats.Count ? beats[beat - 1].TimeSeconds : null;
                // Only Rekordbox's "high" mood uses the EDM phrase vocabulary (Intro/Up/Down/Chorus/Outro),
                // where Chorus is the drop. In mid/low mood (pop, hip-hop, disco) Chorus is a sung
                // chorus, not a drop — those tracks still count for grid metrics, but get no drop reference.
                int dropKind = x.Mood == 1 ? 5 : -1;

                var phrases = x.Phrases.OrderBy(p => p.Beat).ToList();
                var starts = phrases.Select(p => TimeOfBeat(p.Beat)).Where(t => t.HasValue).Select(t => t!.Value).ToList();
                var drops = new List<double>();
                for (int i = 0; i < phrases.Count; i++)
                {
                    if (phrases[i].Kind != dropKind) continue;
                    if (i > 0 && phrases[i - 1].Kind == dropKind) continue; // only the start of each chorus run
                    if (TimeOfBeat(phrases[i].Beat) is double t) drops.Add(t);
                }

                result.Add(new ReferenceTrack
                {
                    SourcePath = source,
                    FileName = Path.GetFileName(source.Replace('\\', '/')),
                    Cues = starts.Select(t => new ReferenceCue { TimeSeconds = t }).ToList(),
                    Drops = drops,
                    Bpm = beats.Select(b => b.Bpm).OrderBy(b => b).ElementAt(beats.Count / 2),
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Reads saved hot/memory cues from every Rekordbox ANLZ root. Real hand-placed cues only land in
    /// a device export cache (&lt;drive&gt;\PIONEER\USBANLZ), so the export drive must be attached.
    /// </summary>
    public static List<ReferenceTrack> ScanAnlzRoots()
    {
        var bySource = new Dictionary<string, ReferenceTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in RekordboxAnlzLocator.FindRoots())
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(p => p.EndsWith(".EXT", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.EndsWith(".EXT", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ToList(); }
            catch { continue; }

            foreach (var file in files)
            {
                RekordboxAnlzResult? parsed;
                try { parsed = RekordboxAnlzParser.TryParse(File.ReadAllBytes(file)); } catch { continue; }
                if (parsed?.SourcePath is not { Length: > 0 } source || parsed.CuePoints.Count == 0) continue;
                if (bySource.ContainsKey(source)) continue; // .EXT (PCO2, a superset) wins over .DAT

                bySource[source] = new ReferenceTrack
                {
                    SourcePath = source,
                    FileName = Path.GetFileName(source.Replace('\\', '/')),
                    Cues = parsed.CuePoints.Select(c => new ReferenceCue
                    {
                        IsHot = c.IsHotCue, HotCueNumber = c.HotCueNumber, IsLoop = c.IsLoop,
                        TimeSeconds = c.TimeSeconds, LoopEndSeconds = c.LoopEndSeconds,
                        Comment = c.Comment, ColorId = c.ColorId,
                    }).ToList(),
                };
            }
        }
        return bySource.Values.ToList();
    }
}

/// <summary>GenerateCues never touches the database; only GenerateAndPersistCuesAsync does.</summary>
sealed class NoDbContextFactory : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => throw new InvalidOperationException("The benchmark never writes cues.");
}

sealed record TrackResult(
    string FileName, string Hash, string Genre, GenreFamily Family, CueGenerationPath Path, double Bpm,
    int ReferenceCueCount, int InferredDropCount,
    double?[] DropErrors,          // signed seconds, generated minus real, per inferred drop (index 0 = Drop 1)
    double[] CueCoverageErrors,    // signed seconds, nearest generated cue minus each reference cue
    double[] ApproachErrors,       // signed seconds, each generated countdown cue vs nearest reference cue
    double[] BeatPhaseErrors,      // per reference cue: offset from ORBIT's nearest grid beat, in beats (±0.5)
    double[] BarPhaseErrors,       // per reference cue: offset from ORBIT's nearest bar line, in beats (±2)
    double[] PhrasePhaseErrors,    // per reference cue: offset from ORBIT's nearest 8-bar phrase line, in bars (±4)
    double[] EarlyBeatPhase,       // beat phase of cues in the first 90s
    double[] LateBeatPhase,        // beat phase of cues after 180s — worse than early means tempo drift
    double? CueImpliedBpm);        // BPM implied by whole-bar spacing between hand cues (the DJ's Rekordbox grid)

static class Metrics
{
    /// <summary>Distinct cue positions: hot and memory cues at the same spot are the same mark.</summary>
    public static List<double> DistinctCueTimes(List<ReferenceCue> cues)
    {
        var times = cues.Select(c => c.TimeSeconds).Where(t => t >= 0).OrderBy(t => t).ToList();
        var distinct = new List<double>();
        foreach (var t in times)
            if (distinct.Count == 0 || t - distinct[^1] > 0.15) distinct.Add(t);
        return distinct;
    }

    /// <summary>
    /// Real drops implied by the DJ's countdown convention: a pair of cues 32 beats (8 bars) apart
    /// means the drop is 32 beats after the second one. Spacing tolerance is one beat, so small
    /// BPM/grid differences between Rekordbox and ORBIT don't hide a real pair.
    /// </summary>
    public static List<double> InferDrops(List<double> refTimes, double bpm, double duration)
    {
        double beat = 60.0 / bpm;
        var drops = new List<double>();
        for (int i = 0; i < refTimes.Count; i++)
            for (int j = i + 1; j < refTimes.Count; j++)
            {
                double gap = refTimes[j] - refTimes[i];
                if (Math.Abs(gap - 32 * beat) > beat) continue;
                double drop = refTimes[j] + 32 * beat;
                if (drop < duration && drops.All(d => Math.Abs(d - drop) > 8 * beat)) drops.Add(drop);
            }
        drops.Sort();
        return drops;
    }

    public static TrackResult Evaluate(
        ReferenceTrack refTrack, AudioFeaturesEntity f, AnalysisPipelineResult analysis,
        CueGenerationPath path, List<double> refTimes, List<CuePointEntity> cues)
    {
        double bpm = f.Bpm;
        var genTimes = cues.Select(c => c.TimestampInSeconds).OrderBy(t => t).ToList();
        var genDrops = cues.Where(c => c.Type == CuePointType.Drop).Select(c => c.TimestampInSeconds).OrderBy(t => t).ToList();
        var genApproach = cues.Where(c => c.Type == CuePointType.Build).Select(c => c.TimestampInSeconds).ToList();

        var realDrops = refTrack.Drops ?? InferDrops(refTimes, bpm, f.TrackDuration);
        var dropErrors = realDrops.Take(2)
            .Select(d => genDrops.Count == 0 ? (double?)null : Nearest(genDrops, d) - d)
            .ToArray();

        return new TrackResult(
            refTrack.FileName, f.TrackUniqueHash, analysis.Genre ?? "",
            GenreFamilyClassifier.Classify(analysis.Genre, (float)bpm).Family, path, bpm,
            refTimes.Count, realDrops.Count, dropErrors,
            refTimes.Select(t => Nearest(genTimes, t) - t).ToArray(),
            genApproach.Select(t => t - Nearest(refTimes, t)).ToArray(),
            // Grid alignment: hand cues sit on the DJ's own beat/bar/phrase lines, so their offset
            // from ORBIT's constant grid (BPM + downbeat anchor, the grid every cue gets snapped to)
            // shows whether ORBIT's grid agrees. Cues before the anchor (e.g. a 0.0s load cue) are skipped.
            GridPhase(refTimes, f, 1), GridPhase(refTimes, f, 4), GridPhase(refTimes, f, 32).Select(b => b / 4).ToArray(),
            GridPhase(refTimes.Where(t => t <= 90).ToList(), f, 1),
            GridPhase(refTimes.Where(t => t >= 180).ToList(), f, 1),
            refTrack.Bpm ?? ImpliedBpm(refTimes, bpm));
    }

    /// <summary>
    /// Hand cues sit on the DJ's Rekordbox grid, so two cues k whole bars apart imply BPM = 240·k/gap.
    /// Only pairs ≥4 bars apart are used (short gaps are too imprecise); median over all pairs.
    /// </summary>
    static double? ImpliedBpm(List<double> refTimes, double orbitBpm)
    {
        var cues = refTimes.Where(t => t > 1.0).ToList();
        double bar = 240.0 / orbitBpm;
        var implied = new List<double>();
        for (int i = 0; i < cues.Count; i++)
            for (int j = i + 1; j < cues.Count; j++)
            {
                double gap = cues[j] - cues[i];
                long k = (long)Math.Round(gap / bar);
                if (k >= 4) implied.Add(240.0 * k / gap);
            }
        if (implied.Count == 0) return null;
        implied.Sort();
        return implied[implied.Count / 2];
    }

    /// <summary>Wrapped offset (in beats) of each cue from the nearest multiple of <paramref name="periodBeats"/> on ORBIT's grid.</summary>
    static double[] GridPhase(List<double> refTimes, AudioFeaturesEntity f, int periodBeats)
    {
        double beat = 60.0 / f.Bpm;
        double anchor = f.DownbeatOffsetSeconds > 0 ? f.DownbeatOffsetSeconds : 0.0;
        return refTimes.Where(t => t > 1.0)
            .Select(t =>
            {
                double beats = (t - anchor) / beat;
                double wrapped = beats - Math.Round(beats / periodBeats) * periodBeats;
                return wrapped;
            })
            .ToArray();
    }

    static double Nearest(List<double> sorted, double t) => sorted.MinBy(x => Math.Abs(x - t));
}

/// <summary>Distribution of absolute errors in musical units (beats at each track's own BPM).</summary>
sealed record Bucketed(int N, double Within50ms, double WithinHalfBeat, double Within1Bar, double Within4Bars,
    double MedianAbsSeconds, double P75AbsSeconds, double MedianSignedSeconds)
{
    public static Bucketed From(IEnumerable<(double Err, double Bpm)> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return new Bucketed(0, 0, 0, 0, 0, 0, 0, 0);
        double Pct(Func<(double Err, double Bpm), bool> p) => 100.0 * list.Count(p) / list.Count;
        var abs = list.Select(x => Math.Abs(x.Err)).OrderBy(x => x).ToList();
        var signed = list.Select(x => x.Err).OrderBy(x => x).ToList();
        return new Bucketed(list.Count,
            Pct(x => Math.Abs(x.Err) <= 0.05),
            Pct(x => Math.Abs(x.Err) <= 0.5 * 60 / x.Bpm),
            Pct(x => Math.Abs(x.Err) <= 4 * 60 / x.Bpm),
            Pct(x => Math.Abs(x.Err) <= 16 * 60 / x.Bpm),
            Percentile(abs, 0.5), Percentile(abs, 0.75), Percentile(signed, 0.5));
    }

    static double Percentile(List<double> sorted, double p)
    {
        double idx = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }
}

sealed class Summary
{
    public DateTime GeneratedAt { get; set; }
    public int TracksScored { get; set; }
    public Dictionary<string, int> Skipped { get; set; } = new();
    public Bucketed Drop1 { get; set; } = null!;
    public Bucketed Drop2 { get; set; } = null!;
    public Bucketed CueCoverage { get; set; } = null!;
    public Bucketed CountdownCues { get; set; } = null!;
    public Dictionary<string, Bucketed> DropsByPath { get; set; } = new();
    public Dictionary<string, Bucketed> DropsByFamily { get; set; } = new();
    public GridAlignment Grid { get; set; } = null!;
}

/// <summary>How well ORBIT's constant beat grid (BPM + downbeat anchor) lines up with the DJ's hand cues.</summary>
sealed record GridAlignment(
    int Cues,
    double OnBeatPct,                  // within ¼ beat of an ORBIT beat
    double OnBarPct,                   // within ¼ beat of an ORBIT bar line (downbeat agrees)
    double OnPhrasePct,                // within ¼ beat of an ORBIT 8-bar phrase line
    Dictionary<string, double> BarPhaseOffBeatsPct, // cues on a beat but not the bar line: off by 1/2/3 beats
    double OnBeatFirst90sPct, double OnBeatAfter180sPct, // tempo drift: alignment early vs late in the track
    int TracksAllCuesOnBar, int TracksScored,
    int BpmTracks, double BpmWithin01Pct, double BpmWithin05Pct, double BpmMedianAbsError)
{
    public static GridAlignment From(List<TrackResult> results)
    {
        const double Tol = 0.25;
        var beat = results.SelectMany(r => r.BeatPhaseErrors).ToList();
        var bar = results.SelectMany(r => r.BarPhaseErrors).ToList();
        var phrase = results.SelectMany(r => r.PhrasePhaseErrors.Select(b => b * 4)).ToList(); // back to beats
        double Pct(IEnumerable<double> xs, Func<double, bool> p) { var l = xs.ToList(); return l.Count == 0 ? 0 : 100.0 * l.Count(p) / l.Count; }

        var onBeatOffBar = bar.Where(b => Math.Abs(b - Math.Round(b)) <= Tol && Math.Abs(b) > Tol).ToList();
        var offBy = onBeatOffBar.GroupBy(b => ((int)Math.Round(b) % 4 + 4) % 4)
            .ToDictionary(g => $"+{g.Key} beats", g => 100.0 * g.Count() / Math.Max(1, bar.Count));

        var bpmErr = results.Where(r => r.CueImpliedBpm.HasValue).Select(r => Math.Abs(r.Bpm - r.CueImpliedBpm!.Value)).ToList();
        var early = results.SelectMany(r => r.EarlyBeatPhase).ToList();
        var late = results.SelectMany(r => r.LateBeatPhase).ToList();

        return new GridAlignment(
            beat.Count,
            Pct(beat, b => Math.Abs(b) <= Tol),
            Pct(bar, b => Math.Abs(b) <= Tol),
            Pct(phrase, b => Math.Abs(b) <= Tol),
            offBy,
            Pct(early, b => Math.Abs(b) <= Tol),
            Pct(late, b => Math.Abs(b) <= Tol),
            results.Count(r => r.BarPhaseErrors.Length > 0 && r.BarPhaseErrors.All(b => Math.Abs(b) <= Tol)),
            results.Count(r => r.BarPhaseErrors.Length > 0),
            bpmErr.Count, Pct(bpmErr, e => e <= 0.1), Pct(bpmErr, e => e <= 0.5),
            bpmErr.Count == 0 ? 0 : bpmErr.OrderBy(x => x).ElementAt(bpmErr.Count / 2));
    }
}

sealed class Report
{
    public Summary Summary { get; }
    readonly List<TrackResult> _results;

    Report(Summary s, List<TrackResult> r) { Summary = s; _results = r; }

    static IEnumerable<(double, double)> Drops(IEnumerable<TrackResult> rs, int? index = null) =>
        rs.SelectMany(r => r.DropErrors.Select((e, i) => (e, i, r.Bpm)))
          .Where(x => x.e.HasValue && (index is null || x.i == index))
          .Select(x => (x.e!.Value, x.Bpm));

    public static Report Build(List<TrackResult> results, Dictionary<string, int> skipped, Options opts) =>
        new(new Summary
        {
            GeneratedAt = DateTime.UtcNow,
            TracksScored = results.Count,
            Skipped = skipped,
            Drop1 = Bucketed.From(Drops(results, 0)),
            Drop2 = Bucketed.From(Drops(results, 1)),
            CueCoverage = Bucketed.From(results.SelectMany(r => r.CueCoverageErrors.Select(e => (e, r.Bpm)))),
            CountdownCues = Bucketed.From(results.SelectMany(r => r.ApproachErrors.Select(e => (e, r.Bpm)))),
            DropsByPath = results.GroupBy(r => r.Path.ToString()).ToDictionary(g => g.Key, g => Bucketed.From(Drops(g))),
            DropsByFamily = results.GroupBy(r => r.Family.ToString()).ToDictionary(g => g.Key, g => Bucketed.From(Drops(g))),
            Grid = GridAlignment.From(results),
        }, results);

    public void Print(int worst)
    {
        var s = Summary;
        Console.WriteLine($"\nScored {s.TracksScored} tracks. Skipped: " +
            (s.Skipped.Count == 0 ? "none" : string.Join(", ", s.Skipped.Select(k => $"{k.Value} {k.Key}"))));
        var g = s.Grid;
        Console.WriteLine($"\nGrid alignment vs {g.Cues} reference markers (hand cues, or Rekordbox phrase starts; tolerance ¼ beat):");
        Console.WriteLine($"  on an ORBIT beat        {g.OnBeatPct,5:F0}%   (first 90s {g.OnBeatFirst90sPct:F0}%, after 180s {g.OnBeatAfter180sPct:F0}% — a drop-off means tempo drift)");
        Console.WriteLine($"  on an ORBIT bar line    {g.OnBarPct,5:F0}%   on-beat-but-wrong-downbeat: " +
            (g.BarPhaseOffBeatsPct.Count == 0 ? "none" : string.Join(", ", g.BarPhaseOffBeatsPct.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value:F0}%"))));
        Console.WriteLine($"  on an ORBIT 8-bar line  {g.OnPhrasePct,5:F0}%");
        Console.WriteLine($"  tracks where every cue is on an ORBIT bar line: {g.TracksAllCuesOnBar}/{g.TracksScored}");
        Console.WriteLine($"  BPM vs cue-implied BPM ({g.BpmTracks} tracks): within 0.1 {g.BpmWithin01Pct:F0}%, within 0.5 {g.BpmWithin05Pct:F0}%, median error {g.BpmMedianAbsError:F2}");

        Console.WriteLine($"\nDrop scoring (Rekordbox Chorus-run starts, or — for hand cues — inferred from cue pairs 8 bars apart; see README). " +
                          $"Tracks with a reference drop: {_results.Count(r => r.InferredDropCount > 0)}");

        Console.WriteLine("\n                          n   ≤50ms  ≤½beat  ≤1bar  ≤4bars   median|err|  p75|err|  median signed");
        Row("Drop 1", s.Drop1);
        Row("Drop 2", s.Drop2);
        Row("Countdown cues (16/8)", s.CountdownCues);
        Row("All reference cues", s.CueCoverage);
        Console.WriteLine("\nDrops by generation path:");
        foreach (var (k, v) in s.DropsByPath.OrderByDescending(x => x.Value.N)) Row("  " + k, v);
        Console.WriteLine("\nDrops by genre family:");
        foreach (var (k, v) in s.DropsByFamily.OrderByDescending(x => x.Value.N)) Row("  " + k, v);

        var worstTracks = _results
            .Select(r => (r, err: r.DropErrors.Where(e => e.HasValue).Select(e => Math.Abs(e!.Value)).DefaultIfEmpty(-1).Max()))
            .Where(x => x.err >= 0).OrderByDescending(x => x.err).Take(worst).ToList();
        if (worstTracks.Count > 0)
        {
            Console.WriteLine($"\nWorst {worstTracks.Count} tracks by drop error:");
            foreach (var (r, err) in worstTracks)
                Console.WriteLine($"  {err,7:F1}s  {r.Path,-17} {r.Family,-14} {r.Bpm,5:F0}bpm  {Truncate(r.FileName, 60)}");
        }
    }

    static void Row(string label, Bucketed b) =>
        Console.WriteLine($"{label,-22} {b.N,5}  {b.Within50ms,5:F0}%  {b.WithinHalfBeat,5:F0}%  {b.Within1Bar,5:F0}%  {b.Within4Bars,5:F0}%" +
                          $"   {b.MedianAbsSeconds,9:F2}s  {b.P75AbsSeconds,8:F2}s  {b.MedianSignedSeconds,+10:F2}s");

    public static void PrintComparison(Summary before, Summary after)
    {
        Console.WriteLine($"\nChange vs baseline from {before.GeneratedAt:yyyy-MM-dd HH:mm} UTC (positive % = better, negative seconds = better):");
        void Delta(string label, Bucketed a, Bucketed b) =>
            Console.WriteLine($"  {label,-22} n {a.N}→{b.N}   ≤½beat {b.WithinHalfBeat - a.WithinHalfBeat:+0;-0;0}pt   ≤1bar {b.Within1Bar - a.Within1Bar:+0;-0;0}pt   " +
                              $"≤4bars {b.Within4Bars - a.Within4Bars:+0;-0;0}pt   median|err| {b.MedianAbsSeconds - a.MedianAbsSeconds:+0.00;-0.00;0.00}s");
        Delta("Drop 1", before.Drop1, after.Drop1);
        Delta("Drop 2", before.Drop2, after.Drop2);
        Delta("Countdown cues", before.CountdownCues, after.CountdownCues);
        Delta("All reference cues", before.CueCoverage, after.CueCoverage);
    }

    public static void WriteCsv(List<TrackResult> results, string path)
    {
        var sb = new StringBuilder("file,hash,genre,family,path,bpm,cue_implied_bpm,beat_phase_median,bar_phase_median,ref_cues,inferred_drops,drop1_err_s,drop2_err_s,coverage_median_abs_s\n");
        foreach (var r in results)
        {
            string D(int i) => i < r.DropErrors.Length && r.DropErrors[i].HasValue ? r.DropErrors[i]!.Value.ToString("F3") : "";
            var cov = r.CueCoverageErrors.Select(Math.Abs).OrderBy(x => x).ToList();
            string covMed = cov.Count == 0 ? "" : cov[cov.Count / 2].ToString("F3");
            static string Med(double[] xs) => xs.Length == 0 ? "" : xs.OrderBy(x => x).ElementAt(xs.Length / 2).ToString("F2");
            sb.AppendLine(string.Join(",", Csv(r.FileName), Csv(r.Hash), Csv(r.Genre), r.Family, r.Path, r.Bpm.ToString("F2"),
                r.CueImpliedBpm?.ToString("F2") ?? "", Med(r.BeatPhaseErrors), Med(r.BarPhaseErrors),
                r.ReferenceCueCount, r.InferredDropCount, D(0), D(1), covMed));
        }
        File.WriteAllText(path, sb.ToString());
    }

    static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
