using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace SLSKDONET.Services;

/// <summary>
/// Generates a mosaic / collage artwork bitmap from a playlist's track album-art URLs.
/// When a playlist has no dedicated cover image (e.g. it contains random tracks from
/// many different albums), this service downloads up to four distinct album thumbnails
/// and composites them into a 2×2 grid — similar to what Spotify displays for
/// user-created playlists that have no explicit cover photo.
///
/// Results are cached in memory by a hash of the source URLs so repeated property
/// accesses don't trigger redundant downloads.
/// </summary>
public class PlaylistMosaicService
{
    private readonly ILogger<PlaylistMosaicService> _logger;
    private readonly HttpClient _httpClient;

    // Cache: hash(urls) → WeakReference<Bitmap>
    private readonly ConcurrentDictionary<string, WeakReference<Bitmap>> _cache = new();
    // Dedup in-flight tasks
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _pending = new();

    /// <summary>
    /// The pixel dimension (width and height) of each tile in the mosaic.
    /// The final image will be <see cref="TileSize"/> × 2 on each axis.
    /// </summary>
    public int TileSize { get; } = 150;

    public PlaylistMosaicService(ILogger<PlaylistMosaicService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Generates (or returns a cached) mosaic bitmap for the given set of album-art URLs.
    /// </summary>
    /// <param name="albumArtUrls">
    /// All album-art URLs in the playlist — duplicates are ignored and at most four
    /// distinct, non-empty URLs are used.
    /// </param>
    /// <returns>
    /// A <see cref="Bitmap"/> containing the mosaic, or <c>null</c> if no artwork
    /// could be downloaded.
    /// </returns>
    public Task<Bitmap?> GenerateMosaicAsync(IEnumerable<string?> albumArtUrls)
    {
        // Pick up to 4 distinct, non-empty URLs
        var urls = albumArtUrls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)       // safe: WhiteSpace filter guarantees non-null
            .Distinct()
            .Take(4)
            .ToList();             // List<string> — no null elements

        if (urls.Count == 0)
            return Task.FromResult<Bitmap?>(null);

        var key = ComputeKey(urls);

        // Fast path: already in memory cache
        if (_cache.TryGetValue(key, out var weakRef) && weakRef.TryGetTarget(out var cached))
            return Task.FromResult<Bitmap?>(cached);

        // Dedup concurrent requests for the same key
        return _pending.GetOrAdd(key, async k =>
        {
            try
            {
                var bitmap = await BuildMosaicAsync(urls);
                if (bitmap != null)
                    _cache[k] = new WeakReference<Bitmap>(bitmap);
                return bitmap;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to build playlist mosaic");
                return null;
            }
            finally
            {
                _pending.TryRemove(k, out _);
            }
        });
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<Bitmap?> BuildMosaicAsync(List<string> urls)
    {
        // Download each image in parallel
        var downloadTasks = urls.Select(url => DownloadSkImageAsync(url)).ToList();
        var skImages = await Task.WhenAll(downloadTasks);
        var validImages = skImages.Where(img => img != null).Select(img => img!).ToList();

        if (validImages.Count == 0)
            return null;

        int total = TileSize * 2; // final image is TileSize*2 on each axis

        using var surface = SKSurface.Create(new SKImageInfo(total, total, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        DrawMosaicTiles(canvas, validImages, total);

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);

        // Dispose the downloaded SK images now that we've composited them
        foreach (var img in validImages)
            img.Dispose();

        using var ms = new MemoryStream(data.ToArray());
        return new Bitmap(ms);
    }

    private void DrawMosaicTiles(SKCanvas canvas, List<SKImage> images, int totalSize)
    {
        int half = totalSize / 2;
        int count = Math.Min(images.Count, 4);

        // Define quadrant rects: top-left, top-right, bottom-left, bottom-right
        var rects = new[]
        {
            new SKRect(0,    0,    half, half),
            new SKRect(half, 0,    totalSize, half),
            new SKRect(0,    half, half, totalSize),
            new SKRect(half, half, totalSize, totalSize)
        };

        if (count == 1)
        {
            // Single image — fill the full canvas
            DrawImageFit(canvas, images[0], new SKRect(0, 0, totalSize, totalSize));
            return;
        }

        if (count == 2)
        {
            // Two images — left/right split
            DrawImageFit(canvas, images[0], new SKRect(0, 0, half, totalSize));
            DrawImageFit(canvas, images[1], new SKRect(half, 0, totalSize, totalSize));
            return;
        }

        // 3 or 4 images — 2×2 grid
        for (int i = 0; i < count; i++)
            DrawImageFit(canvas, images[i], rects[i]);
    }

    /// <summary>Draws a SKImage into <paramref name="dest"/>, cropped to center (cover fill).</summary>
    private static void DrawImageFit(SKCanvas canvas, SKImage image, SKRect dest)
    {
        float srcW = image.Width;
        float srcH = image.Height;
        float dstW = dest.Width;
        float dstH = dest.Height;

        // Scale to cover the dest rect
        float scale = Math.Max(dstW / srcW, dstH / srcH);
        float scaledW = srcW * scale;
        float scaledH = srcH * scale;

        // Center-crop source rect
        float srcX = (scaledW - dstW) / 2f / scale;
        float srcY = (scaledH - dstH) / 2f / scale;
        var src = new SKRect(srcX, srcY, srcX + dstW / scale, srcY + dstH / scale);

        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
        canvas.DrawImage(image, src, dest, paint);
    }

    private async Task<SKImage?> DownloadSkImageAsync(string url)
    {
        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            return SKImage.FromEncodedData(ms);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Mosaic: could not download {Url}: {Msg}", url, ex.Message);
            return null;
        }
    }

    private static string ComputeKey(IEnumerable<string> urls)
    {
        var combined = string.Join("|", urls);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash)[..16]; // first 16 hex chars is plenty
    }
}
