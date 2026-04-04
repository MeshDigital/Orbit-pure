using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Audio
{
    /// <summary>
    /// Manages the persistence and retrieval of surgically isolated stem fragments.
    ///
    /// Cache key format (v2, model-version-aware):
    ///   <c>{modelTag}!{trackHash}_{startTime:F2}_{duration:F2}_{stemType}.wav</c>
    ///
    /// The model tag is derived from the ONNX model filename (without extension) so
    /// switching models automatically invalidates stale cache entries.
    /// Legacy entries (no model tag, no <c>!</c> separator) are treated as stale
    /// and removed by <see cref="PurgeStaleEntriesAsync"/>.
    /// </summary>
    public class StemCacheService
    {
        private readonly ILogger<StemCacheService> _logger;
        private readonly string _cacheBaseDir;

        /// <summary>Separator between model tag and the rest of the cache key.</summary>
        private const char ModelTagSeparator = '!';

        public StemCacheService(ILogger<StemCacheService> logger)
        {
            _logger = logger;
            _cacheBaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "StemCache");
            Directory.CreateDirectory(_cacheBaseDir);
        }

        // ──────────────────────────────────────────────────────────────────────
        // Model version helpers
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Derives a short, human-readable model tag from an ONNX model file path.
        /// Uses the filename without extension (e.g. "spleeter-5stems").
        /// Falls back to "default" if the path is null or empty.
        /// </summary>
        public static string GetModelTag(string? modelPath)
        {
            if (string.IsNullOrEmpty(modelPath)) return "default";
            return Path.GetFileNameWithoutExtension(modelPath);
        }

        // ──────────────────────────────────────────────────────────────────────
        // Cache key
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a versioned cache key.
        /// </summary>
        public string GetCacheKey(string trackHash, float startTime, float duration, string stemType, string modelTag = "")
        {
            var body = $"{trackHash}_{startTime:F2}_{duration:F2}_{stemType}".Replace(".", "_");
            return string.IsNullOrEmpty(modelTag)
                ? body                                          // legacy / unversioned
                : $"{modelTag}{ModelTagSeparator}{body}";      // versioned (v2)
        }

        // ──────────────────────────────────────────────────────────────────────
        // Cache read / write
        // ──────────────────────────────────────────────────────────────────────

        public async Task<string?> TryGetCachedStemAsync(string trackHash, float startTime, float duration, string stemType, string modelTag = "")
        {
            var key = GetCacheKey(trackHash, startTime, duration, stemType, modelTag);
            var path = Path.Combine(_cacheBaseDir, $"{key}.wav");

            if (File.Exists(path))
            {
                _logger.LogInformation("📁 Stem Cache Hit: {Key}", key);
                return path;
            }

            return null;
        }

        public async Task StoreStemAsync(string trackHash, float startTime, float duration, string stemType, string tempFilePath, string modelTag = "")
        {
            var key = GetCacheKey(trackHash, startTime, duration, stemType, modelTag);
            var destPath = Path.Combine(_cacheBaseDir, $"{key}.wav");

            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Copy(tempFilePath, destPath, true);
                    _logger.LogInformation("💾 Stem Cache Stored: {Key}", key);

                    // Periodic size-based LRU cleanup
                    _ = CleanupCacheAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store stem in cache: {Key}", key);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Model-version purge
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Deletes all cached WAV files whose model tag does not match
        /// <paramref name="currentModelTag"/>.  Legacy files (no model tag) are
        /// always treated as stale.
        /// Returns the number of files removed.
        /// </summary>
        public Task<int> PurgeStaleEntriesAsync(string currentModelTag)
        {
            return Task.Run(() =>
            {
                int removed = 0;
                try
                {
                    var expectedPrefix = $"{currentModelTag}{ModelTagSeparator}";
                    var files = Directory.GetFiles(_cacheBaseDir, "*.wav");

                    foreach (var file in files)
                    {
                        var name = Path.GetFileName(file);
                        bool isCurrent = name.StartsWith(expectedPrefix, StringComparison.Ordinal);
                        if (!isCurrent)
                        {
                            File.Delete(file);
                            removed++;
                        }
                    }

                    if (removed > 0)
                        _logger.LogInformation("🧹 Purged {Count} stale stem cache entries (model changed to '{Tag}')", removed, currentModelTag);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Stem cache purge failed: {Msg}", ex.Message);
                }

                return removed;
            });
        }

        // ──────────────────────────────────────────────────────────────────────
        // Size-based LRU cleanup (5 GB cap)
        // ──────────────────────────────────────────────────────────────────────

        private async Task CleanupCacheAsync()
        {
            try
            {
                var dirInfo = new DirectoryInfo(_cacheBaseDir);
                var files = dirInfo.GetFiles("*.wav").OrderBy(f => f.LastAccessTime).ToList();

                long totalSize = files.Sum(f => f.Length);
                long maxSizeBytes = 5L * 1024 * 1024 * 1024; // 5 GB

                if (totalSize > maxSizeBytes)
                {
                    _logger.LogInformation("🧹 Cleaning up stem cache ({Size} MB)...", totalSize / (1024 * 1024));
                    foreach (var file in files)
                    {
                        if (totalSize <= maxSizeBytes) break;
                        totalSize -= file.Length;
                        file.Delete();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Stem cache cleanup failed: {Msg}", ex.Message);
            }
        }
    }
}

