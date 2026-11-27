// Auto-mix benchmark, Part A (plan level) — #599, epic #596.
//
// For every consecutive pair of downloaded tracks in your playlists, asks the same code real playback
// uses (PlaybackTransitionDecider → TransitionResolver → TransitionPlanService/TransitionPlanner) how the
// pair mixes, applies the player's own timing rules (AudioPlayerService.LatestMixStart, transition
// length, TempoSyncRatio), and scores where the mix starts and enters against both tracks' beat grids.
// Runs against a COPY of library.db (ORBIT_DB_PATH) and refuses the live database.

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Transitions;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Sets;
using SLSKDONET.Services.Transitions;

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
// Every `new AppDbContext()` in the services below now opens the copy.
Environment.SetEnvironmentVariable(OrbitPaths.DbPathEnvironmentVariable, dbPath);

// ── The production decision path ────────────────────────────────────────────
var factory = new CopyDbFactory();
var cues = new CuePointService(factory, NullLogger<CuePointService>.Instance);
// TransitionPlanService only calls DatabaseService.GetAudioFeaturesByHashAsync, which opens its own
// AppDbContext; none of the constructor's services are touched on that path.
var database = new DatabaseService(NullLogger<DatabaseService>.Instance, null!, null!, null!);
var planService = new TransitionPlanService(database, cues);
var resolver = new TransitionResolver(factory, new TransitionHistoryService(factory), NullLogger<TransitionResolver>.Instance, planService);
var decider = new PlaybackTransitionDecider(resolver);

// ── Playlists → consecutive playable pairs (the order playback queues them) ──
List<PlaylistTrackEntity> rows;
Dictionary<Guid, string> playlistNames;
Dictionary<string, AudioFeaturesEntity> features;
using (var db = new AppDbContext())
{
    playlistNames = db.Projects.AsNoTracking().Where(p => !p.IsDeleted).ToDictionary(p => p.Id, p => p.SourceTitle);
    rows = db.PlaylistTracks.AsNoTracking()
        .Where(t => t.Status == TrackStatus.Downloaded && t.ResolvedFilePath != null && t.ResolvedFilePath != "")
        .ToList()
        .Where(t => playlistNames.ContainsKey(t.PlaylistId))
        .ToList();
    var hashes = rows.Select(r => r.TrackUniqueHash).Distinct().ToList();
    features = db.AudioFeatures.AsNoTracking().Where(f => hashes.Contains(f.TrackUniqueHash)).ToList()
        .GroupBy(f => f.TrackUniqueHash).ToDictionary(g => g.Key, g => g.First());
}

var playlists = rows.GroupBy(r => r.PlaylistId)
    .Select(g => g.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToList())
    .Where(l => l.Count >= 2)
    .OrderByDescending(l => l.Count)
    .Take(opts.MaxPlaylists)
    .ToList();

var results = new List<PairResult>();
var labelled = new List<(PairResult Result, string Text)>();
foreach (var list in playlists)
{
    for (int i = 0; i + 1 < list.Count; i++)
    {
        var o = list[i];
        var n = list[i + 1];
        if (o.TrackUniqueHash == n.TrackUniqueHash) continue;
        var decision = await decider.DecideAsync(ToPair(o), ToPair(n));
        results.Add(PairScorer.Score(o, n, decision, features.GetValueOrDefault(o.TrackUniqueHash)));
        labelled.Add((results[^1], results[^1].Describe(o, n)));
        if (opts.Trace is { } trace && ($"{o.Artist} {o.Title}".Contains(trace, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine(results[^1].Describe(o, n));
    }
}

if (results.Count == 0)
{
    Console.Error.WriteLine("No playlist with two or more downloaded tracks in this copy.");
    return 1;
}

var summary = Report.Build(results, playlists.Count);
Report.Print(summary, results);
if (opts.Worst > 0)
{
    Console.WriteLine();
    Console.WriteLine($"Worst {opts.Worst} beat-matched pairs by phase error:");
    foreach (var (_, text) in labelled.Where(x => x.Result.PhaseErrorMs.HasValue && x.Result.Path != PlaybackTransitionPath.EchoOutFallback)
                 .OrderByDescending(x => x.Result.PhaseErrorMs).Take(opts.Worst))
        Console.WriteLine(text);
}
if (opts.SummaryPath is { } sp)
{
    File.WriteAllText(sp, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nWrote summary to {sp}");
}
if (opts.ComparePath is { } cp)
    Report.Compare(JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(cp))!, summary);
return 0;

// The queue's view of a track — the same fields PlayerViewModel.ToPairTrack reads.
static PlaybackPairTrack ToPair(PlaylistTrackEntity t) => new(
    t.Id, t.PlaylistId, t.TrackUniqueHash,
    !string.IsNullOrEmpty(t.MusicalKey) ? SLSKDONET.Utils.KeyConverter.ToCamelot(t.MusicalKey) : "—",
    t.Energy ?? 0.0,
    t.BPM is > 0 ? t.BPM : null);

sealed class CopyDbFactory : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new();
}

/// <summary>One pair as playback would play it, scored.</summary>
sealed record PairResult(
    PlaybackTransitionPath Path,
    string Preset,
    string FallbackReason,
    bool Clamped,
    double TransitionSeconds,
    double? StartBeatErrorMs,
    double? EntryBeatErrorMs,
    double? PhaseErrorMs,
    bool? StartOnBar,
    bool? EntryOnBar,
    bool? StartOnPhrase,
    bool? BarsAligned,
    string Tempo,
    string Key,
    string Detail)
{
    public string Describe(PlaylistTrackEntity o, PlaylistTrackEntity n) =>
        $"{o.Artist} - {o.Title}  →  {n.Artist} - {n.Title}\n" +
        $"   {Path} {Preset} {FallbackReason} clamp={Clamped} len={TransitionSeconds:0.0}s start±{StartBeatErrorMs:0}ms entry±{EntryBeatErrorMs:0}ms " +
        $"phase±{PhaseErrorMs:0}ms startBar={StartOnBar} entryBar={EntryOnBar} barsAligned={BarsAligned} tempo={Tempo} key={Key}\n   {Detail}";
}

static class PairScorer
{
    public static PairResult Score(PlaylistTrackEntity o, PlaylistTrackEntity n, PlaybackTransitionDecision d, AudioFeaturesEntity? outFeatures)
    {
        // AudioPlayerService.TransitionSeconds: bars follow the outgoing track's tempo when known.
        double barBpm = d.OutgoingBpm is > 0 ? d.OutgoingBpm.Value : d.IncomingBpm;
        double transitionSeconds = d.Model.DurationBeats * 60.0 / Math.Max(1, barBpm);

        // The player compares against the file's real length; the analysed duration stands in here.
        double outDuration = d.OutgoingStructure?.DurationSeconds
            ?? (outFeatures?.TrackDuration is > 0 ? outFeatures.TrackDuration : o.ActualDurationSeconds ?? (o.CanonicalDuration ?? 0) / 1000.0);
        double start = d.SourceTriggerSeconds is { } src
            ? SLSKDONET.Services.AudioPlayerService.LatestMixStart(src, outDuration, transitionSeconds)
            : Math.Max(0, outDuration - transitionSeconds);
        bool clamped = d.SourceTriggerSeconds is { } s0 && start < s0 - 0.001;
        double entry = d.TargetTriggerSeconds ?? 0;

        var outGrid = d.OutgoingStructure?.BeatGrid;
        var inGrid = d.IncomingStructure?.BeatGrid;
        double? startErr = null, entryErr = null, phaseErr = null;
        bool? startBar = null, entryBar = null, startPhrase = null, barsAligned = null;
        if (outGrid is { Count: > 1 } && d.OutgoingStructure is { } os)
        {
            startErr = Grid.NearestTickErrorMs(outGrid, start);
            double beatsFromDownbeat = Grid.BeatPosition(outGrid, start) - Grid.BeatPosition(outGrid, os.FirstDownbeat);
            startBar = Grid.OnMultiple(beatsFromDownbeat, 4);
            startPhrase = Grid.OnMultiple(beatsFromDownbeat, 32);
            if (inGrid is { Count: > 1 } && d.IncomingStructure is { } ins)
            {
                entryErr = Grid.NearestTickErrorMs(inGrid, entry);
                double inBeats = Grid.BeatPosition(inGrid, entry) - Grid.BeatPosition(inGrid, ins.FirstDownbeat);
                entryBar = Grid.OnMultiple(inBeats, 4);
                // A half/double-time pair (87 vs 174) is compared on the faster track's beats: one tick
                // of the half-time grid spans two of them.
                double raw = os.Bpm > 0 && ins.Bpm > 0 ? os.Bpm / ins.Bpm : 1;
                double outScale = raw < 0.55 ? 2 : 1, inScale = raw > 1.8 ? 2 : 1;
                double outBeats = beatsFromDownbeat * outScale, inBeatsScaled = inBeats * inScale;
                double beatMs = 60000.0 / Math.Max(1, os.LocalBpmAt(start) * outScale);
                // Kick-against-kick at the moment the incoming deck starts (both decks tempo-synced):
                // the fractional-beat offset between the two decks.
                double diff = Grid.Wrap(Grid.Frac(outBeats) - Grid.Frac(inBeatsScaled));
                phaseErr = Math.Abs(diff) * beatMs;
                barsAligned = Grid.Mod(Math.Round(outBeats), 4) == Grid.Mod(Math.Round(inBeatsScaled), 4);
            }
        }

        string fallbackReason = d.Path != PlaybackTransitionPath.EchoOutFallback ? ""
            : d.OutgoingStructure is null || d.IncomingStructure is null ? "no analysis"
            : d.OutgoingStructure.BeatGrid is not { Count: > 0 } || d.IncomingStructure.BeatGrid is not { Count: > 0 } ? "no grid"
            : "tempo jump";

        return new PairResult(d.Path, d.PresetName, fallbackReason, clamped, transitionSeconds,
            startErr, entryErr, phaseErr, startBar, entryBar, startPhrase, barsAligned,
            TempoClass(d.OutgoingBpm, d.IncomingBpm), KeyClass(ToCamelot(o.MusicalKey), ToCamelot(n.MusicalKey)),
            Detail(d, start, entry));
    }

    static string Detail(PlaybackTransitionDecision d, double start, double entry)
    {
        static string Side(TrackStructure? t) => t is null ? "-" :
            $"bpm={t.Bpm:0.00} downbeat={t.FirstDownbeat:0.000} intro={t.IntroCue:0.000} drop1={t.Drop1:0.0} ticks[0..2]={string.Join("/", (t.BeatGrid ?? Array.Empty<double>()).Take(3).Select(x => x.ToString("0.000")))} nTicks={t.BeatGrid?.Count ?? 0}";
        return $"start={start:0.000} entry={entry:0.000} | out {Side(d.OutgoingStructure)} | in {Side(d.IncomingStructure)} | {d.Plan?.Reason}";
    }

    static string? ToCamelot(string? key) => string.IsNullOrEmpty(key) ? null : SLSKDONET.Utils.KeyConverter.ToCamelot(key);

    /// <summary>How the player's tempo sync treats the pair (AudioPlayerService.TempoSyncRatio / TempoFold, ±6 %).</summary>
    static string TempoClass(double? outBpm, double inBpm)
    {
        if (outBpm is not > 0 || inBpm <= 0) return "unknown";
        double raw = outBpm.Value / inBpm;
        if (Math.Abs(raw - 1) < 0.0005) return "same";
        if (Math.Abs(raw - 1) <= 0.06) return "synced";
        // TempoSyncRatio returns 1 both for "no change needed" and "out of range", so classify on the fold itself.
        return TempoFold.RelativeDifference(outBpm.Value, inBpm) <= 0.06 ? "synced half/double" : "out of range";
    }

    /// <summary>Camelot relation: same, adjacent (±1), relative (A↔B), or other. Reported, never shown in the app.</summary>
    static string KeyClass(string? a, string? b)
    {
        if (!Parse(a, out int na, out char la) || !Parse(b, out int nb, out char lb)) return "unknown";
        if (na == nb && la == lb) return "same";
        if (na == nb) return "relative";
        int step = Math.Min((na - nb + 12) % 12, (nb - na + 12) % 12);
        return la == lb && step == 1 ? "adjacent" : "other";

        static bool Parse(string? k, out int num, out char letter)
        {
            num = 0; letter = ' ';
            if (string.IsNullOrWhiteSpace(k) || k.Length < 2) return false;
            letter = char.ToUpperInvariant(k[^1]);
            return (letter is 'A' or 'B') && int.TryParse(k[..^1], out num) && num is >= 1 and <= 12;
        }
    }
}

static class Grid
{
    /// <summary>Fractional beat index of <paramref name="t"/> on the tick list (linear between ticks, extrapolated outside).</summary>
    public static double BeatPosition(IReadOnlyList<double> ticks, double t)
    {
        int i = Index(ticks, t);
        int a = Math.Clamp(i, 0, ticks.Count - 2);
        double span = ticks[a + 1] - ticks[a];
        return a + (span > 0 ? (t - ticks[a]) / span : 0);
    }

    public static double NearestTickErrorMs(IReadOnlyList<double> ticks, double t)
    {
        int i = Index(ticks, t);
        double best = double.MaxValue;
        for (int k = Math.Max(0, i - 1); k <= Math.Min(ticks.Count - 1, i + 2); k++)
            best = Math.Min(best, Math.Abs(ticks[k] - t));
        return best * 1000.0;
    }

    /// <summary>Within 0.1 beat of a multiple of <paramref name="beats"/>.</summary>
    public static bool OnMultiple(double beatPosition, int beats)
    {
        double r = Mod(beatPosition, beats);
        return r < 0.1 || r > beats - 0.1;
    }

    public static double Frac(double x) => x - Math.Floor(x);
    public static double Wrap(double d) => d - Math.Round(d);
    public static double Mod(double x, double m) => ((x % m) + m) % m;

    /// <summary>Largest index with ticks[i] ≤ t (−1 before the first tick).</summary>
    static int Index(IReadOnlyList<double> ticks, double t)
    {
        int lo = 0, hi = ticks.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (ticks[mid] <= t) { ans = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return ans;
    }
}

static class Report
{
    public static Dictionary<string, double> Build(List<PairResult> r, int playlistCount)
    {
        var s = new Dictionary<string, double>
        {
            ["playlists"] = playlistCount,
            ["pairs"] = r.Count,
        };
        foreach (var path in Enum.GetValues<PlaybackTransitionPath>())
            s[$"path.{path}.pct"] = Pct(r, x => x.Path == path);
        foreach (var reason in new[] { "no analysis", "no grid", "tempo jump" })
            s[$"fallback.{reason.Replace(' ', '_')}.pct"] = Pct(r, x => x.FallbackReason == reason);
        s["fallback.tempo_jump_but_half_double.pct"] = Pct(r, x => x.FallbackReason == "tempo jump" && x.Tempo == "synced half/double");
        foreach (var preset in r.Where(x => x.Path == PlaybackTransitionPath.Planned).GroupBy(x => x.Preset))
            s[$"planned.{preset.Key.Replace(' ', '_')}.pct"] = Pct(r, x => x.Path == PlaybackTransitionPath.Planned && x.Preset == preset.Key);
        s["clamped.pct"] = Pct(r, x => x.Clamped);

        // Grid scores only for beat-matched mixes: an echo-out fallback doesn't try to line beats up.
        var graded = r.Where(x => x.PhaseErrorMs.HasValue && x.Path != PlaybackTransitionPath.EchoOutFallback).ToList();
        s["graded.pairs"] = graded.Count;
        if (graded.Count > 0)
        {
            s["start.on_beat_10ms.pct"] = Pct(graded, x => x.StartBeatErrorMs <= 10);
            s["start.on_bar.pct"] = Pct(graded, x => x.StartOnBar == true);
            s["start.on_8bar_phrase.pct"] = Pct(graded, x => x.StartOnPhrase == true);
            s["entry.on_beat_10ms.pct"] = Pct(graded, x => x.EntryBeatErrorMs <= 10);
            s["entry.on_bar.pct"] = Pct(graded, x => x.EntryOnBar == true);
            s["bars_aligned.pct"] = Pct(graded, x => x.BarsAligned == true);
            var phase = graded.Select(x => x.PhaseErrorMs!.Value).OrderBy(x => x).ToList();
            s["phase_error.median_ms"] = Quantile(phase, 0.5);
            s["phase_error.p95_ms"] = Quantile(phase, 0.95);
            s["phase_error.over_20ms.pct"] = Pct(graded, x => x.PhaseErrorMs > 20);
            foreach (var t in graded.GroupBy(x => x.Tempo))
            {
                var g = t.ToList();
                string k = t.Key.Replace(' ', '_').Replace('/', '_');
                s[$"by_tempo.{k}.pairs"] = g.Count;
                s[$"by_tempo.{k}.phase_over_20ms.pct"] = Pct(g, x => x.PhaseErrorMs > 20);
                s[$"by_tempo.{k}.bars_aligned.pct"] = Pct(g, x => x.BarsAligned == true);
            }
        }
        foreach (var t in r.GroupBy(x => x.Tempo)) s[$"tempo.{t.Key.Replace(' ', '_').Replace('/', '_')}.pct"] = Pct(r, x => x.Tempo == t.Key);
        foreach (var k in r.GroupBy(x => x.Key)) s[$"key.{k.Key}.pct"] = Pct(r, x => x.Key == k.Key);
        return s;
    }

    public static void Print(Dictionary<string, double> s, List<PairResult> r)
    {
        Console.WriteLine($"MixBenchmark Part A — {s["pairs"]} pairs in {s["playlists"]} playlists ({s.GetValueOrDefault("graded.pairs")} beat-matched with both grids)\n");
        foreach (var (key, value) in s.Where(kv => kv.Key is not ("pairs" or "playlists" or "graded.pairs")))
            Console.WriteLine($"  {key,-44} {value,8:0.0}");
    }

    public static void Compare(Dictionary<string, double> before, Dictionary<string, double> after)
    {
        Console.WriteLine("\nCompared with the baseline:");
        foreach (var key in after.Keys.Union(before.Keys).OrderBy(k => k))
        {
            double b = before.GetValueOrDefault(key), a = after.GetValueOrDefault(key);
            if (Math.Abs(a - b) < 0.05) continue;
            Console.WriteLine($"  {key,-44} {b,8:0.0} → {a,8:0.0}  ({a - b:+0.0;-0.0})");
        }
    }

    static double Pct<T>(List<T> xs, Func<T, bool> pred) => xs.Count == 0 ? 0 : 100.0 * xs.Count(pred) / xs.Count;

    static double Quantile(List<double> sorted, double q) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];
}

sealed record Options(string DbPath, string? SummaryPath, string? ComparePath, string? Trace, int MaxPlaylists, int Worst)
{
    public static Options? Parse(string[] args)
    {
        string? db = null, summary = null, compare = null, trace = null;
        int max = int.MaxValue, worst = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--db": db = Next(); break;
                case "--summary": summary = Next(); break;
                case "--compare": compare = Next(); break;
                case "--trace": trace = Next(); break;
                case "--max-playlists": max = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--worst": worst = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                default: Console.Error.WriteLine($"Unknown option {args[i]}"); return null;
            }
        }
        return db is null ? null : new Options(db, summary, compare, trace, max, worst);
    }

    public static void PrintUsage() => Console.Error.WriteLine("""
        Usage: dotnet run --project Tests/MixBenchmark -- --db <library.db COPY> [options]
          --summary <file.json>   write the headline numbers
          --compare <file.json>   print the change against an earlier --summary
          --trace "<text>"        print each pair whose outgoing artist/title contains the text
          --max-playlists <n>     only the n largest playlists
          --worst <n>             list the n beat-matched pairs with the largest phase error
        """);
}
