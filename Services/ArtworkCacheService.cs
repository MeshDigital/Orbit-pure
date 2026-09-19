using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SLSKDONET.Utils;

namespace SLSKDONET.Services
{
    /// <summary>
    /// Provides a shared cache for artwork bitmaps to prevent memory bloat.
    /// Uses WeakReferences to ensure bitmaps are eligible for collection when no longer referenced by active ViewModels.
    ///
    /// Remote (Spotify/MusicBrainz) artwork is additionally persisted to a disk cache keyed by a hash
    /// of the URL, in %AppData%/ORBIT/artwork/ — previously there was no disk persistence at all, so
    /// every app restart (and every time a WeakReference-cached bitmap was GC'd mid-session) silently
    /// re-downloaded every album's art from the network. For a large playlist that meant hundreds of
    /// network round-trips just to redraw rows that had already been shown before.
    /// </summary>
    public class ArtworkCacheService
    {
        private readonly ILogger<ArtworkCacheService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _diskCacheDir;

        /// <summary>
        /// Default decode width for artwork requested without an explicit size. Every current
        /// on-screen use of this service (32px list rows, 100px inspector panel, 200px grid
        /// cards, small playlist-card covers) tops out at ~200 logical px — 512px comfortably
        /// covers that at up to ~2.5x DPI scaling. Embedded/remote cover art is frequently much
        /// larger (600-3000px square), so decoding at native resolution for a 32px row was
        /// spending 10-100x the pixels (and proportionally more decode time + retained memory)
        /// than anything on screen ever uses.
        /// </summary>
        public const int DefaultDecodeWidth = 512;

        // Use WeakReferences so that if no ViewModel holds the Bitmap, it can be collected.
        // The Key is "url-or-path|decodeWidth" — full-res and thumbnail requests for the same
        // artwork are cached separately since they're different decoded Bitmap instances.
        private readonly ConcurrentDictionary<string, WeakReference<Bitmap>> _cache = new();

        // Loading tasks to prevent duplicate network calls for the same URL
        private readonly ConcurrentDictionary<string, Task<Bitmap?>> _loadingTasks = new();

        /// <summary>
        /// Bounded set of STRONG references riding on top of <see cref="_cache"/>'s WeakReferences.
        /// A pure WeakReference cache reclaims a bitmap the instant no ViewModel currently
        /// references it — for a virtualized list, that's every row the moment it scrolls off
        /// screen, since VirtualizedTrackCollection evicts its own PlaylistTrackViewModel instances
        /// for pages that scroll far enough away (see its own LRU page eviction). Scrolling back
        /// even one row then forced a full disk-cache-hit-plus-decode again for art that was just
        /// visible. Keeping the most recently touched 100 decoded bitmaps strongly referenced here
        /// absorbs that churn within a fixed ~100MB worst-case budget (100 entries × up to
        /// DefaultDecodeWidth² BGRA pixels) — bounded, unlike letting every distinct album ever
        /// scrolled past accumulate as a live GC root.
        /// </summary>
        private readonly BoundedLruCache<string, Bitmap> _hotCache = new(capacity: 100);

        public ArtworkCacheService(ILogger<ArtworkCacheService> logger, HttpClient httpClient, string? diskCacheDirOverride = null)
        {
            _logger = logger;
            _httpClient = httpClient;

            if (diskCacheDirOverride != null)
            {
                _diskCacheDir = diskCacheDirOverride;
            }
            else
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                _diskCacheDir = Path.Combine(appData, "ORBIT", "artwork");
            }
            try
            {
                Directory.CreateDirectory(_diskCacheDir);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to create artwork disk cache directory — falling back to network-only for this session");
            }
        }

        /// <summary>
        /// Retrieves a shared Bitmap instance for the given URI or File Path.
        /// If the bitmap is already in memory, returns the existing instance.
        /// </summary>
        /// <param name="uriOrPath">Remote URL or local file path.</param>
        /// <param name="decodeWidth">
        /// Target decode width in pixels (aspect ratio preserved). Defaults to
        /// <see cref="DefaultDecodeWidth"/>, which covers every current on-screen size. Pass a
        /// larger value (or a very large one, e.g. int.MaxValue, to mean "native resolution") only
        /// for a genuinely full-size display context.
        /// </param>
        public async Task<Bitmap?> GetBitmapAsync(string? uriOrPath, int decodeWidth = DefaultDecodeWidth)
        {
            if (string.IsNullOrWhiteSpace(uriOrPath)) return null;

            var cacheKey = $"{uriOrPath}|{decodeWidth}";

            // 1. Check Cache
            if (_cache.TryGetValue(cacheKey, out var weakRef))
            {
                if (weakRef.TryGetTarget(out var bitmap))
                {
                    // Touch the hot cache on every hit, not just on first load, so its LRU order
                    // reflects actual recency of use rather than pure load order — a row scrolled
                    // past repeatedly should outlast one touched once and never revisited.
                    _hotCache.Set(cacheKey, bitmap);
                    return bitmap;
                }
                else
                {
                    // Reference is dead, remove it (optional, safe to overwrite later)
                    _cache.TryRemove(cacheKey, out _);
                }
            }

            // 2. Load (with dedup via loadingTasks)
            return await _loadingTasks.GetOrAdd(cacheKey, async (k) =>
            {
                try
                {
                    var loaded = await LoadBitmapInternalAsync(uriOrPath, decodeWidth).ConfigureAwait(false);
                    if (loaded != null)
                    {
                        // Add to Cache
                        _cache.AddOrUpdate(k,
                            new WeakReference<Bitmap>(loaded),
                            (key, oldVal) => new WeakReference<Bitmap>(loaded));
                        _hotCache.Set(k, loaded);

                        // Periodic cleanup (Probabilistic 1/1000 hits)
                        // Prevents dictionary content leak (Dead WeakRefs + Strings)
                        if (new Random().Next(0, 1000) == 0)
                        {
                             _ = Task.Run(() =>
                             {
                                 try { PurgeDeadReferences(); }
                                 catch (Exception ex) { _logger.LogDebug(ex, "Artwork cache periodic cleanup failed"); }
                             });
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

        /// <summary>
        /// Synchronous, non-loading peek at whatever's already resident for this key — no Task,
        /// no Dispatcher hop. <see cref="Models.ArtworkProxy.Image"/> uses this first so that a
        /// row re-bound to artwork this service already has decoded (e.g. a virtualized row
        /// recreated on scroll-back, or two tracks sharing the same album) paints it immediately
        /// on the very first property-getter call, instead of always returning null for one frame
        /// and asynchronously popping in the image a moment later regardless of whether the
        /// decode work was actually needed again.
        /// </summary>
        public bool TryGetCachedBitmap(string? uriOrPath, out Bitmap? bitmap, int decodeWidth = DefaultDecodeWidth)
        {
            bitmap = null;
            if (string.IsNullOrWhiteSpace(uriOrPath)) return false;

            var cacheKey = $"{uriOrPath}|{decodeWidth}";
            if (_cache.TryGetValue(cacheKey, out var weakRef) && weakRef.TryGetTarget(out var cached))
            {
                _hotCache.Set(cacheKey, cached);
                bitmap = cached;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves and decodes artwork — decode always runs off the calling thread via
        /// <see cref="Task.Run(Func{Bitmap})"/>. Previously the local-file branch decoded
        /// synchronously with no preceding await, which meant it ran inline on whatever thread
        /// first touched <see cref="Models.ArtworkProxy.Image"/> — the UI thread, since that getter
        /// fires directly from data binding. A full-resolution JPEG/PNG decode on the UI thread per
        /// unique album is a directly visible scroll hitch.
        /// </summary>
        private async Task<Bitmap?> LoadBitmapInternalAsync(string uriOrPath, int decodeWidth)
        {
            try
            {
                var bytes = await ResolveBytesAsync(uriOrPath).ConfigureAwait(false);
                if (bytes == null)
                    return null;

                return await Task.Run(() =>
                {
                    using var stream = new MemoryStream(bytes);
                    // Decoding directly to the target width (rather than full native resolution
                    // then letting the Image control scale it down) is what actually saves the
                    // memory/CPU — Skia never materializes the full-size pixel buffer at all.
                    return decodeWidth < int.MaxValue
                        ? Bitmap.DecodeToWidth(stream, decodeWidth)
                        : new Bitmap(stream);
                }).ConfigureAwait(false);
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

        /// <summary>
        /// Resolves raw image bytes for a URL or local path: disk cache hit → network download (then
        /// persisted to disk) → local file read. Kept separate from decoding (which needs a live
        /// Avalonia/Skia platform and so isn't exercisable from a plain unit test) so the actual fix
        /// here — never re-hitting the network once a URL is cached to disk — has direct test coverage.
        /// </summary>
        internal async Task<byte[]?> ResolveBytesAsync(string uriOrPath)
        {
            if (uriOrPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var diskCachePath = GetDiskCachePath(uriOrPath);

                if (diskCachePath != null && File.Exists(diskCachePath))
                {
                    try
                    {
                        return await File.ReadAllBytesAsync(diskCachePath).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Corrupt/partial cache file — fall through and re-download.
                        _logger.LogDebug(ex, "Disk-cached artwork unreadable, re-downloading: {Path}", diskCachePath);
                    }
                }

                var data = await _httpClient.GetByteArrayAsync(uriOrPath).ConfigureAwait(false);

                if (diskCachePath != null)
                {
                    try
                    {
                        await File.WriteAllBytesAsync(diskCachePath, data).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to persist artwork to disk cache: {Path}", diskCachePath);
                    }
                }

                return data;
            }
            else if (File.Exists(uriOrPath))
            {
                return await File.ReadAllBytesAsync(uriOrPath).ConfigureAwait(false);
            }

            return null;
        }

        /// <summary>Stable, collision-safe disk cache filename for a remote artwork URL. Returns null if the disk cache directory couldn't be created.</summary>
        private string? GetDiskCachePath(string url)
        {
            if (!Directory.Exists(_diskCacheDir))
                return null;

            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
            var hash = Convert.ToHexString(hashBytes);
            return Path.Combine(_diskCacheDir, $"{hash}.cache");
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
