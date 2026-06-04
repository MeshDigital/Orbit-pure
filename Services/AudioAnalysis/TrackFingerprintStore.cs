using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Versioned JSON store for A10 fingerprints.
/// Stored separately from existing analysis artifacts to avoid schema churn during A10.x.
///
/// A10.6 hardening:
///   • In-memory cache (_memoryCache) — cache hits bypass disk entirely.
///   • Write lock only — concurrent disk reads are safe (FileShare.Read); reads no longer
///     block each other or writes.
///   • Invalidate(hash) / InvalidateAll() — called when a fingerprint is re-analysed so
///     stale cache entries are evicted before any consumer re-scores them.
/// </summary>
public sealed class TrackFingerprintStore : IDisposable
{
    private readonly ILogger<TrackFingerprintStore> _logger;
    private readonly string _root;

    // Write-only lock — concurrent reads are safe because FileShare.Read is set.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Session-scoped in-memory cache. Unbounded by design: fingerprints are ~2 KB each;
    // a 10 K-track library consumes ~20 MB — acceptable for a desktop DAW process.
    private readonly ConcurrentDictionary<string, TrackFingerprint> _memoryCache = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public TrackFingerprintStore(ILogger<TrackFingerprintStore> logger, string? root = null)
    {
        _logger = logger;
        _root = root
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "Fingerprints");
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(TrackFingerprint fingerprint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (string.IsNullOrWhiteSpace(fingerprint.TrackUniqueHash))
            return;

        fingerprint.SchemaVersion = TrackFingerprint.CurrentSchemaVersion;
        var document = new TrackFingerprintDocument
        {
            SchemaVersion = TrackFingerprint.CurrentSchemaVersion,
            GeneratedAtUtc = fingerprint.GeneratedAtUtc,
            Fingerprint = fingerprint,
        };

        var path = GetStoragePath(fingerprint.TrackUniqueHash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(fs, document, JsonOptions, ct).ConfigureAwait(false);
            // Update cache after successful write so the new fingerprint is immediately visible.
            _memoryCache[fingerprint.TrackUniqueHash] = fingerprint;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TrackFingerprintStore: failed to persist fingerprint for {TrackHash}", fingerprint.TrackUniqueHash);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<TrackFingerprint?> GetAsync(string trackHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackHash))
            return null;

        // Fast path: return cached fingerprint without any I/O or locking.
        if (_memoryCache.TryGetValue(trackHash, out var cached))
            return cached;

        var path = GetStoragePath(trackHash);
        if (!File.Exists(path))
            return null;

        // Disk reads are lock-free: FileShare.Read allows concurrent readers, and the
        // write lock in SaveAsync ensures no partial writes are visible.
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("schema_version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out var schemaVersion))
            {
                _logger.LogWarning("TrackFingerprintStore: missing schema_version in {Path}", path);
                return null;
            }

            if (schemaVersion is not 1 and not TrackFingerprint.CurrentSchemaVersion)
            {
                _logger.LogWarning(
                    "TrackFingerprintStore: unsupported schema {SchemaVersion} for {TrackHash} (current {Current})",
                    schemaVersion,
                    trackHash,
                    TrackFingerprint.CurrentSchemaVersion);
                return null;
            }

            fs.Position = 0;
            var document = await JsonSerializer.DeserializeAsync<TrackFingerprintDocument>(fs, JsonOptions, ct).ConfigureAwait(false);
            if (document?.Fingerprint is null)
                return null;

            document.Fingerprint.SchemaVersion = schemaVersion;
            // Populate cache so subsequent reads for this hash are served from memory.
            _memoryCache[trackHash] = document.Fingerprint;
            return document.Fingerprint;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TrackFingerprintStore: failed to load fingerprint for {TrackHash}", trackHash);
            return null;
        }
    }

    /// <summary>
    /// Evicts <paramref name="trackHash"/> from the in-memory cache. Call this when a
    /// fingerprint is re-analysed so any cached similarity results using the old fingerprint
    /// are discarded before the next scoring pass.
    /// </summary>
    public void Invalidate(string trackHash)
        => _memoryCache.TryRemove(trackHash, out _);

    /// <summary>
    /// Evicts all entries from the in-memory cache. Useful after a bulk re-analysis pass.
    /// </summary>
    public void InvalidateAll()
        => _memoryCache.Clear();

    public string GetStoragePath(string trackHash)
    {
        var shard = trackHash.Length >= 2 ? trackHash[..2].ToLowerInvariant() : "00";
        return Path.Combine(_root, shard, $"{trackHash}.fingerprint.json");
    }

    public void Dispose() => _writeLock.Dispose();

    private sealed class TrackFingerprintDocument
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("generated_at_utc")]
        public DateTime GeneratedAtUtc { get; set; }

        [JsonPropertyName("fingerprint")]
        public TrackFingerprint Fingerprint { get; set; } = new();
    }
}