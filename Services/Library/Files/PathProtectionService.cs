using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;

namespace SLSKDONET.Services.Library.Files;

/// <summary>Snapshot of a track's LF4 path protection state for UI display.</summary>
public readonly record struct PathProtectionState(bool IsLocked, DateTime? PathLockedAt, string? FileOrigin);

/// <summary>
/// Sets and reads the LF4 path lock on a library entry (#346). A locked track (PathLockedAt set) is
/// skipped by <see cref="FileRelocationService"/>'s protection guard and shown as locked in the UI.
/// </summary>
public sealed class PathProtectionService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public PathProtectionService(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    /// <summary>Reads lock status and FileOrigin in a single query. Returns unlocked/null for an unknown hash.</summary>
    public async Task<PathProtectionState> GetProtectionStateAsync(string hash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return default;

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.LibraryEntries
            .AsNoTracking()
            .Where(e => e.UniqueHash == hash)
            .Select(e => new { e.PathLockedAt, e.FileOrigin })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return row is null
            ? default
            : new PathProtectionState(row.PathLockedAt != null, row.PathLockedAt, row.FileOrigin);
    }

    /// <summary>Locks (sets PathLockedAt) or unlocks (clears it) a library entry. No-op for an unknown hash.</summary>
    public async Task SetPathLockedAsync(string hash, bool locked, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == hash, ct).ConfigureAwait(false);
        if (entry is null) return;
        entry.PathLockedAt = locked ? DateTime.UtcNow : null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>True when the entry exists and its path is locked.</summary>
    public async Task<bool> IsPathLockedAsync(string hash, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.LibraryEntries
            .AnyAsync(e => e.UniqueHash == hash && e.PathLockedAt != null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-derives and stores the file's origin (Downloaded / LinkedFolder / Imported) from its path,
    /// the enabled library-folder roots and the download directory. Returns the stored origin, or
    /// null for an unknown hash.
    /// </summary>
    public async Task<string?> RefreshFileOriginAsync(string hash, string filePath, string? downloadDirectory, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == hash, ct).ConfigureAwait(false);
        if (entry is null) return null;

        var roots = await db.LibraryFolders
            .Where(f => f.IsEnabled)
            .Select(f => f.FolderPath)
            .ToListAsync(ct).ConfigureAwait(false);

        var origin = FileOriginDeriver.Derive(filePath, roots, downloadDirectory);
        entry.FileOrigin = origin;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return origin;
    }
}
