using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services;

/// <summary>
/// Two-tier (in-memory  + disk JSON) cache for <see cref="AudioFeaturesEntity"/> records.
///
/// Cache layout (disk):
///   %APPDATA%\ORBIT\AnalysisCache\{trackHash[0..1]}\{trackHash}.json
///
/// In-memory eviction uses a simple LRU-like TTL: entries older than
/// <see cref="MemoryTtl"/> are considered stale and re-read from disk.
/// Implements Issue 7.2 / #42 (analysis results caching).
/// </summary>
public sealed class AnalysisResultDiskCache : IDisposable
{
    // ── Configuration ────────────────────────────────────────────────────

    /// <summary>Time-to-live for in-memory entries.</summary>
    public static readonly TimeSpan MemoryTtl = TimeSpan.FromMinutes(10);

    private readonly string _cacheRoot;
    private readonly ILogger<AnalysisResultDiskCache> _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    // in-memory hot cache
    private readonly ConcurrentDictionary<string, (AudioFeaturesEntity Entity, DateTime Ts)>
        _memCache = new(StringComparer.Ordinal);

    public AnalysisResultDiskCache(ILogger<AnalysisResultDiskCache> logger, string? cacheRoot = null)
    {
        _logger    = logger;
        _cacheRoot = cacheRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "ORBIT", "AnalysisCache");
        Directory.CreateDirectory(_cacheRoot);
    }

    // ── Read ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a cached <see cref="AudioFeaturesEntity"/> for the given hash,
    /// or <c>null</c> if not cached.
    /// </summary>
    public async Task<AudioFeaturesEntity?> GetAsync(string trackHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackHash)) return null;

        // 1. Hot-cache hit
        if (_memCache.TryGetValue(trackHash, out var entry) &&
            DateTime.UtcNow - entry.Ts < MemoryTtl)
        {
            return entry.Entity;
        }

        // 2. Disk hit
        string path = GetDiskPath(trackHash);
        if (!File.Exists(path)) return null;

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var fs    = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var entity      = await JsonSerializer.DeserializeAsync<AudioFeaturesEntity>(fs, cancellationToken: ct)
                              .ConfigureAwait(false);
            if (entity is not null)
                _memCache[trackHash] = (entity, DateTime.UtcNow);
            return entity;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnalysisCache: failed to deserialise {Path}", path);
            return null;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores an <see cref="AudioFeaturesEntity"/> in both the in-memory and disk caches.
    /// </summary>
    public async Task SetAsync(AudioFeaturesEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (string.IsNullOrWhiteSpace(entity.TrackUniqueHash)) return;

        string hash = entity.TrackUniqueHash;
        _memCache[hash] = (entity, DateTime.UtcNow);

        string path = GetDiskPath(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(fs, entity, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AnalysisCache: failed to write {Path}", path);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    // ── Invalidation ──────────────────────────────────────────────────────

    /// <summary>Removes a single entry from memory and disk.</summary>
    public void Invalidate(string trackHash)
    {
        if (string.IsNullOrWhiteSpace(trackHash)) return;
        _memCache.TryRemove(trackHash, out _);
        string path = GetDiskPath(trackHash);
        if (File.Exists(path))
        {
            try   { File.Delete(path); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Clears all in-memory entries (disk cache remains).</summary>
    public void ClearMemory() => _memCache.Clear();

    // ── Path helpers ──────────────────────────────────────────────────────

    private string GetDiskPath(string hash)
    {
        // Two-level sharding: first 2 chars of hash → sub-directory
        string shard = hash.Length >= 2 ? hash[..2].ToLowerInvariant() : "00";
        return Path.Combine(_cacheRoot, shard, $"{hash}.json");
    }

    public void Dispose() => _ioLock.Dispose();
}
