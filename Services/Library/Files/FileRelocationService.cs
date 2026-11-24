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

/// <summary>The result of executing a plan: how many moved and the per-file failures.</summary>
public sealed record RelocationOutcome(int Moved, IReadOnlyList<RelocationProblem> Failures);

/// <summary>
/// The single move/rename path for library tracks (#344, LF2): moves the audio file and updates
/// every path column (LibraryEntries.FilePath, PlaylistTracks.ResolvedFilePath, Tracks.LocalFilePath)
/// in one EF transaction, then publishes a refresh event.
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

    /// <summary>Executes a plan: moves each file, updates the three path columns, and publishes one refresh event.</summary>
    public async Task<RelocationOutcome> ExecuteAsync(RelocationPlan plan, CancellationToken ct = default)
    {
        var failures = new List<RelocationProblem>();
        var moved = 0;

        foreach (var move in plan.Moves)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await MoveFileAsync(move.SourcePath, move.TargetPath, ct).ConfigureAwait(false);
                await UpdatePathsAsync(move.Hash, move.TargetPath, ct).ConfigureAwait(false);
                moved++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Relocation failed for {Hash}: {Source} -> {Target}", move.Hash, move.SourcePath, move.TargetPath);
                failures.Add(new RelocationProblem(move.Hash, ex.Message));
            }
        }

        if (moved > 0)
            _eventBus.Publish(new LibraryUpdatedEvent("library files relocated"));

        return new RelocationOutcome(moved, failures);
    }

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
}
