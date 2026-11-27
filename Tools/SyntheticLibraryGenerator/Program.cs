using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SyntheticLibraryGenerator;

/// <summary>
/// Generates deterministic synthetic ORBIT SQLite databases (5k / 30k tracks) with realistic
/// LibraryEntries, AudioFeatures, CuePoints, and Playlists for PF0 (#606) benchmarking.
/// Use via ORBIT_DB_PATH or pass to Tests/PerfBench.
/// </summary>
internal static class Program
{
    private static readonly string[] Artists =
    {
        "Calibre", "Alix Perez", "Break", "Sub Focus", "Dimension", "Camo & Krooked", "Mefjus", "Noisia",
        "Bicep", "Four Tet", "Floating Points", "Overmono", "Ben UFO", "Joy Orbison", "Pangaea", "Pearson Sound",
        "Charlotte de Witte", "Amelie Lens", "Dax J", "Kobosil", "SNTS", "I Hate Models", "Paula Temple", "Rødhåd",
        "Peggy Gou", "Folamour", "Kettama", "Mall Grab", "DJ Seinfeld", "Ross from Friends", "Solomun", "Tale Of Us",
        "Skream", "Benga", "Mala", "Coki", "Hamdi", "Flowdan", "Burial", "Digital Mystikz"
    };

    private static readonly string[] TitleWords =
    {
        "Midnight", "Eclipse", "Pulse", "Submerge", "Horizon", "Velvet", "Static", "Kinetic",
        "Echoes", "Mirage", "Gravity", "Rift", "Solstice", "Neon", "Drift", "Cipher",
        "Vortex", "Lumen", "Obsidian", "Prism", "Signal", "Tide", "Vector", "Nocturne"
    };

    private static readonly string[] MixTags =
    {
        "", " (Original Mix)", " (VIP)", " (Extended Mix)", " (Club Edit)", " (Dub)", " (Remix)"
    };

    private static readonly (string Genre, string Subgenre, double MinBpm, double MaxBpm)[] GenreProfiles =
    {
        ("Drum & Bass", "Liquid DnB", 170, 176),
        ("Drum & Bass", "Neurofunk", 172, 178),
        ("Drum & Bass", "Jump Up", 174, 176),
        ("Techno", "Peak Time Techno", 132, 142),
        ("Techno", "Hypnotic Techno", 128, 136),
        ("House", "Deep House", 118, 125),
        ("House", "Tech House", 124, 130),
        ("Dubstep", "Deep Dubstep", 138, 142),
        ("Ambient", "Downtempo", 85, 110),
        ("Breakbeat", "UK Garage", 128, 136)
    };

    private static readonly string[] CamelotKeys =
    {
        "1A", "2A", "3A", "4A", "5A", "6A", "7A", "8A", "9A", "10A", "11A", "12A",
        "1B", "2B", "3B", "4B", "5B", "6B", "7B", "8B", "9B", "10B", "11B", "12B"
    };

    private static readonly string[] Formats = { "flac", "mp3", "wav", "aiff", "m4a" };

    public static async Task<int> Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        int count = 30_000;
        int seed = 42;
        string? outPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--count" when i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c):
                    count = Math.Max(1, c);
                    break;
                case "--seed" when i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s):
                    seed = s;
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--help" or "-h":
                    Console.WriteLine("Usage: SyntheticLibraryGenerator --count <5000|30000> --out <db-path> [--seed 42]");
                    return 0;
            }
        }

        outPath ??= Path.Combine(Path.GetTempPath(), $"orbit-synthetic-{count}.db");
        outPath = Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        if (File.Exists(outPath)) File.Delete(outPath);
        if (File.Exists(outPath + "-wal")) File.Delete(outPath + "-wal");
        if (File.Exists(outPath + "-shm")) File.Delete(outPath + "-shm");

        Environment.SetEnvironmentVariable("ORBIT_DB_PATH", outPath);

        var sw = Stopwatch.StartNew();
        Console.WriteLine($"[SyntheticLibraryGenerator] Creating schema at {outPath} ({count:N0} tracks, seed={seed})...");

        // Create the current EF Core schema directly (avoids SchemaMigratorService backup/restore hooks on temp DBs).
        await using (var initCtx = new AppDbContext())
        {
            await initCtx.Database.EnsureCreatedAsync();
        }

        var rng = new Random(seed);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Create 4 realistic playlists: Small (50), Medium (500), Large (5000), and Unassigned/Club (1000).
        var playlists = new[]
        {
            new PlaylistJobEntity { Id = Guid.NewGuid(), SourceTitle = "Warmup Crate (50)", SourceType = "Manual", CreatedAt = baseTime, DateUpdated = baseTime, TotalTracks = Math.Min(50, count), SuccessfulCount = Math.Min(50, count) },
            new PlaylistJobEntity { Id = Guid.NewGuid(), SourceTitle = "Weekend Club Set (500)", SourceType = "Manual", CreatedAt = baseTime.AddDays(1), DateUpdated = baseTime.AddDays(1), TotalTracks = Math.Min(500, count), SuccessfulCount = Math.Min(500, count) },
            new PlaylistJobEntity { Id = Guid.NewGuid(), SourceTitle = "Mega Archive (5000)", SourceType = "Rekordbox", CreatedAt = baseTime.AddDays(2), DateUpdated = baseTime.AddDays(2), TotalTracks = Math.Min(5000, count), SuccessfulCount = Math.Min(5000, count) },
            new PlaylistJobEntity { Id = Guid.NewGuid(), SourceTitle = "DnB Rollers (1000)", SourceType = "Manual", CreatedAt = baseTime.AddDays(3), DateUpdated = baseTime.AddDays(3), TotalTracks = Math.Min(1000, count), SuccessfulCount = Math.Min(1000, count) },
        };

        await using (var ctx = new AppDbContext())
        {
            ctx.Projects.AddRange(playlists);
            await ctx.SaveChangesAsync();
        }

        const int batchSize = 1000;
        int totalCues = 0;
        int totalPlaylistRows = 0;

        for (int start = 0; start < count; start += batchSize)
        {
            int end = Math.Min(start + batchSize, count);
            await using var ctx = new AppDbContext();
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;

            var entries = new List<LibraryEntryEntity>(end - start);
            var features = new List<AudioFeaturesEntity>(end - start);
            var cues = new List<CuePointEntity>((end - start) * 4);
            var pTracks = new List<PlaylistTrackEntity>();

            for (int i = start; i < end; i++)
            {
                var hash = $"syn_{seed:D2}_{i:D7}_{rng.Next(100000, 999999):D6}";
                var artist = Artists[rng.Next(Artists.Length)];
                var title = $"{TitleWords[rng.Next(TitleWords.Length)]} {TitleWords[rng.Next(TitleWords.Length)]}{MixTags[rng.Next(MixTags.Length)]}";
                var album = $"{TitleWords[rng.Next(TitleWords.Length)]} EP";
                var profile = GenreProfiles[rng.Next(GenreProfiles.Length)];
                var bpm = Math.Round(profile.MinBpm + rng.NextDouble() * (profile.MaxBpm - profile.MinBpm), 2);
                var key = CamelotKeys[rng.Next(CamelotKeys.Length)];
                var format = Formats[rng.Next(Formats.Length)];
                var bitrate = format is "flac" or "wav" or "aiff" ? 1411 : 320;
                var duration = rng.Next(180, 460);
                var energy = Math.Round(0.25 + rng.NextDouble() * 0.70, 3);
                var dance = Math.Round(0.35 + rng.NextDouble() * 0.60, 3);
                var valence = Math.Round(0.15 + rng.NextDouble() * 0.80, 3);
                var addedAt = baseTime.AddSeconds(i * 17);
                var filePath = $@"C:\Music\Synthetic\{artist}\{title}.{format}";

                entries.Add(new LibraryEntryEntity
                {
                    UniqueHash = hash,
                    Id = Guid.NewGuid(),
                    Artist = artist,
                    Title = title,
                    Album = album,
                    FilePath = filePath,
                    AvailabilityState = TrackAvailabilityState.Ready,
                    Bitrate = bitrate,
                    DurationSeconds = duration,
                    Format = format,
                    AddedAt = addedAt,
                    LastUsedAt = addedAt,
                    Genres = $"{profile.Genre}, {profile.Subgenre}",
                    BPM = bpm,
                    MusicalKey = key,
                    Energy = energy,
                    Danceability = dance,
                    Valence = valence,
                    Loudness = -8.5 + rng.NextDouble() * 4.0,
                    TruePeak = -0.3 - rng.NextDouble() * 1.2,
                    IsEnriched = true
                });

                // 85% analyzed with AudioFeatures + CuePoints (realistic DJ library mix).
                if (rng.NextDouble() < 0.85)
                {
                    var drop1 = Math.Round(32.0 + rng.NextDouble() * 48.0, 3);
                    var breakdown = Math.Round(drop1 + 48.0 + rng.NextDouble() * 32.0, 3);
                    var outro = Math.Round(Math.Max(breakdown + 32.0, duration - 45.0), 3);

                    features.Add(new AudioFeaturesEntity
                    {
                        Id = Guid.NewGuid(),
                        TrackUniqueHash = hash,
                        TrackDuration = duration,
                        Bpm = (float)bpm,
                        BpmConfidence = 0.92f,
                        CamelotKey = key,
                        KeyConfidence = 0.88f,
                        Energy = (float)energy,
                        EnergyScore = Math.Clamp((int)Math.Round(energy * 10), 1, 10),
                        Danceability = (float)dance,
                        Valence = (float)valence,
                        LoudnessLUFS = (float)(-8.5 + rng.NextDouble() * 4.0),
                        DownbeatOffsetSeconds = Math.Round(rng.NextDouble() * 0.5, 4),
                        DetectedSubGenre = profile.Subgenre,
                        ElectronicSubgenre = profile.Genre,
                        ElectronicSubgenreConfidence = 0.84f,
                        DropTimeSeconds = (float)drop1,
                        DropConfidence = 0.90f,
                        GridConfidence = 0.91f,
                        GridSource = "Analysis",
                        AnalyzedAt = addedAt.AddMinutes(5)
                    });

                    cues.Add(new CuePointEntity { TrackUniqueHash = hash, TimestampInSeconds = 0.5, Type = CuePointType.Intro, Label = "Intro", Color = "#28E214", SlotIndex = 0, Confidence = 0.95f });
                    cues.Add(new CuePointEntity { TrackUniqueHash = hash, TimestampInSeconds = drop1, Type = CuePointType.Drop, Label = "Drop 1", Color = "#FF3B30", SlotIndex = 1, Confidence = 0.90f });
                    cues.Add(new CuePointEntity { TrackUniqueHash = hash, TimestampInSeconds = breakdown, Type = CuePointType.Breakdown, Label = "Breakdown", Color = "#00C7BE", SlotIndex = 2, Confidence = 0.82f });
                    cues.Add(new CuePointEntity { TrackUniqueHash = hash, TimestampInSeconds = outro, Type = CuePointType.Outro, Label = "Outro", Color = "#AF52DE", SlotIndex = 3, Confidence = 0.88f });
                }

                // Assign subsets to playlists so playlist queries can be benchmarked too.
                void AddToPlaylist(PlaylistJobEntity p, int order)
                {
                    pTracks.Add(new PlaylistTrackEntity
                    {
                        Id = Guid.NewGuid(),
                        PlaylistId = p.Id,
                        Artist = artist,
                        Title = title,
                        Album = album,
                        TrackUniqueHash = hash,
                        Status = TrackStatus.Downloaded,
                        AvailabilityState = TrackAvailabilityState.Ready,
                        ResolvedFilePath = filePath,
                        Bitrate = bitrate,
                        Format = format,
                        ActualDurationSeconds = duration,
                        AddedAt = addedAt,
                        CompletedAt = addedAt,
                        SortOrder = order,
                        Genres = $"{profile.Genre}, {profile.Subgenre}",
                        PrimaryGenre = profile.Genre,
                        DetectedSubGenre = profile.Subgenre,
                        BPM = bpm,
                        MusicalKey = key,
                        Energy = energy,
                        Danceability = dance,
                        Valence = valence,
                        IsEnriched = true,
                        AnalysisStatus = AnalysisStatus.Completed
                    });
                }

                if (i < 50) AddToPlaylist(playlists[0], i);
                if (i < 500) AddToPlaylist(playlists[1], i);
                if (i < 5000) AddToPlaylist(playlists[2], i);
                if (i < 1000) AddToPlaylist(playlists[3], i);
            }

            ctx.LibraryEntries.AddRange(entries);
            ctx.AudioFeatures.AddRange(features);
            ctx.CuePoints.AddRange(cues);
            ctx.PlaylistTracks.AddRange(pTracks);
            await ctx.SaveChangesAsync();

            totalCues += cues.Count;
            totalPlaylistRows += pTracks.Count;
        }

        // Create & seed the FTS5 virtual table matching SchemaMigratorService so search benchmarks work identically to production.
        await using (var ftsCtx = new AppDbContext())
        {
            await ftsCtx.Database.ExecuteSqlRawAsync("""
                CREATE VIRTUAL TABLE IF NOT EXISTS LibraryEntriesFts USING fts5(
                    Artist,
                    Title,
                    Album,
                    Key,
                    UniqueHash UNINDEXED
                );
                INSERT INTO LibraryEntriesFts(rowid, Artist, Title, Album, Key, UniqueHash)
                SELECT rowid, Artist, Title, Album, COALESCE(MusicalKey, ''), UniqueHash
                FROM LibraryEntries;
                """);
        }

        var sizeMb = new FileInfo(outPath).Length / (1024.0 * 1024.0);
        Console.WriteLine($"[SyntheticLibraryGenerator] Done in {sw.Elapsed.TotalSeconds:F2}s: {count:N0} tracks, {totalCues:N0} cues, {totalPlaylistRows:N0} playlist rows, {sizeMb:F1} MB -> {outPath}");
        return 0;
    }

    private sealed class SqliteDatabaseFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
    }
}
