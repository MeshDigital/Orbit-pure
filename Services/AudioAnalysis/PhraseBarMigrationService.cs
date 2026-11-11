using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Models;
using SLSKDONET.Views;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// One-time migration of phrase data to the bar-indexed model (issue #225). Existing
/// <c>TrackPhrases</c> rows and the legacy <c>PhraseSegmentsJson</c> column are stored in seconds;
/// this converts their boundaries to bars (from Bar 1, using each track's grid) so that from then on
/// a grid edit re-derives the seconds and phrase boundaries move with the bars. Runs once (marker
/// file), with a consistent SQLite backup first — the same safety pattern as
/// <see cref="BeatGridRecomputeService"/>. User-edited phrases are never overwritten.
/// </summary>
public sealed class PhraseBarMigrationService
{
    private const string PassName = "phrase-bars-v1";
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(120);

    private readonly ILogger<PhraseBarMigrationService> _logger;
    private readonly DatabaseService _database;
    private readonly IEventBus _eventBus;

    public PhraseBarMigrationService(ILogger<PhraseBarMigrationService> logger, DatabaseService database, IEventBus eventBus)
    {
        _logger = logger;
        _database = database;
        _eventBus = eventBus;
    }

    /// <summary>Where the backup and the "done" marker live. Overridable in tests (never the real AppData).</summary>
    internal string DataDirectory { get; set; } = Path.Combine(OrbitPaths.RoamingRoot, "ORBIT", "Backups");

    private string MarkerPath => Path.Combine(DataDirectory, $"{PassName}.done");

    /// <summary>Starts the pass in the background if it hasn't run yet. Never throws.</summary>
    public void StartIfPending(CancellationToken ct = default)
    {
        if (File.Exists(MarkerPath)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartDelay, ct).ConfigureAwait(false);
                await RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PhraseBarMigration] Pass failed; it will retry on next launch.");
            }
        }, ct);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (File.Exists(MarkerPath)) return;

        List<AudioFeaturesEntity> features;
        using (var db = new AppDbContext())
        {
            features = await db.AudioFeatures.AsNoTracking()
                .Where(f => f.Bpm > 0 && !string.IsNullOrWhiteSpace(f.BeatGridJson))
                .ToListAsync(ct).ConfigureAwait(false);
        }
        if (features.Count == 0) { WriteMarker("no analysed tracks"); return; }

        string backup = BackupLibrary();
        _logger.LogInformation("[PhraseBarMigration] Converting phrase data to bars for {Count} tracks (backup: {Backup})", features.Count, backup);

        int converted = 0, backfilled = 0;
        foreach (var f in features)
        {
            ct.ThrowIfCancellationRequested();
            double bpm = f.Bpm;
            double downbeat = f.DownbeatOffsetSeconds;

            using var context = new AppDbContext();
            var phrases = await context.TrackPhrases
                .Where(p => p.TrackUniqueHash == f.TrackUniqueHash)
                .ToListAsync(ct).ConfigureAwait(false);

            // A user-edited phrase map is never overwritten.
            bool userOwned = phrases.Any(p => string.Equals(p.Source, "User", StringComparison.Ordinal));
            if (userOwned) continue;

            if (phrases.Count > 0)
            {
                foreach (var p in phrases)
                {
                    if (p.BarCount > 0 || p.StartBar > 0) continue; // already migrated
                    p.StartBar = PhraseBarIndexing.SecondsToBar(p.StartTimeSeconds, bpm, downbeat);
                    p.BarCount = PhraseBarIndexing.DurationToBars(p.EndTimeSeconds - p.StartTimeSeconds, bpm);
                    p.Source = string.IsNullOrWhiteSpace(f.PhraseSegmentsSource) ? "Heuristic" : f.PhraseSegmentsSource;
                    converted++;
                }
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
                continue;
            }

            // No TrackPhrases yet: backfill from the trusted legacy JSON.
            if (!PhraseSourceResolver.IsTrustedSource(f.PhraseSegmentsSource)) continue;
            var segments = ParsePhraseSegments(f.PhraseSegmentsJson);
            if (segments.Count < 2) continue;

            int order = 0;
            foreach (var s in segments.OrderBy(s => s.Start))
            {
                context.TrackPhrases.Add(new TrackPhraseEntity
                {
                    TrackUniqueHash = f.TrackUniqueHash,
                    Type = TypeFromLabel(s.Label),
                    Label = s.Label,
                    StartTimeSeconds = s.Start,
                    EndTimeSeconds = s.Start + s.Duration,
                    EnergyLevel = 0,
                    Confidence = s.Confidence,
                    OrderIndex = order++,
                    StartBar = PhraseBarIndexing.SecondsToBar(s.Start, bpm, downbeat),
                    BarCount = PhraseBarIndexing.DurationToBars(s.Duration, bpm),
                    Source = f.PhraseSegmentsSource,
                });
                backfilled++;
            }
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        string summary = $"{converted} phrase rows converted, {backfilled} phrases backfilled from legacy JSON";
        _logger.LogInformation("[PhraseBarMigration] Done: {Summary}", summary);
        WriteMarker(summary);

        if (converted + backfilled > 0)
            _eventBus.Publish(new ToastRequestedEvent(
                "Phrase map updated",
                $"Indexed phrase boundaries by bar for {converted + backfilled} phrases so they follow the beat grid.",
                NotificationType.Success, TimeSpan.FromSeconds(8)));
    }

    internal static List<PhraseSegment> ParsePhraseSegments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<PhraseSegment>();
        try { return JsonSerializer.Deserialize<List<PhraseSegment>>(json) ?? new List<PhraseSegment>(); }
        catch (JsonException) { return new List<PhraseSegment>(); }
    }

    private static PhraseType TypeFromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return PhraseType.Unknown;
        var upper = label.ToUpperInvariant();
        if (upper.Contains("INTRO")) return PhraseType.Intro;
        if (upper.Contains("OUTRO")) return PhraseType.Outro;
        if (upper.Contains("BUILD")) return PhraseType.Build;
        if (upper.Contains("DROP")) return PhraseType.Drop;
        if (upper.Contains("BREAK")) return PhraseType.Breakdown;
        if (upper.Contains("CHORUS")) return PhraseType.Chorus;
        if (upper.Contains("VERSE")) return PhraseType.Verse;
        if (upper.Contains("BRIDGE")) return PhraseType.Bridge;
        return PhraseType.Unknown;
    }

    private string BackupLibrary()
    {
        string source = OrbitPaths.LibraryDbPath;
        Directory.CreateDirectory(DataDirectory);
        string target = Path.Combine(DataDirectory, $"library.db.bak-{PassName}-{DateTime.Now:yyyyMMdd-HHmmss}");

        using var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly }.ToString());
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target }.ToString());
        src.Open();
        dst.Open();
        src.BackupDatabase(dst);
        SqliteConnection.ClearPool(dst);
        return target;
    }

    private void WriteMarker(string summary)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, $"{DateTime.UtcNow:O}\n{summary}\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PhraseBarMigration] Could not write marker {Path}", MarkerPath);
        }
    }
}
