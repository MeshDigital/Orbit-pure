using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;

namespace SLSKDONET.Services.Library.Maintenance;

public sealed record ContentHashBackfillProgress(int Done, int Total, string? CurrentPath);

public sealed record DuplicateContentGroup(string AudioContentHash, IReadOnlyList<string> TrackHashes);

/// <summary>
/// Backfills <c>LibraryEntryEntity.AudioContentHash</c> (issue #238) one file at a time, and
/// groups entries that share the same tag-independent hash.
/// </summary>
public class LibraryContentHashBackfillService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly AudioContentHasher _hasher;
    private readonly ILogger<LibraryContentHashBackfillService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LibraryContentHashBackfillService(
        IDbContextFactory<AppDbContext> factory,
        AudioContentHasher hasher,
        ILogger<LibraryContentHashBackfillService> logger)
    {
        _factory = factory;
        _hasher = hasher;
        _logger = logger;
    }

    public bool IsRunning => _gate.CurrentCount == 0;

    public async Task<int> BackfillAsync(IProgress<ContentHashBackfillProgress>? progress, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return 0; // already running; the caller can resume later

        try
        {
            List<(string Hash, string Path)> pending;
            await using (var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                pending = await db.LibraryEntries
                    .AsNoTracking()
                    .Where(e => (e.AudioContentHash == null || e.AudioContentHash == "") &&
                                e.FilePath != null && e.FilePath != "")
                    .OrderBy(e => e.AddedAt)
                    .Select(e => new ValueTuple<string, string>(e.UniqueHash, e.FilePath!))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            }

            int done = 0, updated = 0;
            foreach (var item in pending)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ContentHashBackfillProgress(done, pending.Count, item.Path));

                if (!System.IO.File.Exists(item.Path))
                {
                    done++;
                    continue;
                }

                var hash = await _hasher.ComputeAsync(item.Path, ct).ConfigureAwait(false);
                if (hash is null)
                {
                    _logger.LogWarning("Could not hash library file {Path}; leaving AudioContentHash null", item.Path);
                    done++;
                    continue;
                }

                await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
                var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == item.Hash, ct).ConfigureAwait(false);
                if (entry != null)
                {
                    entry.AudioContentHash = hash.Value;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    updated++;
                }

                done++;
                progress?.Report(new ContentHashBackfillProgress(done, pending.Count, item.Path));
            }

            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DuplicateContentGroup>> FindDuplicateGroupsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.LibraryEntries
            .AsNoTracking()
            .Where(e => e.AudioContentHash != null && e.AudioContentHash != "")
            .Select(e => new { e.UniqueHash, Hash = e.AudioContentHash! })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.Hash, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateContentGroup(g.Key, g.Select(r => r.UniqueHash).ToList()))
            .OrderByDescending(g => g.TrackHashes.Count)
            .ThenBy(g => g.AudioContentHash, StringComparer.Ordinal)
            .ToList();
    }
}