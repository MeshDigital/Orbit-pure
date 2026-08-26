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
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// One-time cleanup for two confirmed duplicate-data bugs (see <c>ImportOrchestrator</c>'s fresh-import
/// dedup and <c>LibraryService.AddTracksToProjectAsync</c>'s dedup guard for the code-level fixes that
/// stop new duplicates from forming):
///
/// 1. The same track inserted multiple times into the same playlist (same PlaylistId + TrackUniqueHash).
/// 2. The same physical file registered twice in LibraryEntries under two different UniqueHash values —
///    a legacy random-GUID hash format alongside the current deterministic artist-title hash format
///    (<see cref="LibraryFolderScannerService"/>'s BuildLibraryUniqueHash).
///
/// Structured like <see cref="UnidentifiedTrackCleanupService"/>: idempotent (safe to re-run — finds
/// nothing on a clean database), child-rows-first deletion, collects per-step errors rather than
/// aborting, and takes its own safety backup before touching anything.
/// </summary>
public sealed class DuplicateTrackCleanupService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<DuplicateTrackCleanupService> _logger;

    public DuplicateTrackCleanupService(
        IDbContextFactory<AppDbContext> dbFactory,
        IEventBus eventBus,
        ILogger<DuplicateTrackCleanupService> logger)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<DuplicateCleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        var backupPath = TryCreateSafetyBackup();
        if (backupPath == null)
        {
            const string reason = "Aborted: could not create a safety backup before cleanup (see log for details).";
            _logger.LogWarning("[DuplicateCleanup] {Reason}", reason);
            return new DuplicateCleanupResult(null, 0, 0, 0, 0, new List<string> { reason });
        }

        var errors = new List<string>();
        int libraryEntriesMerged = 0;
        int playlistRowsRemoved = 0, queueItemsRemoved = 0, technicalDetailsRemoved = 0;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            libraryEntriesMerged = await MergeDuplicateLibraryEntriesAsync(db, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add($"LibraryEntries merge: {ex.Message}");
            _logger.LogError(ex, "[DuplicateCleanup] Failed merging duplicate LibraryEntries");
        }

        try
        {
            (playlistRowsRemoved, queueItemsRemoved, technicalDetailsRemoved) =
                await RemoveDuplicatePlaylistTracksAsync(db, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add($"PlaylistTracks dedup: {ex.Message}");
            _logger.LogError(ex, "[DuplicateCleanup] Failed removing duplicate PlaylistTracks");
        }

        var result = new DuplicateCleanupResult(
            backupPath, libraryEntriesMerged, playlistRowsRemoved, queueItemsRemoved, technicalDetailsRemoved, errors);

        _logger.LogInformation("[DuplicateCleanup] {Summary}", result.Summary);
        return result;
    }

    /// <summary>
    /// Step B — same file registered twice under two different UniqueHash values. Keeps the row
    /// matching the current deterministic hash format, repoints every hash-keyed child row to the
    /// survivor (never dropping data silently), then deletes the retired LibraryEntries row.
    /// </summary>
    private async Task<int> MergeDuplicateLibraryEntriesAsync(AppDbContext db, CancellationToken ct)
    {
        var entries = await db.LibraryEntries
            .Select(e => new { e.UniqueHash, e.FilePath })
            .ToListAsync(ct).ConfigureAwait(false);

        var duplicateGroups = entries
            .GroupBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        var merged = 0;

        foreach (var group in duplicateGroups)
        {
            ct.ThrowIfCancellationRequested();

            // Prefer the row whose hash is NOT GUID-shaped — that's the current deterministic
            // artist-title format; a GUID-shaped hash is the legacy scheme being retired.
            var ordered = group.OrderBy(e => Guid.TryParse(e.UniqueHash, out _) ? 1 : 0).ToList();
            var survivorHash = ordered[0].UniqueHash;

            foreach (var loser in ordered.Skip(1))
            {
                await RepointHashReferencesAsync(db, loser.UniqueHash, survivorHash, ct).ConfigureAwait(false);

                var deleted = await db.LibraryEntries
                    .Where(e => e.UniqueHash == loser.UniqueHash)
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);

                if (deleted > 0)
                {
                    merged++;
                    _eventBus.Publish(new LibraryEntryDeletedEvent(loser.UniqueHash));
                }
            }
        }

        return merged;
    }

    private static async Task RepointHashReferencesAsync(AppDbContext db, string fromHash, string toHash, CancellationToken ct)
    {
        // Many-rows-per-hash tables (multiple cue points / analysis runs per track are normal):
        // always repoint every row, no collision risk.
        await db.PlaylistTracks.Where(t => t.TrackUniqueHash == fromHash)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.TrackUniqueHash, toHash), ct).ConfigureAwait(false);
        await db.CuePoints.Where(c => c.TrackUniqueHash == fromHash)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.TrackUniqueHash, toHash), ct).ConfigureAwait(false);
        await db.AnalysisRuns.Where(a => a.TrackUniqueHash == fromHash)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.TrackUniqueHash, toHash), ct).ConfigureAwait(false);

        // One-row-per-hash tables: repoint only if the survivor doesn't already have a row there
        // (avoids a unique-constraint collision, e.g. audio_features.TrackUniqueHash is UNIQUE) —
        // otherwise the survivor's existing data wins and the loser's copy is simply dropped rather
        // than left orphaned (none of these three are FK-cascaded from LibraryEntries in the real
        // schema, so nothing else will clean them up).
        if (!await db.StemPreferences.AnyAsync(s => s.TrackUniqueHash == toHash, ct).ConfigureAwait(false))
            await db.StemPreferences.Where(s => s.TrackUniqueHash == fromHash)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TrackUniqueHash, toHash), ct).ConfigureAwait(false);
        else
            await db.StemPreferences.Where(s => s.TrackUniqueHash == fromHash).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        if (!await db.AudioFeatures.AnyAsync(f => f.TrackUniqueHash == toHash, ct).ConfigureAwait(false))
            await db.AudioFeatures.Where(f => f.TrackUniqueHash == fromHash)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TrackUniqueHash, toHash), ct).ConfigureAwait(false);
        else
            await db.AudioFeatures.Where(f => f.TrackUniqueHash == fromHash).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        if (!await db.AudioAnalysis.AnyAsync(a => a.TrackUniqueHash == toHash, ct).ConfigureAwait(false))
            await db.AudioAnalysis.Where(a => a.TrackUniqueHash == fromHash)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TrackUniqueHash, toHash), ct).ConfigureAwait(false);
        else
            await db.AudioAnalysis.Where(a => a.TrackUniqueHash == fromHash).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Step A — the same track inserted more than once into the same playlist. Keeps the
    /// Downloaded copy if one exists (it has a real file), else the earliest-added row. Runs after
    /// <see cref="MergeDuplicateLibraryEntriesAsync"/> in <see cref="RunAsync"/> on purpose: its
    /// PlaylistTracks.TrackUniqueHash repoint can itself create a fresh same-playlist collision if
    /// that playlist already had a row under the surviving hash, which this pass then cleans up too.
    /// </summary>
    private async Task<(int playlistRowsRemoved, int queueItemsRemoved, int technicalDetailsRemoved)> RemoveDuplicatePlaylistTracksAsync(
        AppDbContext db, CancellationToken ct)
    {
        var rows = await db.PlaylistTracks
            .Select(t => new { t.Id, t.PlaylistId, t.TrackUniqueHash, t.Status, t.AddedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        var loserIds = rows
            .Where(t => !string.IsNullOrEmpty(t.TrackUniqueHash))
            .GroupBy(t => (t.PlaylistId, t.TrackUniqueHash))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g
                .OrderByDescending(t => t.Status == TrackStatus.Downloaded)
                .ThenBy(t => t.AddedAt)
                .Skip(1)
                .Select(t => t.Id))
            .ToList();

        if (loserIds.Count == 0) return (0, 0, 0);

        var technicalDetailsRemoved = await db.TechnicalDetails
            .Where(td => loserIds.Contains(td.PlaylistTrackId))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var queueItemsRemoved = await db.QueueItems
            .Where(q => loserIds.Contains(q.PlaylistTrackId))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var playlistRowsRemoved = await db.PlaylistTracks
            .Where(t => loserIds.Contains(t.Id))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return (playlistRowsRemoved, queueItemsRemoved, technicalDetailsRemoved);
    }

    /// <summary>
    /// Plain file-copy backup, same mechanism and health gate as
    /// <c>SchemaMigratorService.PerformBackupAsync</c> — refuses to back up (and thus refuses to
    /// run the cleanup at all, see <see cref="RunAsync"/>) if the live database fails a basic
    /// integrity check.
    /// </summary>
    private string? TryCreateSafetyBackup()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dbPath = Path.Combine(appData, "ORBIT", "library.db");
            var backupDir = Path.Combine(appData, "ORBIT", "Backups");

            if (!File.Exists(dbPath))
            {
                _logger.LogWarning("[DuplicateCleanup] No database found at {Path}", dbPath);
                return null;
            }

            if (!IsSqliteDatabaseHealthy(dbPath))
            {
                _logger.LogWarning("[DuplicateCleanup] Skipping cleanup — database failed integrity check: {Path}", dbPath);
                return null;
            }

            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var backupPath = Path.Combine(backupDir, $"library.backup.pre-dedup-cleanup.{timestamp}.db");
            File.Copy(dbPath, backupPath, overwrite: true);

            _logger.LogInformation("[DuplicateCleanup] Safety backup created at {Path}", backupPath);
            return backupPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DuplicateCleanup] Failed to create safety backup");
            return null;
        }
    }

    private bool IsSqliteDatabaseHealthy(string dbPath)
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check(1);";
            var result = cmd.ExecuteScalar()?.ToString();
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DuplicateCleanup] SQLite health check failed for {DbPath}", dbPath);
            return false;
        }
    }
}

public record DuplicateCleanupResult(
    string? BackupPath,
    int LibraryEntriesMerged,
    int PlaylistRowsRemoved,
    int QueueItemsRemoved,
    int TechnicalDetailsRemoved,
    IReadOnlyList<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
    public bool Aborted => BackupPath == null;
    public string Summary => Aborted
        ? "Cleanup aborted before making changes — see errors."
        : $"Merged {LibraryEntriesMerged} duplicate library file(s), removed {PlaylistRowsRemoved} duplicate playlist track row(s) " +
          $"({QueueItemsRemoved} queue item(s), {TechnicalDetailsRemoved} technical-detail row(s) cleaned up alongside)" +
          (HasErrors ? $", {Errors.Count} error(s)" : string.Empty);
}
