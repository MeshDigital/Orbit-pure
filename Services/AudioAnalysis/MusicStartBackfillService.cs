using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Views;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// One-time backfill of <see cref="AudioFeaturesEntity.MusicStartSeconds"/> for tracks analysed before
/// the detector existed, computed cheaply from the stored waveform blob (no decode). Runs once
/// (marker file), with a consistent SQLite backup first — the same safety pattern as
/// <see cref="BeatGridRecomputeService"/>. A user-placed marker (<c>MusicStartSource == "User"</c>)
/// is never overwritten.
/// </summary>
public sealed class MusicStartBackfillService
{
    // v2: noise-floor threshold, Bar-1 plausibility cap, "Estimated" source (display-only). v1 used a
    // 5%-of-peak threshold that read quiet intros as silence and stored the result as "Analysis".
    private const string PassName = "music-start-v2";
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(120);

    private readonly ILogger<MusicStartBackfillService> _logger;
    private readonly DatabaseService _database;
    private readonly IEventBus _eventBus;

    public MusicStartBackfillService(ILogger<MusicStartBackfillService> logger, DatabaseService database, IEventBus eventBus)
    {
        _logger = logger;
        _database = database;
        _eventBus = eventBus;
    }

    /// <summary>Where the backup and the "done" marker live. Overridable in tests.</summary>
    internal string DataDirectory { get; set; } = Path.Combine(OrbitPaths.RoamingRoot, "ORBIT", "Backups");

    private string MarkerPath => Path.Combine(DataDirectory, $"{PassName}.done");

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
            catch (OperationCanceledException)
            {
                // intentional: app shutdown cancels the one-off pass; no marker is written, so it retries next launch.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MusicStartBackfill] Pass failed; it will retry on next launch.");
            }
        }, ct);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (File.Exists(MarkerPath)) return;

        List<AudioFeaturesEntity> candidates;
        using (var db = new AppDbContext())
        {
            candidates = await db.AudioFeatures.AsNoTracking()
                .Where(f => f.WaveformBlob != null && f.WaveformBlobSampleCount > 0 && f.TrackDuration > 0
                            && (f.MusicStartSeconds == null || f.MusicStartSource == MusicStartDetector.SourceEstimated))
                .ToListAsync(ct).ConfigureAwait(false);
        }
        if (candidates.Count == 0) { WriteMarker("no candidates"); return; }

        string backup = BackupLibrary();
        _logger.LogInformation("[MusicStartBackfill] Estimating music start for {Count} tracks (backup: {Backup})", candidates.Count, backup);

        int filled = 0;
        foreach (var f in candidates)
        {
            ct.ThrowIfCancellationRequested();
            double? barOne = f.DownbeatOffsetSeconds > 0 ? f.DownbeatOffsetSeconds : null;
            double? musicStart = MusicStartDetector.DetectFromWaveformBlob(f.WaveformBlob!, f.WaveformBlobSampleCount, f.TrackDuration, barOne);
            if (musicStart is null) continue;

            using var context = new AppDbContext();
            var existing = await context.AudioFeatures.FirstOrDefaultAsync(x => x.TrackUniqueHash == f.TrackUniqueHash, ct).ConfigureAwait(false);
            if (existing is null
                || existing.MusicStartSource is MusicStartDetector.SourceUser or MusicStartDetector.SourceAnalysis) continue;
            existing.MusicStartSeconds = (float)musicStart.Value;
            existing.MusicStartSource = MusicStartDetector.SourceEstimated;
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            filled++;
        }

        string summary = $"{filled} tracks backfilled with an estimated music start";
        _logger.LogInformation("[MusicStartBackfill] Done: {Summary}", summary);
        WriteMarker(summary);

        if (filled > 0)
            _eventBus.Publish(new ToastRequestedEvent(
                "Music start estimated",
                $"Estimated the music start for {filled} tracks from their stored waveform.",
                NotificationType.Success, TimeSpan.FromSeconds(8)));
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
            _logger.LogWarning(ex, "[MusicStartBackfill] Could not write marker {Path}", MarkerPath);
        }
    }
}
