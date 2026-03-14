using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services
{
    /// <summary>
    /// Provides a shared cache for artwork bitmaps to prevent memory bloat.
    /// Uses WeakReferences to ensure bitmaps are eligible for collection when no longer referenced by active ViewModels.
    /// </summary>
    public class ArtworkCacheService
    {
        private readonly ILogger<ArtworkCacheService> _logger;
        private readonly HttpClient _httpClient;
        
        // Use WeakReferences so that if no ViewModel holds the Bitmap, it can be collected.
        // The Key is the URL/Path.
        private readonly ConcurrentDictionary<string, WeakReference<Bitmap>> _cache = new();
        
        // Loading tasks to prevent duplicate network calls for the same URL
        private readonly ConcurrentDictionary<string, Task<Bitmap?>> _loadingTasks = new();

        public ArtworkCacheService(ILogger<ArtworkCacheService> logger, HttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
        }

        /// <summary>
        /// Retrieves a shared Bitmap instance for the given URI or File Path.
        /// If the bitmap is already in memory, returns the existing instance.
        /// </summary>
        public async Task<Bitmap?> GetBitmapAsync(string? uriOrPath)
        {
            if (string.IsNullOrWhiteSpace(uriOrPath)) return null;

            // 1. Check Cache
            if (_cache.TryGetValue(uriOrPath, out var weakRef))
            {
                if (weakRef.TryGetTarget(out var bitmap))
                {
                    return bitmap;
                }
                else
                {
                    // Reference is dead, remove it (optional, safe to overwrite later)
                    _cache.TryRemove(uriOrPath, out _);
                }
            }

            // 2. Load (with dedup via loadingTasks)
            return await _loadingTasks.GetOrAdd(uriOrPath, async (k) =>
            {
                try
                {
                    var loaded = await LoadBitmapInternalAsync(k);
                    if (loaded != null)
                    {
                        // Add to Cache
                        _cache.AddOrUpdate(k, 
                            new WeakReference<Bitmap>(loaded), 
                            (key, oldVal) => new WeakReference<Bitmap>(loaded));
                            
                        // Periodic cleanup (Probabilistic 1/1000 hits)
                        // Prevents dictionary content leak (Dead WeakRefs + Strings)
                        if (new Random().Next(0, 1000) == 0)
                        {
                             _ = Task.Run(PurgeDeadReferences);
                        }
                    }
                    return loaded;
                }
                finally
                {
                    _loadingTasks.TryRemove(k, out _);
                }
            });
        }

        private async Task<Bitmap?> LoadBitmapInternalAsync(string uriOrPath)
        {
            try
            {
                if (uriOrPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    // Download
                    var data = await _httpClient.GetByteArrayAsync(uriOrPath);
                    using var stream = new MemoryStream(data);
                    return new Bitmap(stream);
                }
                else if (File.Exists(uriOrPath))
                {
                    // Local File
                    return new Bitmap(uriOrPath);
                }
            }
            catch (Exception ex)
            {
                // Throttle failure logging to prevent spam during network issues
                if (DateTime.Now.Second % 10 == 0)
                {
                    _logger.LogWarning("Failed to load artwork (throttled): {Path}. Error: {Message}", uriOrPath, ex.Message);
                }
            }
            return null;
        }
        
        private void PurgeDeadReferences()
        {
            foreach (var kvp in _cache)
            {
                if (!kvp.Value.TryGetTarget(out _))
                {
                    _cache.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}
