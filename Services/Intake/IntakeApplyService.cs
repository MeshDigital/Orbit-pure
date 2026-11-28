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

namespace SLSKDONET.Services.Intake;

public sealed record IntakeApplyProgress(int Done, int Total, string? CurrentFile);

/// <summary>What one Apply did.</summary>
public sealed record IntakeApplyResult(int Kept, int Replaced, int Skipped, int UpgradesQueued, int Failed)
{
    public string Summary =>
        string.Join(", ", new[]
        {
            Kept > 0 ? $"{Kept} added to the library" : null,
            Replaced > 0 ? $"{Replaced} replaced with a better copy" : null,
            UpgradesQueued > 0 ? $"{UpgradesQueued} searches for a better copy queued" : null,
            Skipped > 0 ? $"{Skipped} left on the drive" : null,
            Failed > 0 ? $"{Failed} failed" : null,
        }.Where(s => s != null)) is { Length: > 0 } s ? s : "Nothing to do";
}

/// <summary>
/// Carries out the user's Intake decisions (epic #632). The source is never modified: Keep copies
/// the file into the library at the user's path template, Replace copies it next to the library
/// first and then swaps it in through the quality-upgrade path (old file to ORBIT's trash, cues and
/// beatgrid kept), Skip leaves it on the drive, and Upgrade queues one Soulseek search project.
/// Each file's outcome is saved as it happens, so an interrupted Apply resumes where it stopped and
/// never copies a file twice.
/// </summary>
public sealed class IntakeApplyService
{
    private const int ImportBatchSize = 200;
    private const string PartSuffix = ".orbit-intake-part";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IIntakeLibraryWriter _writer;
    private readonly ILogger<IntakeApplyService> _logger;

    public IntakeApplyService(IDbContextFactory<AppDbContext> dbFactory, IIntakeLibraryWriter writer, ILogger<IntakeApplyService> logger)
    {
        _dbFactory = dbFactory;
        _writer = writer;
        _logger = logger;
    }

    /// <summary>Sets the action for files (the user overriding the pre-ticked one). Returns how many changed.</summary>
    public async Task<int> SetActionAsync(IReadOnlyCollection<string> itemIds, IntakeAction action, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var items = await db.IntakeItems.Where(i => itemIds.Contains(i.Id) && i.Status == IntakeItemStatus.Scanned).ToListAsync(ct).ConfigureAwait(false);
        int changed = 0;
        foreach (var i in items)
        {
            var allowed = AllowedActions(i);
            if (!allowed.Contains(action) || i.Action == action) continue;
            i.Action = action;
            changed++;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return changed;
    }

    /// <summary>Where kept files go (the library's download folder, filed by the naming template).</summary>
    public string LibraryRoot => _writer.LibraryRoot;

    /// <summary>Bytes Apply will copy (Add + Replace) and the free space where they go (null when unknown).</summary>
    public async Task<(long Needed, long? Free)> CheckSpaceAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var needed = await db.IntakeItems
            .Where(i => i.SessionId == sessionId && i.Status == IntakeItemStatus.Scanned
                && (i.Action == IntakeAction.Keep || i.Action == IntakeAction.Replace))
            .SumAsync(i => (long?)i.SizeBytes, ct).ConfigureAwait(false) ?? 0;
        return (needed, FreeBytes(_writer.LibraryRoot));
    }

    internal static long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts failed files back in line: a file whose check failed is checked again (Pending), a file
    /// whose apply step failed keeps its lane and action and is applied again. Returns both counts.
    /// </summary>
    public async Task<(int Recheck, int Reapply)> RetryFailedAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var failed = await db.IntakeItems.Where(i => i.SessionId == sessionId && i.Status == IntakeItemStatus.Failed).ToListAsync(ct).ConfigureAwait(false);
        int recheck = 0, reapply = 0;
        foreach (var i in failed)
        {
            i.Error = null;
            if (i.Lane == IntakeLane.Pending) { i.Status = IntakeItemStatus.Pending; recheck++; }
            else { i.Status = IntakeItemStatus.Scanned; reapply++; }
        }
        if (recheck > 0)
        {
            var s = await db.IntakeSessions.FirstAsync(x => x.Id == sessionId, ct).ConfigureAwait(false);
            s.Status = IntakeSessionStatus.Scanning;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (recheck, reapply);
    }

    /// <summary>The actions that make sense for a file: Replace needs a matched library track.</summary>
    public static IReadOnlyList<IntakeAction> AllowedActions(IntakeItemEntity item)
    {
        var list = new List<IntakeAction> { IntakeAction.Keep, IntakeAction.Skip };
        if (item.MatchKind != IntakeMatchKind.None && !string.IsNullOrEmpty(item.MatchedTrackHash)) list.Add(IntakeAction.Replace);
        if (!string.IsNullOrWhiteSpace(item.Artist) && !string.IsNullOrWhiteSpace(item.Title)) list.Add(IntakeAction.Upgrade);
        return list;
    }

    public async Task<IntakeApplyResult> ApplyAsync(string sessionId, IProgress<IntakeApplyProgress>? progress = null, CancellationToken ct = default)
    {
        IntakeSessionEntity session;
        List<IntakeItemEntity> items;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            session = await db.IntakeSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Intake session {sessionId} doesn't exist.");
            if (await db.IntakeItems.AnyAsync(i => i.SessionId == sessionId && i.Status == IntakeItemStatus.Pending, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Finish the scan before applying: some files haven't been checked yet.");
            session.Status = IntakeSessionStatus.Applying;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            items = await db.IntakeItems.AsNoTracking()
                .Where(i => i.SessionId == sessionId && i.Status == IntakeItemStatus.Scanned)
                .OrderBy(i => i.RelativePath)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        int kept = 0, replaced = 0, skipped = 0, failed = 0, done = 0, upgrades = 0;
        var toImport = new List<IntakeItemEntity>();
        var upgradeItems = new List<IntakeItemEntity>();
        bool needsSource = items.Any(i => i.Action is IntakeAction.Keep or IntakeAction.Replace);
        if (needsSource && !Directory.Exists(session.SourceRoot))
            throw new DirectoryNotFoundException($"{session.Label} isn't connected. Plug it in to apply.");
        long needed = items.Where(i => i.Action is IntakeAction.Keep or IntakeAction.Replace).Sum(i => i.SizeBytes);
        if (FreeBytes(_writer.LibraryRoot) is long free && needed > free)
            throw new IOException($"Not enough space in {_writer.LibraryRoot}: these files need {SLSKDONET.Utils.NumFormat.Size(needed)}, "
                + $"{SLSKDONET.Utils.NumFormat.Size(free)} is free. Free up space or leave some files on the drive.");

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new IntakeApplyProgress(done, items.Count, item.RelativePath));
            try
            {
                switch (item.Action)
                {
                    case IntakeAction.Keep:
                        item.AppliedPath = await CopyIntoLibraryAsync(item, ct).ConfigureAwait(false);
                        toImport.Add(item);
                        if (toImport.Count >= ImportBatchSize)
                            kept += await ImportBatchAsync(toImport, ct).ConfigureAwait(false);
                        break;
                    case IntakeAction.Replace:
                        var staged = await StageForReplaceAsync(item, ct).ConfigureAwait(false);
                        try
                        {
                            item.AppliedPath = await _writer.ReplaceAsync(item, staged, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            TryDelete(staged); // left behind only when the swap refused it
                        }
                        await MarkAsync(item, IntakeItemStatus.Applied, null, ct).ConfigureAwait(false);
                        replaced++;
                        break;
                    case IntakeAction.Upgrade:
                        upgradeItems.Add(item);
                        break;
                    default:
                        await MarkAsync(item, IntakeItemStatus.Skipped, null, ct).ConfigureAwait(false);
                        skipped++;
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intake apply: {Action} failed for {Path}", item.Action, item.SourcePath);
                await MarkAsync(item, IntakeItemStatus.Failed, ex.Message, ct).ConfigureAwait(false);
                failed++;
            }
            done++;
        }

        kept += await ImportBatchAsync(toImport, ct).ConfigureAwait(false);

        if (upgradeItems.Count > 0)
        {
            try
            {
                upgrades = await _writer.QueueUpgradesAsync($"Intake upgrades: {session.Label}", upgradeItems, ct).ConfigureAwait(false);
                foreach (var u in upgradeItems)
                    await MarkAsync(u, IntakeItemStatus.Applied, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Intake apply: queueing {Count} upgrade searches failed", upgradeItems.Count);
                foreach (var u in upgradeItems)
                    await MarkAsync(u, IntakeItemStatus.Failed, "Couldn't queue the search: " + ex.Message, ct).ConfigureAwait(false);
                failed += upgradeItems.Count;
            }
        }

        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var s = await db.IntakeSessions.FirstAsync(x => x.Id == sessionId, ct).ConfigureAwait(false);
            bool open = await db.IntakeItems.AnyAsync(i => i.SessionId == sessionId
                && (i.Status == IntakeItemStatus.Scanned || i.Status == IntakeItemStatus.Failed), ct).ConfigureAwait(false);
            s.Status = open ? IntakeSessionStatus.Ready : IntakeSessionStatus.Done;
            if (!open) s.ClosedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        progress?.Report(new IntakeApplyProgress(items.Count, items.Count, null));
        var result = new IntakeApplyResult(kept, replaced, skipped, upgrades, failed);
        _logger.LogInformation("Intake apply {Id}: {Summary}", sessionId, result.Summary);
        return result;
    }

    /// <summary>
    /// Copies the file to its template path through a temporary name, so a half-copied file never
    /// looks like a finished one. A file already there with the same size is this file from an
    /// interrupted Apply; any other file already there gets a " (2)" name instead of being overwritten.
    /// </summary>
    private async Task<string> CopyIntoLibraryAsync(IntakeItemEntity item, CancellationToken ct)
    {
        var target = _writer.TargetPathFor(item);
        if (File.Exists(target) && new FileInfo(target).Length == item.SizeBytes)
            return target;
        target = FreeName(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await CopyVerifiedAsync(item.SourcePath, target, item.SizeBytes, ct).ConfigureAwait(false);
        return target;
    }

    private async Task<string> StageForReplaceAsync(IntakeItemEntity item, CancellationToken ct)
    {
        var folder = Path.Combine(_writer.LibraryRoot, ".orbit-intake");
        Directory.CreateDirectory(folder);
        var staged = Path.Combine(folder, Guid.NewGuid().ToString("N") + item.Extension);
        await CopyVerifiedAsync(item.SourcePath, staged, item.SizeBytes, ct).ConfigureAwait(false);
        return staged;
    }

    private async Task CopyVerifiedAsync(string source, string target, long expectedSize, CancellationToken ct)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("The file is gone from the source.", source);
        var part = target + PartSuffix;
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
                await input.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
            long copied = new FileInfo(part).Length;
            if (expectedSize > 0 && copied != expectedSize)
                throw new IOException($"The copy is incomplete ({copied} of {expectedSize} bytes).");
            File.Move(part, target);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private async Task<int> ImportBatchAsync(List<IntakeItemEntity> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return 0;
        var snapshot = batch.ToList();
        batch.Clear();
        try
        {
            await _writer.ImportFilesAsync(snapshot.Select(i => i.AppliedPath!).ToList(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The copies stay where they are: a later library scan or a retry adds them.
            _logger.LogWarning(ex, "Intake apply: adding {Count} copied files to the library failed", snapshot.Count);
            foreach (var i in snapshot)
                await MarkAsync(i, IntakeItemStatus.Failed, $"Copied to {i.AppliedPath} but not added to the library: {ex.Message}", ct).ConfigureAwait(false);
            return 0;
        }
        foreach (var i in snapshot)
            await MarkAsync(i, IntakeItemStatus.Applied, null, ct).ConfigureAwait(false);
        return snapshot.Count;
    }

    private async Task MarkAsync(IntakeItemEntity item, IntakeItemStatus status, string? error, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
        var row = await db.IntakeItems.FirstOrDefaultAsync(i => i.Id == item.Id, CancellationToken.None).ConfigureAwait(false);
        if (row == null) return;
        row.Status = status;
        row.Error = error;
        row.AppliedPath = item.AppliedPath;
        row.AppliedAt = status is IntakeItemStatus.Applied or IntakeItemStatus.Skipped ? DateTime.UtcNow : null;
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal static string FreeName(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Intake: couldn't remove the temporary file {Path}", path);
        }
    }
}
