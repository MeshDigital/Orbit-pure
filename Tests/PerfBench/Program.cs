using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library;

namespace PerfBench;

/// <summary>
/// PF0 (#606) Library &amp; Genre performance harness.
/// Runs repeatable timings against a 5k/30k ORBIT SQLite database and prints a Markdown baseline table.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string? dbPath = null;
        int iterations = 5;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--iterations" when i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var it):
                    iterations = Math.Max(1, it);
                    break;
                case "--help" or "-h":
                    Console.WriteLine("Usage: PerfBench --db <path-to-library.db> [--iterations 5]");
                    return 0;
            }
        }

        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            Console.Error.WriteLine("Error: pass an existing SQLite DB via --db <path>. Generate one with Tools/SyntheticLibraryGenerator.");
            return 1;
        }

        dbPath = Path.GetFullPath(dbPath);
        Environment.SetEnvironmentVariable("ORBIT_DB_PATH", dbPath);

        var factory = new SqliteDatabaseFactory();
        var dbService = new SLSKDONET.Services.Repositories.TrackRepository(NullLogger<SLSKDONET.Services.Repositories.TrackRepository>.Instance);

        int totalTracks;
        Guid largePlaylistId;
        await using (var ctx = new AppDbContext())
        {
            totalTracks = await ctx.LibraryEntries.AsNoTracking().CountAsync();
            largePlaylistId = await ctx.Projects.AsNoTracking()
                .OrderByDescending(p => p.TotalTracks)
                .Select(p => p.Id)
                .FirstOrDefaultAsync();
        }

        Console.WriteLine($"# ORBIT PerfBench Baseline ({totalTracks:N0} tracks)");
        Console.WriteLine($"- **DB:** `{Path.GetFileName(dbPath)}` ({new FileInfo(dbPath).Length / (1024.0 * 1024.0):F1} MB)");
        Console.WriteLine($"- **Machine:** {Environment.OSVersion}, {Environment.ProcessorCount} logical cores, .NET {Environment.Version}");
        Console.WriteLine($"- **Iterations:** {iterations}");
        Console.WriteLine();
        Console.WriteLine("| Metric | Cold (ms) | Median (ms) | P95 (ms) | Rows | Alloc (MB) |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|");

        // 1. First page query (All Tracks, 100 rows)
        await MeasureAsync("First page query (All Tracks, 100)", iterations, async () =>
        {
            var rows = await dbService.GetPagedAllTracksAsync(0, 100);
            return rows.Count;
        });

        // 2. Paging while scrolling (10 pages across the library, simulating VirtualizedTrackCollection)
        await MeasureAsync("Scroll paging (10x100 pages across library)", iterations, async () =>
        {
            int loaded = 0;
            int step = Math.Max(100, totalTracks / 10);
            for (int p = 0; p < 10; p++)
            {
                var rows = await dbService.GetPagedAllTracksAsync(Math.Min(p * step, Math.Max(0, totalTracks - 100)), 100);
                loaded += rows.Count;
            }
            return loaded;
        });

        // 3. Search keystroke -> results
        string[] queries = { "cal", "midnight", "techno", "vip", "sub" };
        await MeasureAsync("Search keystroke -> results (page 0, 100)", iterations, async () =>
        {
            int count = 0;
            foreach (var q in queries)
            {
                var rows = await dbService.GetPagedAllTracksAsync(0, 100, filter: q);
                count += rows.Count;
            }
            return count / queries.Length;
        }, divideBy: queries.Length);

        // 4. LibraryGenreIndex cold build (#562)
        IReadOnlyCollection<string> genreHashes = Array.Empty<string>();
        await MeasureAsync("LibraryGenreIndex.BuildAsync (#562)", Math.Min(3, iterations), async () =>
        {
            using var genreIndex = new LibraryGenreIndex(factory, eventBus: null, NullLogger<LibraryGenreIndex>.Instance);
            await genreIndex.BuildAsync();
            var tree = genreIndex.GetTree();
            var firstBucket = tree.FirstOrDefault(n => !n.Key.IsUnclassified);
            if (firstBucket != null)
                genreHashes = genreIndex.GetHashes(firstBucket.Key);
            return tree.Sum(n => n.TrackCount);
        });

        // 5. Genre filter query (#563)
        await MeasureAsync($"Genre filter page 0 ({genreHashes.Count:N0} hashes, #563)", iterations, async () =>
        {
            var rows = await dbService.GetPagedAllTracksAsync(0, 100, hashFilter: genreHashes);
            return rows.Count;
        });

        // 6. Play current view list build (#565) — filtered genre view
        await MeasureAsync("Play current view (ordered genre subset, #565)", iterations, async () =>
        {
            var rows = await dbService.GetOrderedAllTracksAsync(downloadedOnly: true, hashFilter: genreHashes);
            return rows.Count;
        });

        // 7. Play current view list build (#565) — large playlist
        if (largePlaylistId != Guid.Empty)
        {
            await MeasureAsync("Play current view (ordered 5k playlist, #565)", iterations, async () =>
            {
                var rows = await dbService.GetOrderedPlaylistTracksAsync(largePlaylistId, downloadedOnly: true);
                return rows.Count;
            });
        }

        // 8. Full library ordered list build (worst-case Play All)
        await MeasureAsync("Play current view (all tracks ordered, #565)", Math.Min(3, iterations), async () =>
        {
            var rows = await dbService.GetOrderedAllTracksAsync(downloadedOnly: true);
            return rows.Count;
        });

        // Memory summary
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var proc = Process.GetCurrentProcess();
        Console.WriteLine();
        Console.WriteLine($"- **Working Set:** {proc.WorkingSet64 / (1024.0 * 1024.0):F1} MB");
        Console.WriteLine($"- **Managed Heap (post-GC):** {GC.GetTotalMemory(false) / (1024.0 * 1024.0):F1} MB");

        return 0;
    }

    private static async Task MeasureAsync(string name, int iterations, Func<Task<int>> action, int divideBy = 1)
    {
        var times = new List<double>(iterations);
        int lastRows = 0;
        long totalAlloc = 0;

        for (int i = 0; i < iterations; i++)
        {
            long allocBefore = GC.GetTotalAllocatedBytes(precise: false);
            var sw = Stopwatch.StartNew();
            lastRows = await action();
            sw.Stop();
            long allocAfter = GC.GetTotalAllocatedBytes(precise: false);

            times.Add(sw.Elapsed.TotalMilliseconds / divideBy);
            totalAlloc += Math.Max(0, allocAfter - allocBefore) / divideBy;
        }

        double cold = times[0];
        var sorted = times.OrderBy(t => t).ToList();
        double median = sorted[sorted.Count / 2];
        double p95 = sorted[(int)Math.Clamp(Math.Ceiling(sorted.Count * 0.95) - 1, 0, sorted.Count - 1)];
        double avgAllocMb = (totalAlloc / (double)iterations) / (1024.0 * 1024.0);

        Console.WriteLine($"| {name} | {cold:F1} | {median:F1} | {p95:F1} | {lastRows:N0} | {avgAllocMb:F2} |");
    }

    private sealed class SqliteDatabaseFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
    }
}
