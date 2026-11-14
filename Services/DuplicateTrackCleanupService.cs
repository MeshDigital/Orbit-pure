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

        var backupPath = TryCreateSafetyBackup("pre-dedup-cleanup");
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

    /// <summary>
    /// Exact-duplicate merge (issue #238): the same audio stored as several library entries under
    /// different files, tags or folders. For every plan, folds each duplicate into the keeper —
    /// playlist memberships, set slots, cues the keeper doesn't already have, and analysis only the
    /// duplicate has — then removes the duplicate's library entry. Takes a safety backup first and
    /// aborts without changes if it can't. Each group runs in its own transaction, so a failing
    /// group rolls back on its own and is reported instead of aborting the rest.
    /// <para>Never touches files on disk; <see cref="ExactDuplicateMergeResult.RemovedFiles"/> lists
    /// the duplicates' paths so the caller can offer the opt-in Recycle Bin step.</para>
    /// </summary>
    public async Task<ExactDuplicateMergeResult> MergeIntoKeepersAsync(
        IReadOnlyList<ExactDuplicateMergePlan> plans, CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        var backupPath = TryCreateSafetyBackup("pre-exact-duplicate-merge");
        if (backupPath == null)
        {
            const string reason = "Aborted: could not create a safety backup before merging (see log for details).";
            _logger.LogWarning("[DuplicateCleanup] {Reason}", reason);
            return new ExactDuplicateMergeResult(null, 0, 0, Array.Empty<RemovedDuplicateFile>(), new List<string> { reason });
        }

        var errors = new List<string>();
        var removedFiles = new List<RemovedDuplicateFile>();
        int groupsMerged = 0, entriesRemoved = 0;

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var losers = plan.DuplicateHashes
                .Where(h => !string.IsNullOrEmpty(h) && !string.Equals(h, plan.KeeperHash, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (losers.Count == 0) continue;

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var keeper = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == plan.KeeperHash, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("the track to keep is no longer in the library");

                var removedHere = new List<(string Hash, string Path)>();
                foreach (var loserHash in losers)
                {
                    var loser = await db.LibraryEntries.AsNoTracking()
                        .FirstOrDefaultAsync(e => e.UniqueHash == loserHash, cancellationToken).ConfigureAwait(false);
                    if (loser == null) continue; // already gone (merged elsewhere): nothing to fold in

                    FillMissingFromDuplicate(keeper, loser);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    await RepointHashReferencesAsync(db, loserHash, plan.KeeperHash, cancellationToken).ConfigureAwait(false);

                    var deleted = await db.LibraryEntries.Where(e => e.UniqueHash == loserHash)
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                    if (deleted > 0) removedHere.Add((loserHash, loser.FilePath));
                }

                // Repointing can leave a playlist holding the keeper twice; keep one row per playlist.
                await RemoveDuplicatePlaylistTracksAsync(db, cancellationToken, plan.KeeperHash).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

                if (removedHere.Count > 0) groupsMerged++;
                entriesRemoved += removedHere.Count;
                foreach (var (hash, path) in removedHere)
                {
                    _eventBus.Publish(new LibraryEntryDeletedEvent(hash));
                    if (!string.IsNullOrWhiteSpace(path))
                        removedFiles.Add(new RemovedDuplicateFile(hash, path, keeper.FilePath));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                errors.Add($"{plan.KeeperHash}: {ex.Message}");
                _logger.LogError(ex, "[DuplicateCleanup] Failed merging exact duplicates into {Keeper}", plan.KeeperHash);
            }
        }

        var result = new ExactDuplicateMergeResult(backupPath, groupsMerged, entriesRemoved, removedFiles, errors);
        _logger.LogInformation("[DuplicateCleanup] Exact duplicates: {Summary}", result.Summary);
        return result;
    }

    /// <summary>Copies analysis and metadata the keeper lacks from a duplicate of the same audio.
    /// Never overwrites a value the keeper already has.</summary>
    internal static void FillMissingFromDuplicate(LibraryEntryEntity keeper, LibraryEntryEntity duplicate)
    {
        keeper.BPM ??= duplicate.BPM;
        if (string.IsNullOrWhiteSpace(keeper.MusicalKey)) keeper.MusicalKey = duplicate.MusicalKey;
        if (string.IsNullOrWhiteSpace(keeper.Genres) || keeper.Genres == "[]") keeper.Genres = duplicate.Genres;
        if (string.IsNullOrWhiteSpace(keeper.Label)) keeper.Label = duplicate.Label;
        if (string.IsNullOrWhiteSpace(keeper.Comments)) keeper.Comments = duplicate.Comments;
        if (string.IsNullOrWhiteSpace(keeper.ColorTag)) keeper.ColorTag = duplicate.ColorTag;
        if (string.IsNullOrWhiteSpace(keeper.ISRC)) keeper.ISRC = duplicate.ISRC;
        if (string.IsNullOrWhiteSpace(keeper.SpotifyTrackId)) keeper.SpotifyTrackId = duplicate.SpotifyTrackId;
        if (string.IsNullOrWhiteSpace(keeper.AlbumArtUrl)) keeper.AlbumArtUrl = duplicate.AlbumArtUrl;
        if (keeper.WaveformData is not { Length: > 0 } && duplicate.WaveformData is { Length: > 0 })
        {
            keeper.WaveformData = duplicate.WaveformData;
            keeper.RmsData = duplicate.RmsData;
            keeper.LowData = duplicate.LowData;
            keeper.MidData = duplicate.MidData;
            keeper.HighData = duplicate.HighData;
        }
        if (keeper.AnalysisStatus != AnalysisStatus.Completed && duplicate.AnalysisStatus == AnalysisStatus.Completed)
            keeper.AnalysisStatus = AnalysisStatus.Completed;
        if (duplicate.AddedAt != default && (keeper.AddedAt == default || duplicate.AddedAt < keeper.AddedAt))
            keeper.AddedAt = duplicate.AddedAt; // the track has been in the library since the older copy
    }

    private static async Task RepointHashReferencesAsync(AppDbContext db, string fromHash, string toHash, CancellationToken ct)
    {
        var pair = await db.LibraryEntries.AsNoTracking()
            .Where(e => e.UniqueHash == fromHash || e.UniqueHash == toHash)
            .Select(e => new { e.UniqueHash, e.Id, e.FilePath, e.Format, e.Bitrate })
            .ToListAsync(ct).ConfigureAwait(false);
        var from = pair.FirstOrDefault(e => e.UniqueHash == fromHash);
        var to = pair.FirstOrDefault(e => e.UniqueHash == toHash);

        // Playlist rows of the retired entry point at its file; when the survivor lives in another
        // file (exact duplicates), the rows must follow it or they'd play / link the removed copy.
        if (from != null && to != null && !string.Equals(from.FilePath, to.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            await db.PlaylistTracks.Where(t => t.TrackUniqueHash == fromHash)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.ResolvedFilePath, to.FilePath)
                    .SetProperty(t => t.Format, to.Format)
                    .SetProperty(t => t.Bitrate, to.Bitrate), ct).ConfigureAwait(false);
        }

        await db.PlaylistTracks.Where(t => t.TrackUniqueHash == fromHash)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.TrackUniqueHash, toHash), ct).ConfigureAwait(false);
        await db.AnalysisRuns.Where(a => a.TrackUniqueHash == fromHash)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.TrackUniqueHash, toHash), ct).ConfigureAwait(false);

        // Set slots keep their place in the set and now reference the survivor.
        await db.SetTracks.Where(t => t.TrackUniqueHash == fromHash)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.TrackUniqueHash, toHash), ct).ConfigureAwait(false);
        if (from != null && to != null)
            await db.SetTracks.Where(t => t.LibraryId == from.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LibraryId, (Guid?)to.Id), ct).ConfigureAwait(false);

        await MergeCuesAsync(db, fromHash, toHash, ct).ConfigureAwait(false);

        // Phrases describe one analysis of the audio: keep the survivor's if it has any.
        if (await db.TrackPhrases.AnyAsync(p => p.TrackUniqueHash == toHash, ct).ConfigureAwait(false))
            await db.TrackPhrases.Where(p => p.TrackUniqueHash == fromHash).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        else
            await db.TrackPhrases.Where(p => p.TrackUniqueHash == fromHash)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.TrackUniqueHash, toHash), ct).ConfigureAwait(false);

        // Export snapshots belong to the retired entry's file; the next export merges afresh.
        await db.RekordboxExportCueSync.Where(c => c.TrackUniqueHash == fromHash).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // TrackUniqueHash is this table's primary key, so it is moved with plain SQL.
        if (!await db.TrackCrowdProfiles.AnyAsync(p => p.TrackUniqueHash == toHash, ct).ConfigureAwait(false))
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE TrackCrowdProfiles SET TrackUniqueHash = {toHash} WHERE TrackUniqueHash = {fromHash}", ct).ConfigureAwait(false);
        else
            await db.TrackCrowdProfiles.Where(p => p.TrackUniqueHash == fromHash).ExecuteDeleteAsync(ct).ConfigureAwait(false);

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

    /// <summary>Cue positions closer than this are the same cue (both copies were analysed).</summary>
    internal const double SameCueToleranceSeconds = 0.05;

    /// <summary>
    /// Union of both entries' cues on the survivor: a retired cue moves over unless the survivor
    /// already has one at the same spot (same loop-ness, within <see cref="SameCueToleranceSeconds"/>).
    /// A moved hot cue whose pad is taken on the survivor becomes a memory cue rather than
    /// fighting over the pad.
    /// </summary>
    private static async Task MergeCuesAsync(AppDbContext db, string fromHash, string toHash, CancellationToken ct)
    {
        var survivorCues = await db.CuePoints.AsNoTracking()
            .Where(c => c.TrackUniqueHash == toHash)
            .Select(c => new { c.TimestampInSeconds, c.IsLoop, c.SlotIndex })
            .ToListAsync(ct).ConfigureAwait(false);
        var retiredCues = await db.CuePoints.Where(c => c.TrackUniqueHash == fromHash).ToListAsync(ct).ConfigureAwait(false);
        if (retiredCues.Count == 0) return;

        var usedPads = survivorCues.Where(c => c.SlotIndex >= 0).Select(c => c.SlotIndex).ToHashSet();
        foreach (var cue in retiredCues)
        {
            var alreadyThere = survivorCues.Any(k => k.IsLoop == cue.IsLoop &&
                                                     Math.Abs(k.TimestampInSeconds - cue.TimestampInSeconds) < SameCueToleranceSeconds);
            if (alreadyThere)
            {
                db.CuePoints.Remove(cue);
                continue;
            }

            cue.TrackUniqueHash = toHash;
            if (cue.SlotIndex >= 0 && !usedPads.Add(cue.SlotIndex)) cue.SlotIndex = -1;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Step A — the same track inserted more than once into the same playlist. Keeps the
    /// Downloaded copy if one exists (it has a real file), else the earliest-added row. Runs after
    /// <see cref="MergeDuplicateLibraryEntriesAsync"/> in <see cref="RunAsync"/> on purpose: its
    /// PlaylistTracks.TrackUniqueHash repoint can itself create a fresh same-playlist collision if
    /// that playlist already had a row under the surviving hash, which this pass then cleans up too.
    /// </summary>
    private async Task<(int playlistRowsRemoved, int queueItemsRemoved, int technicalDetailsRemoved)> RemoveDuplicatePlaylistTracksAsync(
        AppDbContext db, CancellationToken ct, string? onlyTrackHash = null)
    {
        var query = onlyTrackHash == null ? db.PlaylistTracks : db.PlaylistTracks.Where(t => t.TrackUniqueHash == onlyTrackHash);
        var rows = await query
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
    /// Online-backup snapshot (<see cref="LibraryDatabaseMaintenance.CreateBackup"/>: a plain file
    /// copy can miss pages still in the WAL) into the Backups folder next to the database, so tests
    /// (ORBIT_DB_PATH) never write into the real one. Refuses to back up, and thus refuses to run
    /// the cleanup at all, if the database fails a basic integrity check.
    /// </summary>
    private string? TryCreateSafetyBackup(string label)
    {
        try
        {
            var dbPath = SLSKDONET.Data.OrbitPaths.LibraryDbPath;
            var backupDir = LibraryDatabaseMaintenance.GetBackupDirectory(dbPath);

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

            // Unique per call: two merges in the same second used to write the same file, and the
            // second backup failed, which aborted that merge (seen as a flaky test).
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff", System.Globalization.CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(backupDir, $"library.backup.{label}.{timestamp}.{Guid.NewGuid().ToString("N")[..6]}.db");
            LibraryDatabaseMaintenance.CreateBackup(dbPath, backupPath);

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

/// <summary>One exact-duplicate group to resolve: keep <paramref name="KeeperHash"/>, fold the rest into it.</summary>
public sealed record ExactDuplicateMergePlan(string KeeperHash, IReadOnlyList<string> DuplicateHashes);

/// <summary>A library entry removed by the merge, with its (still untouched) file on disk.</summary>
public sealed record RemovedDuplicateFile(string UniqueHash, string FilePath, string KeeperFilePath);

public sealed record ExactDuplicateMergeResult(
    string? BackupPath,
    int GroupsMerged,
    int EntriesRemoved,
    IReadOnlyList<RemovedDuplicateFile> RemovedFiles,
    IReadOnlyList<string> Errors)
{
    public bool Aborted => BackupPath == null;
    public bool HasErrors => Errors.Count > 0;
    public string Summary => Aborted
        ? "Merge aborted before making changes — no safety backup could be made."
        : $"Merged {GroupsMerged} group(s), removed {EntriesRemoved} entr{(EntriesRemoved == 1 ? "y" : "ies")}, {Errors.Count} error(s)";
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
