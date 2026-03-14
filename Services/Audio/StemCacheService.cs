using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Audio
{
    /// <summary>
    /// Manages the persistence and retrieval of surgically isolated stem fragments.
    /// </summary>
    public class StemCacheService
    {
        private readonly ILogger<StemCacheService> _logger;
        private readonly string _cacheBaseDir;

        public StemCacheService(ILogger<StemCacheService> logger)
        {
            _logger = logger;
            _cacheBaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "StemCache");
            Directory.CreateDirectory(_cacheBaseDir);
        }

        public string GetCacheKey(string trackHash, float startTime, float duration, string stemType)
        {
            return $"{trackHash}_{startTime:F2}_{duration:F2}_{stemType}".Replace(".", "_");
        }

        public async Task<string?> TryGetCachedStemAsync(string trackHash, float startTime, float duration, string stemType)
        {
            var key = GetCacheKey(trackHash, startTime, duration, stemType);
            var path = Path.Combine(_cacheBaseDir, $"{key}.wav");

            if (File.Exists(path))
            {
                _logger.LogInformation("📁 Stem Cache Hit: {Key}", key);
                return path;
            }

            return null;
        }

        public async Task StoreStemAsync(string trackHash, float startTime, float duration, string stemType, string tempFilePath)
        {
            var key = GetCacheKey(trackHash, startTime, duration, stemType);
            var destPath = Path.Combine(_cacheBaseDir, $"{key}.wav");

            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Copy(tempFilePath, destPath, true);
                    _logger.LogInformation("💾 Stem Cache Stored: {Key}", key);
                    
                    // Periodic Cleanup
                    _ = CleanupCacheAsync(); 
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store stem in cache: {Key}", key);
            }
        }

        private async Task CleanupCacheAsync()
        {
            try
            {
                var dirInfo = new DirectoryInfo(_cacheBaseDir);
                var files = dirInfo.GetFiles("*.wav").OrderBy(f => f.LastAccessTime).ToList();
                
                long totalSize = files.Sum(f => f.Length);
                long maxSizeBytes = 5L * 1024 * 1024 * 1024; // 5GB

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
