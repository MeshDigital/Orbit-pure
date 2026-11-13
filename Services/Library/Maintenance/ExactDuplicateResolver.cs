using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.Library.Maintenance;

/// <summary>Moves a file to the Recycle Bin. Never deletes permanently.</summary>
public interface IFileRecycler
{
    bool IsAvailable { get; }

    /// <summary>Moves <paramref name="path"/> to the Recycle Bin; throws when that isn't possible.</summary>
    void MoveToRecycleBin(string path);
}

/// <summary>Windows Recycle Bin via the shell (Microsoft.VisualBasic.FileIO). Unavailable elsewhere.</summary>
public sealed class ShellFileRecycler : IFileRecycler
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public void MoveToRecycleBin(string path)
    {
        if (!IsAvailable)
            throw new PlatformNotSupportedException("The Recycle Bin is only available on Windows; the file was kept.");

        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
    }
}

public sealed record ExactDuplicateResolution(ExactDuplicateMergeResult Merge, int FilesRecycled, IReadOnlyList<string> RecycleErrors)
{
    public int ErrorCount => Merge.Errors.Count + RecycleErrors.Count;

    /// <summary>"Merged 3 groups, removed 4 entries, 0 errors" (+ the Recycle Bin part when asked for).</summary>
    public string Summary(bool recycleRequested)
    {
        if (Merge.Aborted) return Merge.Summary;
        var text = $"Merged {CountFormat.N(Merge.GroupsMerged)} group{(Merge.GroupsMerged == 1 ? "" : "s")}, " +
                   $"removed {CountFormat.N(Merge.EntriesRemoved)} entr{(Merge.EntriesRemoved == 1 ? "y" : "ies")}, " +
                   $"{CountFormat.N(ErrorCount)} error{(ErrorCount == 1 ? "" : "s")}";
        if (recycleRequested)
            text += $"; moved {CountFormat.N(FilesRecycled)} file{(FilesRecycled == 1 ? "" : "s")} to the Recycle Bin";
        return text;
    }
}

/// <summary>
/// Resolves exact-duplicate groups (issue #238): merges each group into its keeper through
/// <see cref="DuplicateTrackCleanupService.MergeIntoKeepersAsync"/> (one merge path, with its
/// safety backup), then — only when the DJ opted in — moves the removed copies' files to the
/// Recycle Bin. A file is only recycled when it is not the keeper's file and no remaining library
/// entry points at it.
/// </summary>
public sealed class ExactDuplicateResolver
{
    private readonly DuplicateTrackCleanupService _cleanup;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IFileRecycler _recycler;
    private readonly ILogger<ExactDuplicateResolver> _logger;

    public ExactDuplicateResolver(
        DuplicateTrackCleanupService cleanup,
        IDbContextFactory<AppDbContext> dbFactory,
        IFileRecycler recycler,
        ILogger<ExactDuplicateResolver> logger)
    {
        _cleanup = cleanup;
        _dbFactory = dbFactory;
        _recycler = recycler;
        _logger = logger;
    }

    public bool CanRecycle => _recycler.IsAvailable;

    public async Task<ExactDuplicateResolution> ResolveAsync(
        IReadOnlyList<ExactDuplicateMergePlan> plans, bool moveFilesToRecycleBin, CancellationToken ct = default)
    {
        // On the pool: the merge starts with a synchronous database backup, and Task.Yield inside it
        // would otherwise resume on the caller's (UI) context and freeze the window while it copies.
        var merge = await Task.Run(() => _cleanup.MergeIntoKeepersAsync(plans, ct), ct).ConfigureAwait(false);
        if (!moveFilesToRecycleBin || merge.Aborted || merge.RemovedFiles.Count == 0)
            return new ExactDuplicateResolution(merge, 0, Array.Empty<string>());

        var stillReferenced = await StillReferencedPathsAsync(merge.RemovedFiles.Select(f => f.FilePath), ct).ConfigureAwait(false);

        int recycled = 0;
        var errors = new List<string>();
        foreach (var file in merge.RemovedFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(file.FilePath, file.KeeperFilePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (stillReferenced.Contains(file.FilePath)) continue;

            try
            {
                if (!File.Exists(file.FilePath)) continue; // already gone: nothing to recycle
                _recycler.MoveToRecycleBin(file.FilePath);
                recycled++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not move duplicate {Path} to the Recycle Bin", file.FilePath);
                errors.Add($"{Path.GetFileName(file.FilePath)}: {ex.Message}");
            }
        }

        return new ExactDuplicateResolution(merge, recycled, errors);
    }

    private async Task<HashSet<string>> StillReferencedPathsAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var wanted = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return found;

        // Compared in memory: SQLite's lower() only folds ASCII, and a missed match here would recycle a file still in use.
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var all = await db.LibraryEntries.AsNoTracking().Select(e => e.FilePath).ToListAsync(ct).ConfigureAwait(false);
        var wantedSet = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        foreach (var path in all)
            if (!string.IsNullOrEmpty(path) && wantedSet.Contains(path)) found.Add(path);
        return found;
    }
}
