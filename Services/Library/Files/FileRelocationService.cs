using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Events;

namespace SLSKDONET.Services.Library.Files;

/// <summary>One library file to relocate: the shared track hash plus its current and target paths.</summary>
public sealed record RelocationRequest(string Hash, string SourcePath, string TargetPath);

/// <summary>A relocation that could not be planned (or executed) and why.</summary>
public sealed record RelocationProblem(string Hash, string Reason);

/// <summary>The validated, safe subset of a relocation batch, plus the problems found up front.</summary>
public sealed record RelocationPlan(IReadOnlyList<RelocationRequest> Moves, IReadOnlyList<RelocationProblem> Problems);

/// <summary>The result of executing a plan: how many moved, the per-file failures, and the journal batch id for undo.</summary>
public sealed record RelocationOutcome(int Moved, IReadOnlyList<RelocationProblem> Failures, Guid BatchId = default);

/// <summary>Preview of a bulk folder re-point operation for missing files (LP2 #553).</summary>
public sealed record RelocateFolderPreview(
    string OldPrefix,
    string NewPrefix,
    int TotalMissingInLibrary,
    int MatchedPrefixCount,
    int FoundCount,
    int StillMissingCount,
    int PathLockedCount,
    RelocationPlan Plan,
    IReadOnlyList<RelocationRequest> SampleMatches,
    string? WarningMessage);

/// <summary>
/// The single move/rename path for library tracks (#344, LF2): moves the audio file and updates
/// every path column (LibraryEntries.FilePath, PlaylistTracks.ResolvedFilePath, Tracks.LocalFilePath)
/// in one EF transaction, then publishes a refresh event.
/// Extended in LP2 (#553) with a re-point mode for missing files moved outside ORBIT.
/// </summary>
public class FileRelocationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<FileRelocationService> _logger;

    public FileRelocationService(IDbContextFactory<AppDbContext> dbFactory, IEventBus eventBus, ILogger<FileRelocationService> logger)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>Validates a batch up front: missing sources, destination collisions and path-length violations.</summary>
    public RelocationPlan Plan(IReadOnlyList<RelocationRequest> requests)
    {
        var moves = new List<RelocationRequest>();
        var problems = new List<RelocationProblem>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in requests)
        {
            if (!File.Exists(r.SourcePath))
            {
                problems.Add(new RelocationProblem(r.Hash, "Source file not found"));
                continue;
            }

            if (string.Equals(r.SourcePath, r.TargetPath, StringComparison.OrdinalIgnoreCase))
                continue; // already in place — nothing to do

            if (!targets.Add(r.TargetPath))
            {
                problems.Add(new RelocationProblem(r.Hash, "Destination collision"));
                continue;
            }

            if (r.TargetPath.Length > 260)
            {
                problems.Add(new RelocationProblem(r.Hash, "Path too long"));
                continue;
            }

            moves.Add(r);
        }

        return new RelocationPlan(moves, problems);
    }

    /// <summary>
    /// Validates a batch up front AND applies LF4 path protection: a locked track (PathLockedAt set,
    /// e.g. first Rekordbox/USB export) and a linked-folder track (FileOrigin == LinkedFolder) are
    /// reported as problems instead of being auto-moved.
    /// </summary>
    public async Task<RelocationPlan> PlanAsync(IReadOnlyList<RelocationRequest> requests, CancellationToken ct = default)
    {
        var plan = Plan(requests);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var hashes = plan.Moves.Select(m => m.Hash).Distinct(StringComparer.Ordinal).ToList();
        var entries = hashes.Count == 0
            ? new Dictionary<string, LibraryEntryEntity>(StringComparer.Ordinal)
            : await db.LibraryEntries.Where(e => hashes.Contains(e.UniqueHash))
                .ToDictionaryAsync(e => e.UniqueHash, StringComparer.Ordinal, ct).ConfigureAwait(false);

        var moves = new List<RelocationRequest>(plan.Moves.Count);
        var problems = new List<RelocationProblem>(plan.Problems);
        foreach (var move in plan.Moves)
        {
            if (entries.TryGetValue(move.Hash, out var entry))
            {
                if (entry.PathLockedAt is not null)
                {
                    problems.Add(new RelocationProblem(move.Hash, "Path is locked"));
                    continue;
                }
                if (string.Equals(entry.FileOrigin, FileOriginDeriver.LinkedFolder, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add(new RelocationProblem(move.Hash, "Linked folder — not auto-moved"));
                    continue;
                }
            }
            moves.Add(move);
        }

        return new RelocationPlan(moves, problems);
    }

    /// <summary>Executes a plan: moves each file, updates the three path columns, journals the move, and publishes one refresh event.</summary>
    public async Task<RelocationOutcome> ExecuteAsync(RelocationPlan plan, CancellationToken ct = default)
    {
        var failures = new List<RelocationProblem>();
        var journal = new List<LibraryActionLogEntity>();
        var batchId = Guid.NewGuid();
        var moved = 0;

        foreach (var move in plan.Moves)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await MoveFileAsync(move.SourcePath, move.TargetPath, ct).ConfigureAwait(false);
                try
                {
                    await UpdatePathsAsync(move.Hash, move.TargetPath, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Roll the file move back so disk and DB never diverge (#344).
                    try
                    {
                        await MoveFileAsync(move.TargetPath, move.SourcePath, ct).ConfigureAwait(false);
                    }
                    catch (Exception rollbackEx)
                    {
                        _logger.LogError(rollbackEx, "Rollback failed for {Hash} after a DB write failure", move.Hash);
                    }
                    throw;
                }
                // Best-effort: move sidecar stem folders alongside (a failure here doesn't undo the track move).
                try
                {
                    await MoveStemFoldersAsync(move.SourcePath, move.TargetPath, ct).ConfigureAwait(false);
                }
                catch (Exception stemEx)
                {
                    _logger.LogWarning(stemEx, "Stem folder move failed for {Hash}; the track moved but its stem folder may be orphaned", move.Hash);
                }
                journal.Add(new LibraryActionLogEntity
                {
                    BatchId = batchId,
                    ActionType = IsRename(move) ? LibraryActionType.Rename : LibraryActionType.ManualMove,
                    SourcePath = move.SourcePath,
                    DestinationPath = move.TargetPath,
                });
                moved++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Relocation failed for {Hash}: {Source} -> {Target}", move.Hash, move.SourcePath, move.TargetPath);
                failures.Add(new RelocationProblem(move.Hash, ex.Message));
            }
        }

        if (moved > 0)
        {
            await WriteJournalAsync(journal, ct).ConfigureAwait(false);
            _eventBus.Publish(new LibraryUpdatedEvent("library files relocated"));
        }

        return new RelocationOutcome(moved, failures, batchId);
    }

    private static bool IsRename(RelocationRequest move) =>
        string.Equals(Path.GetDirectoryName(move.SourcePath), Path.GetDirectoryName(move.TargetPath), StringComparison.OrdinalIgnoreCase);

    private async Task MoveFileAsync(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            File.Move(source, target);
        }
        catch (IOException)
        {
            // Cross-volume (or a transient handle) — copy, verify size, then delete the source.
            await Task.Run(() => File.Copy(source, target, overwrite: true), ct).ConfigureAwait(false);
            var src = new FileInfo(source);
            var dst = new FileInfo(target);
            if (!dst.Exists || dst.Length != src.Length)
                throw new IOException($"Copy verify failed for {target} (size mismatch)");
            File.Delete(source);
        }
    }

    /// <summary>Moves the sidecar stem folders (Stems/&lt;name&gt;/, &lt;name&gt;_Stems/, _stems/&lt;name&gt;/) alongside a relocated track (#344).</summary>
    private async Task MoveStemFoldersAsync(string sourceTrack, string targetTrack, CancellationToken ct)
    {
        var sourceDir = Path.GetDirectoryName(sourceTrack);
        var targetDir = Path.GetDirectoryName(targetTrack);
        if (string.IsNullOrEmpty(sourceDir) || string.IsNullOrEmpty(targetDir)) return;

        var sourceName = Path.GetFileNameWithoutExtension(sourceTrack);
        var targetName = Path.GetFileNameWithoutExtension(targetTrack);
        if (sourceName == targetName) return; // same filename — the folders don't change

        var patterns = new (string Source, string Target)[]
        {
            (Path.Combine(sourceDir, "Stems", sourceName), Path.Combine(targetDir, "Stems", targetName)),
            (Path.Combine(sourceDir, sourceName + "_Stems"), Path.Combine(targetDir, targetName + "_Stems")),
            (Path.Combine(sourceDir, "_stems", sourceName), Path.Combine(targetDir, "_stems", targetName)),
        };

        foreach (var (source, target) in patterns)
        {
            if (Directory.Exists(source))
                await MoveDirectoryAsync(source, target, ct).ConfigureAwait(false);
        }
    }

    private async Task MoveDirectoryAsync(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            Directory.Move(source, target);
        }
        catch (IOException)
        {
            // Cross-volume — copy recursively, then delete the source.
            await Task.Run(() => CopyDirectory(source, target), ct).ConfigureAwait(false);
            Directory.Delete(source, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private async Task UpdatePathsAsync(string hash, string newPath, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == hash, ct).ConfigureAwait(false);
        if (entry != null)
        {
            entry.FilePath = newPath;
            entry.FilePathUpdatedAt = DateTime.UtcNow;
        }

        var playlistTracks = await db.PlaylistTracks.Where(t => t.TrackUniqueHash == hash).ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in playlistTracks)
            t.ResolvedFilePath = newPath;

        var tracks = await db.Tracks.Where(t => t.GlobalId == hash).ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in tracks)
            t.LocalFilePath = newPath;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteJournalAsync(IReadOnlyList<LibraryActionLogEntity> journal, CancellationToken ct)
    {
        if (journal.Count == 0) return;
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.LibraryActionLogs.AddRange(journal);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reverts a journaled relocation batch: for each logged move (in reverse order) the file is moved
    /// back to its source and the three path columns are restored. Returns how many were reverted.
    /// </summary>
    public async Task<int> RevertBatchAsync(Guid batchId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var logs = await db.LibraryActionLogs
            .Where(l => l.BatchId == batchId)
            .OrderByDescending(l => l.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var reverted = 0;
        foreach (var log in logs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RevertFileAsync(log, ct).ConfigureAwait(false);
                reverted++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Undo failed for {Source} -> {Destination}", log.SourcePath, log.DestinationPath);
            }
        }
        return reverted;
    }

    /// <summary>
    /// Suggests the most common parent folder prefixes among missing library files (LP2 #553).
    /// </summary>
    public async Task<IReadOnlyList<(string Prefix, int MissingCount)>> SuggestMissingPrefixesAsync(int maxSuggestions = 10, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entries = await db.LibraryEntries
            .AsNoTracking()
            .Where(e => !string.IsNullOrEmpty(e.FilePath))
            .Select(e => e.FilePath)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in entries)
        {
            if (File.Exists(path)) continue;
            var dir = GetParentDirectoryNormalized(path);
            if (string.IsNullOrEmpty(dir)) continue;
            counts[dir] = counts.TryGetValue(dir, out var c) ? c + 1 : 1;
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxSuggestions))
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Previews bulk re-pointing of missing files from <paramref name="oldPrefix"/> to
    /// <paramref name="newPrefix"/> (LP2 #553). Handles Windows/POSIX separators and case differences.
    /// Nothing is written until <see cref="ExecuteRepointAsync"/> is called.
    /// </summary>
    public async Task<RelocateFolderPreview> PlanRepointFolderAsync(
        string oldPrefix,
        string newPrefix,
        int sampleLimit = 10,
        CancellationToken ct = default)
    {
        var normalizedOld = NormalizePrefix(oldPrefix);
        var normalizedNew = NormalizePrefix(newPrefix);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entries = await db.LibraryEntries
            .AsNoTracking()
            .Where(e => !string.IsNullOrEmpty(e.FilePath))
            .Select(e => new { e.UniqueHash, e.FilePath, e.PathLockedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var totalMissing = 0;
        var matchedPrefix = 0;
        var stillMissing = 0;
        var pathLockedCount = 0;
        var moves = new List<RelocationRequest>();
        var problems = new List<RelocationProblem>();

        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(e.FilePath)) continue;
            totalMissing++;

            if (!TryRemapPrefix(e.FilePath, normalizedOld, normalizedNew, out var candidateTarget))
                continue;

            matchedPrefix++;
            if (File.Exists(candidateTarget))
            {
                moves.Add(new RelocationRequest(e.UniqueHash, e.FilePath, candidateTarget));
                if (e.PathLockedAt is not null)
                    pathLockedCount++;
            }
            else
            {
                stillMissing++;
                problems.Add(new RelocationProblem(e.UniqueHash, $"Not found at {candidateTarget}"));
            }
        }

        string? warning = pathLockedCount > 0
            ? $"{pathLockedCount} path-locked track(s) will be re-pointed; Rekordbox/USB exports still reference their old paths."
            : null;

        var plan = new RelocationPlan(moves, problems);
        return new RelocateFolderPreview(
            OldPrefix: oldPrefix,
            NewPrefix: newPrefix,
            TotalMissingInLibrary: totalMissing,
            MatchedPrefixCount: matchedPrefix,
            FoundCount: moves.Count,
            StillMissingCount: stillMissing,
            PathLockedCount: pathLockedCount,
            Plan: plan,
            SampleMatches: moves.Take(Math.Max(1, sampleLimit)).ToList(),
            WarningMessage: warning);
    }

    /// <summary>
    /// Executes a re-point plan (LP2 #553): updates all three DB path columns and writes a
    /// <see cref="LibraryActionType.Repoint"/> journal batch WITHOUT moving any file on disk.
    /// Only rows whose target file exists on disk are re-pointed.
    /// </summary>
    public async Task<RelocationOutcome> ExecuteRepointAsync(RelocationPlan plan, CancellationToken ct = default)
    {
        var failures = new List<RelocationProblem>();
        var journal = new List<LibraryActionLogEntity>();
        var batchId = Guid.NewGuid();
        var repointed = 0;

        foreach (var move in plan.Moves)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(move.TargetPath))
            {
                failures.Add(new RelocationProblem(move.Hash, "Target file not found on disk"));
                continue;
            }

            try
            {
                await UpdatePathsAsync(move.Hash, move.TargetPath, ct).ConfigureAwait(false);
                journal.Add(new LibraryActionLogEntity
                {
                    BatchId = batchId,
                    ActionType = LibraryActionType.Repoint,
                    SourcePath = move.SourcePath,
                    DestinationPath = move.TargetPath,
                });
                repointed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Repoint failed for {Hash}: {Source} -> {Target}", move.Hash, move.SourcePath, move.TargetPath);
                failures.Add(new RelocationProblem(move.Hash, ex.Message));
            }
        }

        if (repointed > 0)
        {
            await WriteJournalAsync(journal, ct).ConfigureAwait(false);
            _eventBus.Publish(new LibraryUpdatedEvent("library files repointed"));
        }

        return new RelocationOutcome(repointed, failures, batchId);
    }

    /// <summary>
    /// Remaps <paramref name="sourcePath"/> from <paramref name="oldPrefix"/> to <paramref name="newPrefix"/>,
    /// handling Windows (\) and POSIX (/) separators and case differences.
    /// </summary>
    public static bool TryRemapPrefix(string sourcePath, string oldPrefix, string newPrefix, out string targetPath)
    {
        targetPath = string.Empty;
        var normSource = NormalizeSeparators(sourcePath);
        var normOld = NormalizePrefix(oldPrefix);
        var normNew = NormalizePrefix(newPrefix);
        if (string.IsNullOrEmpty(normOld) || string.IsNullOrEmpty(normNew))
            return false;

        // Match exact directory boundary (either exact prefix + "/" or prefix ends with "/")
        var prefixWithSlash = normOld.EndsWith('/') ? normOld : normOld + "/";
        if (!normSource.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase))
            return false;

        var relative = normSource[prefixWithSlash.Length..];
        var newBase = normNew.TrimEnd('/');
        var combined = string.IsNullOrEmpty(newBase)
            ? "/" + relative
            : newBase + "/" + relative;

        targetPath = Path.DirectorySeparatorChar == '\\'
            ? combined.Replace('/', '\\')
            : combined;
        return true;
    }

    private static string NormalizeSeparators(string path) =>
        (path ?? string.Empty).Trim().Replace('\\', '/');

    private static string NormalizePrefix(string prefix)
    {
        var norm = NormalizeSeparators(prefix);
        return norm.Length > 1 ? norm.TrimEnd('/') : norm;
    }

    private static string GetParentDirectoryNormalized(string path)
    {
        var norm = NormalizeSeparators(path);
        var idx = norm.LastIndexOf('/');
        return idx > 0 ? norm[..idx] : string.Empty;
    }

    private async Task RevertFileAsync(LibraryActionLogEntity log, CancellationToken ct)
    {
        // Only move the file back for actual file moves (never for Repoint entries, LP2 #553).
        if (log.ActionType != LibraryActionType.Repoint
            && File.Exists(log.DestinationPath)
            && !File.Exists(log.SourcePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log.SourcePath)!);
            File.Move(log.DestinationPath, log.SourcePath);
        }

        // Restore every path column that points at the destination back to the source (path-keyed,
        // since the journal ledger records paths rather than the track hash).
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entries = await db.LibraryEntries.Where(e => e.FilePath == log.DestinationPath).ToListAsync(ct).ConfigureAwait(false);
        foreach (var e in entries)
        {
            e.FilePath = log.SourcePath;
            e.FilePathUpdatedAt = DateTime.UtcNow;
        }

        var playlistTracks = await db.PlaylistTracks.Where(t => t.ResolvedFilePath == log.DestinationPath).ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in playlistTracks)
            t.ResolvedFilePath = log.SourcePath;

        var tracks = await db.Tracks.Where(t => t.LocalFilePath == log.DestinationPath).ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in tracks)
            t.LocalFilePath = log.SourcePath;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
