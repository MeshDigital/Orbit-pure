using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Enrichment;

/// <summary>A cached provider answer.</summary>
public sealed record CachedLookup(int StatusCode, string? Payload, DateTime CreatedAtUtc);

/// <summary>Cache of provider lookups (a sqlite table), so re-runs are cheap.</summary>
public interface IMetadataLookupCache
{
    Task<CachedLookup?> GetAsync(string provider, string key, CancellationToken ct = default);
    Task PutAsync(string provider, string key, int statusCode, string? payload, CancellationToken ct = default);
}

/// <summary>
/// Persistence for the enrichment pipeline, all on the library database:
/// <list type="bullet">
/// <item>the resumable queue (<c>EnrichmentTasks</c>; TrackId = LibraryEntries.UniqueHash);</item>
/// <item>the lookup cache (<c>MetadataLookupCache</c>);</item>
/// <item>per-field provenance (<c>MetadataProvenance</c>).</item>
/// </list>
/// Every method logs and rethrows nothing for the cache (a cache miss is harmless); queue methods
/// let exceptions through to the orchestrator, which logs and surfaces them.
/// </summary>
public sealed class EnrichmentStore : IMetadataLookupCache
{
    /// <summary>Positive answers are reused for this long.</summary>
    public static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(60);
    /// <summary>"Not found" answers expire sooner (catalogues grow).</summary>
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(14);

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger<EnrichmentStore> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public EnrichmentStore(IDbContextFactory<AppDbContext> factory, ILogger<EnrichmentStore> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // ── Queue ────────────────────────────────────────────────────────────────

    /// <summary>Queues tracks that aren't queued yet. Returns how many were added.</summary>
    public async Task<int> EnqueueAsync(IEnumerable<string> hashes, int priority, CancellationToken ct = default)
    {
        var wanted = hashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0) return 0;

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _factory.CreateDbContext();
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in wanted.Chunk(500))
            {
                var ids = chunk.ToList();
                var existing = await db.EnrichmentTasks
                    .Where(t => ids.Contains(t.TrackId) && (t.Status == EnrichmentStatus.Queued || t.Status == EnrichmentStatus.Processing))
                    .Select(t => new { t.TrackId, t.Id, t.Priority })
                    .ToListAsync(ct).ConfigureAwait(false);
                foreach (var e in existing)
                {
                    active.Add(e.TrackId);
                    if (e.Priority < priority)
                    {
                        var row = await db.EnrichmentTasks.FindAsync(new object[] { e.Id }, ct).ConfigureAwait(false);
                        if (row != null) row.Priority = priority;
                    }
                }
            }

            var now = DateTime.UtcNow;
            int added = 0;
            foreach (var hash in wanted)
            {
                if (active.Contains(hash)) continue;
                db.EnrichmentTasks.Add(new EnrichmentTaskEntity
                {
                    Id = Guid.NewGuid(),
                    TrackId = hash,
                    Status = EnrichmentStatus.Queued,
                    Priority = priority,
                    CreatedAt = now,
                });
                added++;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return added;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Takes up to <paramref name="max"/> due tasks (highest priority, oldest first) and marks them
    /// Processing.
    /// </summary>
    public async Task<List<EnrichmentTaskEntity>> ClaimAsync(int max, DateTime nowUtc, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _factory.CreateDbContext();
            var due = await db.EnrichmentTasks
                .Where(t => t.Status == EnrichmentStatus.Queued && (t.NextAttemptAt == null || t.NextAttemptAt <= nowUtc))
                .OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedAt)
                .Take(max)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var t in due)
            {
                t.Status = EnrichmentStatus.Processing;
                t.UpdatedAt = nowUtc;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return due;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task CompleteAsync(Guid taskId, CancellationToken ct = default) =>
        UpdateTaskAsync(taskId, t => { t.Status = EnrichmentStatus.Completed; t.ErrorMessage = null; }, ct);

    public Task FailAsync(Guid taskId, string error, CancellationToken ct = default) =>
        UpdateTaskAsync(taskId, t => { t.Status = EnrichmentStatus.Failed; t.ErrorMessage = Truncate(error); }, ct);

    /// <summary>Back to the queue, not before <paramref name="notBeforeUtc"/>.</summary>
    public Task DeferAsync(Guid taskId, DateTime notBeforeUtc, string reason, CancellationToken ct = default) =>
        UpdateTaskAsync(taskId, t =>
        {
            t.Status = EnrichmentStatus.Queued;
            t.RetryCount++;
            t.NextAttemptAt = notBeforeUtc;
            t.ErrorMessage = Truncate(reason);
        }, ct);

    /// <summary>
    /// After a restart or crash: tasks left in Processing go back to the queue, so the run resumes
    /// where it stopped. Returns how many were reset.
    /// </summary>
    public async Task<int> ResetInterruptedAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _factory.CreateDbContext();
            var stuck = await db.EnrichmentTasks.Where(t => t.Status == EnrichmentStatus.Processing).ToListAsync(ct).ConfigureAwait(false);
            foreach (var t in stuck) t.Status = EnrichmentStatus.Queued;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return stuck.Count;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Drops every queued task (Cancel). Returns how many were removed.</summary>
    public async Task<int> ClearQueuedAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _factory.CreateDbContext();
            var queued = await db.EnrichmentTasks.Where(t => t.Status == EnrichmentStatus.Queued).ToListAsync(ct).ConfigureAwait(false);
            db.EnrichmentTasks.RemoveRange(queued);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return queued.Count;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        await using var db = _factory.CreateDbContext();
        return await db.EnrichmentTasks
            .CountAsync(t => t.Status == EnrichmentStatus.Queued || t.Status == EnrichmentStatus.Processing, ct)
            .ConfigureAwait(false);
    }

    /// <summary>When the next deferred task becomes due, or null when none is waiting.</summary>
    public async Task<DateTime?> NextDeferredAsync(CancellationToken ct = default)
    {
        await using var db = _factory.CreateDbContext();
        return await db.EnrichmentTasks
            .Where(t => t.Status == EnrichmentStatus.Queued && t.NextAttemptAt != null)
            .OrderBy(t => t.NextAttemptAt)
            .Select(t => t.NextAttemptAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private async Task UpdateTaskAsync(Guid id, Action<EnrichmentTaskEntity> change, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _factory.CreateDbContext();
            var task = await db.EnrichmentTasks.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
            if (task == null) return;
            change(task);
            task.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string Truncate(string s) => s.Length > 500 ? s[..500] : s;

    // ── Lookup cache ─────────────────────────────────────────────────────────

    public async Task<CachedLookup?> GetAsync(string provider, string key, CancellationToken ct = default)
    {
        try
        {
            await using var db = _factory.CreateDbContext();
            var row = await db.MetadataLookupCache.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Provider == provider && c.CacheKey == key, ct).ConfigureAwait(false);
            if (row == null) return null;
            var ttl = row.StatusCode == 200 ? PositiveTtl : NegativeTtl;
            return DateTime.UtcNow - row.CreatedAtUtc > ttl ? null : new CachedLookup(row.StatusCode, row.Payload, row.CreatedAtUtc);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Enrichment] Lookup cache read failed for {Provider}/{Key}; asking the provider instead", provider, key);
            return null;
        }
    }

    public async Task PutAsync(string provider, string key, int statusCode, string? payload, CancellationToken ct = default)
    {
        try
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await using var db = _factory.CreateDbContext();
                var row = await db.MetadataLookupCache.FirstOrDefaultAsync(c => c.Provider == provider && c.CacheKey == key, ct).ConfigureAwait(false);
                if (row == null)
                    db.MetadataLookupCache.Add(new MetadataLookupCacheEntity { Provider = provider, CacheKey = key, StatusCode = statusCode, Payload = payload, CreatedAtUtc = DateTime.UtcNow });
                else
                {
                    row.StatusCode = statusCode;
                    row.Payload = payload;
                    row.CreatedAtUtc = DateTime.UtcNow;
                }
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Enrichment] Lookup cache write failed for {Provider}/{Key}", provider, key);
        }
    }

    // ── Provenance ───────────────────────────────────────────────────────────

    public async Task<Dictionary<string, MetadataProvenanceEntity>> GetProvenanceAsync(string hash, CancellationToken ct = default)
    {
        await using var db = _factory.CreateDbContext();
        var rows = await db.MetadataProvenance.AsNoTracking().Where(p => p.TrackUniqueHash == hash).ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Field, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Records that the user edited these fields, so enrichment leaves them alone.</summary>
    public async Task MarkUserEditedAsync(IEnumerable<string> hashes, IEnumerable<string> fields, CancellationToken ct = default)
    {
        var fieldList = fields.ToList();
        var updates = hashes.SelectMany(h => fieldList.Select(f => new MetadataProvenanceEntity
        {
            TrackUniqueHash = h, Field = f, Source = MetadataMerger.UserSource, Confidence = 1.0, UpdatedAtUtc = DateTime.UtcNow,
        })).ToList();
        await UpsertProvenanceAsync(updates, ct).ConfigureAwait(false);
    }

    public async Task UpsertProvenanceAsync(IReadOnlyCollection<MetadataProvenanceEntity> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = _factory.CreateDbContext();
            foreach (var r in rows)
            {
                var existing = await db.MetadataProvenance.FindAsync(new object[] { r.TrackUniqueHash, r.Field }, ct).ConfigureAwait(false);
                if (existing == null) db.MetadataProvenance.Add(r);
                else
                {
                    existing.Source = r.Source;
                    existing.Confidence = r.Confidence;
                    existing.UpdatedAtUtc = r.UpdatedAtUtc;
                }
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
