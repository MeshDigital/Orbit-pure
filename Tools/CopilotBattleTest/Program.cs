using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
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

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString())
                .Options;

            var factory = new CliDbContextFactory(options);
            var copilot = new OrbitCopilot(factory);

            var report = await RunAsync(copilot, factory, dbPath, minutes, bpmVariance, source, target, setArg);
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
        string? setArg)
    {
        var report = new BattleReport
        {
            DbPath = dbPath,
            GeneratedAtUtc = DateTime.UtcNow,
        };

        // 1. Set Builder.
        var build = await copilot.BuildSetAsync(new SetBuildRequest(minutes, EventProfile: null, bpmVariance));
        report.SetBuild = await ResolveSpineAsync(factory, build.SpineTrackHashes);
        report.SetBuildDurationMinutes = build.DurationMinutes;

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

    private sealed record SpineTrackEntry(int Position, string Hash, string Title, string Artist, double Bpm, double Energy);

    private sealed class BattleReport
    {
        public string DbPath { get; init; } = string.Empty;
        public DateTime GeneratedAtUtc { get; init; }
        public double SetBuildDurationMinutes { get; set; }
        public List<SpineTrackEntry>? SetBuild { get; set; }
        public List<BridgePath>? Bridges { get; set; }
        public List<ClashReport>? Clashes { get; set; }
        public List<AlternativeRoute>? AlternativeRoutes { get; set; }
    }
}
