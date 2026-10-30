using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Copilot;

namespace SLSKDONET.Tools.CopilotBattleTest;

/// <summary>
/// Read-only battle-test harness for the ORBIT copilot. Runs the Set Builder, Bridge Finder,
/// Clash Detector and Alternative Route finder against a real library database and dumps the
/// results as JSON + a human-readable track sequence.
///
/// Usage:
///   CopilotBattleTest [--db <path>] [--minutes 60] [--bpm-variance 0.08]
///                     [--source <hash> --target <hash>] [--set <setProjectId>] [--out <path>]
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var dbPath = Arg(args, "--db") ?? SLSKDONET.Data.OrbitPaths.LibraryDbPath;
            var minutes = double.TryParse(Arg(args, "--minutes"), out var m) ? m : 60.0;
            var bpmVariance = double.TryParse(Arg(args, "--bpm-variance"), out var v) ? v : 0.08;
            var source = Arg(args, "--source");
            var target = Arg(args, "--target");
            var setArg = Arg(args, "--set");
            var outPath = Arg(args, "--out");
            var seed = Arg(args, "--seed");
            var targetBpm = double.TryParse(Arg(args, "--target-bpm"), out var tb) ? tb : (double?)null;
            var mockVocals = args.Any(a => string.Equals(a, "--mock-vocals", StringComparison.OrdinalIgnoreCase));

            // Optional schema migration (runs SchemaMigratorService + SchemaReconciler against the
            // default library path, READ-WRITE, so the Phase 2 tables exist before the copilot runs).
            if (args.Any(a => string.Equals(a, "--migrate", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("Reconciling schema (SchemaReconciler)...");
                var migOptions = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={SLSKDONET.Data.OrbitPaths.LibraryDbPath}")
                    .Options;
                using var migCtx = new AppDbContext(migOptions);
                var conn = migCtx.Database.GetDbConnection();
                var reconcileReport = SchemaReconciler.Reconcile(migCtx, conn, new ConsoleLogger<AppDbContext>());
                Console.WriteLine($"Created tables: [{string.Join(", ", reconcileReport.CreatedTables)}]");
                Console.WriteLine($"Added columns:  [{string.Join(", ", reconcileReport.AddedColumns)}]");
                Console.WriteLine($"Failures:       [{string.Join("; ", reconcileReport.Failures)}]");
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString())
                .Options;

            var factory = new CliDbContextFactory(options);
            var copilot = new OrbitCopilot(factory);

            var report = await RunAsync(copilot, factory, dbPath, minutes, bpmVariance, source, target, setArg, seed, targetBpm, mockVocals);
            var text = RenderText(report);

            Console.WriteLine(text);

            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (!string.IsNullOrWhiteSpace(outPath))
            {
                await File.WriteAllTextAsync(outPath, json);
                Console.WriteLine($"\nJSON written to {outPath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Battle test failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<BattleReport> RunAsync(
        OrbitCopilot copilot,
        CliDbContextFactory factory,
        string dbPath,
        double minutes,
        double bpmVariance,
        string? source,
        string? target,
        string? setArg,
        string? seed,
        double? targetBpm,
        bool mockVocals)
    {
        var report = new BattleReport
        {
            DbPath = dbPath,
            GeneratedAtUtc = DateTime.UtcNow,
        };

        // 1. Set Builder.
        var build = await copilot.BuildSetAsync(new SetBuildRequest(minutes, EventProfile: null, bpmVariance, SeedTrackHash: seed, TargetBpm: targetBpm));
        report.SetBuild = await ResolveSpineAsync(factory, build.SpineTrackHashes);
        report.SetBuildDurationMinutes = build.DurationMinutes;
        report.SpineClashes = await ResolveSpineClashesAsync(factory, build.SpineTrackHashes, mockVocals);

        // 2. Bridge Finder.
        if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(target))
            report.Bridges = (await copilot.FindBridgesAsync(source, target)).ToList();

        // 3. Clash Detector + Alternative Routes (per set).
        if (Guid.TryParse(setArg, out var setId))
        {
            report.Clashes = (await copilot.DetectClashesAsync(setId)).ToList();
            report.AlternativeRoutes = (await copilot.FindAlternativeRoutesAsync(setId)).ToList();
        }

        return report;
    }

    private static async Task<List<SpineTrackEntry>> ResolveSpineAsync(CliDbContextFactory factory, IReadOnlyList<string> hashes)
    {
        await using var db = factory.CreateDbContext();

        var entries = await db.LibraryEntries
            .Where(e => hashes.Contains(e.UniqueHash))
            .ToListAsync();
        var entryByHash = entries.ToDictionary(e => e.UniqueHash, StringComparer.Ordinal);

        var features = await db.AudioFeatures
            .Where(f => hashes.Contains(f.TrackUniqueHash))
            .ToListAsync();
        var featureByHash = features.ToDictionary(f => f.TrackUniqueHash, StringComparer.Ordinal);

        var result = new List<SpineTrackEntry>();
        for (var i = 0; i < hashes.Count; i++)
        {
            var hash = hashes[i];
            entryByHash.TryGetValue(hash, out var entry);
            featureByHash.TryGetValue(hash, out var feature);

            result.Add(new SpineTrackEntry(
                i + 1,
                hash,
                entry?.Title ?? hash,
                entry?.Artist ?? string.Empty,
                feature?.Bpm ?? 0,
                feature is null ? 0 : feature.Energy * 100.0));
        }
        return result;
    }

    private static async Task<List<ClashReport>> ResolveSpineClashesAsync(CliDbContextFactory factory, IReadOnlyList<string> spineHashes, bool mockVocals)
    {
        if (spineHashes.Count < 2)
            return new List<ClashReport>();

        await using var db = factory.CreateDbContext();

        var features = await db.AudioFeatures
            .Where(f => spineHashes.Contains(f.TrackUniqueHash))
            .ToListAsync();
        var featureByHash = features.ToDictionary(f => f.TrackUniqueHash, StringComparer.Ordinal);

        var cues = await db.CuePoints
            .Where(c => spineHashes.Contains(c.TrackUniqueHash))
            .ToListAsync();

        const int barsBeforeMix = 32; // D&B long-blend default
        var bpm = featureByHash.Values.FirstOrDefault(f => f.Bpm > 0)?.Bpm ?? 174.0;

        var spineData = new List<TrackClashData>();
        foreach (var hash in spineHashes)
        {
            featureByHash.TryGetValue(hash, out var f);
            var trackCues = cues.Where(c => c.TrackUniqueHash == hash).ToList();
            var duration = f?.TrackDuration > 0 ? f.TrackDuration : 300.0;

            var mixOut = ResolveCue(trackCues, CuePointType.MixOut) ?? duration - 44.0;
            var mixIn = ResolveCue(trackCues, CuePointType.MixIn) ?? 44.0;
            var camelot = ClashDetector.CamelotNumberFromKey(f?.CamelotKey);

            VocalSegment[] vocalSegments;
            if (mockVocals)
            {
                // Synthetic temporal vocal blocks that deliberately bleed into the 44s mix-in and
                // mix-out windows, forcing an intentional vocal overlap to verify the window math.
                vocalSegments = new[]
                {
                    new VocalSegment(Math.Max(0.0, mixIn - 10.0), mixIn + 10.0),
                    new VocalSegment(Math.Max(0.0, mixOut - 10.0), Math.Min(duration, mixOut + 10.0)),
                };
            }
            else
            {
                vocalSegments = (f?.VocalDensity ?? 0f) > 0.5f
                    ? new[] { new VocalSegment(0.0, duration) }
                    : Array.Empty<VocalSegment>();
            }

            spineData.Add(new TrackClashData(hash, mixOut, mixIn, barsBeforeMix, camelot, vocalSegments));
        }

        return ClashDetector.DetectClashes(spineData, bpm).ToList();
    }

    private static double? ResolveCue(IReadOnlyList<CuePointEntity> cues, CuePointType type)
        => cues.FirstOrDefault(c => c.Type == type)?.TimestampInSeconds;

    private static string RenderText(BattleReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== ORBIT Copilot Battle Test ===");
        sb.AppendLine($"DB:      {report.DbPath}");
        sb.AppendLine($"At:      {report.GeneratedAtUtc:O}");
        sb.AppendLine();

        if (report.SetBuild is { Count: > 0 })
        {
            sb.AppendLine($"--- Set Builder spine ({report.SetBuildDurationMinutes:F1} min) ---");
            foreach (var t in report.SetBuild)
                sb.AppendLine($"  {t.Position,3}. {t.Title}  ({t.Bpm:0.#} BPM, {t.Energy:0.#} energy)  [{t.Hash}]");
            sb.AppendLine();
        }

        if (report.Bridges is { Count: > 0 })
        {
            sb.AppendLine("--- Bridge Finder ---");
            foreach (var b in report.Bridges)
                sb.AppendLine($"  depth {b.Depth}, score {b.TotalScore:F1}:  {string.Join(" -> ", b.TrackHashes)}");
            sb.AppendLine();
        }

        if (report.Clashes is { Count: > 0 })
        {
            sb.AppendLine("--- Clash Detector ---");
            foreach (var c in report.Clashes)
                sb.AppendLine($"  {c.TrackHashA} -> {c.TrackHashB}: {c.Reason}");
            sb.AppendLine();
        }

        if (report.AlternativeRoutes is { Count: > 0 })
        {
            sb.AppendLine("--- Alternative Routes ---");
            foreach (var r in report.AlternativeRoutes)
                sb.AppendLine($"  {r.SourceTrackHash} -> {r.TargetTrackHash}: {r.Score:F1}");
        }

        if (report.SpineClashes is { Count: > 0 })
        {
            sb.AppendLine("--- Spine Clashes (in-memory) ---");
            foreach (var c in report.SpineClashes)
                sb.AppendLine($"  {c.TrackHashA} -> {c.TrackHashB}: {c.Reason}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private sealed class CliDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public CliDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }

    private sealed class ConsoleLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Console.WriteLine($"[{logLevel}] {formatter(state, exception)}");
    }

    private sealed record SpineTrackEntry(int Position, string Hash, string Title, string Artist, double Bpm, double Energy);

    private sealed class BattleReport
    {
        public string DbPath { get; init; } = string.Empty;
        public DateTime GeneratedAtUtc { get; init; }
        public double SetBuildDurationMinutes { get; set; }
        public List<SpineTrackEntry>? SetBuild { get; set; }
        public List<BridgePath>? Bridges { get; set; }
        public List<ClashReport>? Clashes { get; set; }
        public List<ClashReport>? SpineClashes { get; set; }
        public List<AlternativeRoute>? AlternativeRoutes { get; set; }
    }
}
