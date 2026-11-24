using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;

namespace SLSKDONET.Services.Library.Files;

/// <summary>
/// Sets and reads the LF4 path lock on a library entry (#346). A locked track (PathLockedAt set) is
/// skipped by <see cref="FileRelocationService"/>'s protection guard and shown as locked in the UI.
/// </summary>
public sealed class PathProtectionService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public PathProtectionService(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

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
}
