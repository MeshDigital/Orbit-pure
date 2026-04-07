using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services.Audio.Separation;

/// <summary>
/// Decorator that wraps any <see cref="IStemSeparator"/> with cache-through behaviour.
/// On a cache hit (all 4 stem WAV files present for the current model version) the
/// ONNX inference is skipped entirely; on a miss the inner separator is called and
/// the output files are registered in <see cref="StemCacheService"/>.
///
/// Additionally exposes <see cref="SeparateWithProgressAsync"/> which reports
/// per-stem completion progress (0 → 1) via <see cref="IProgress{T}"/>.
/// </summary>
public sealed class CachedStemSeparator : IStemSeparator
{
    private readonly IStemSeparator    _inner;
    private readonly StemCacheService  _cache;
    private readonly ILogger<CachedStemSeparator> _logger;

    public string Name     => $"{_inner.Name} (cached)";
    public bool IsAvailable => _inner.IsAvailable;
    public string ModelTag  => _inner.ModelTag;

    public CachedStemSeparator(
        IStemSeparator inner,
        StemCacheService cache,
        ILogger<CachedStemSeparator> logger)
    {
        _inner  = inner;
        _cache  = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Dictionary<StemType, string>> SeparateAsync(
        string inputFilePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
        => SeparateWithProgressAsync(inputFilePath, outputDirectory, null, cancellationToken);

    /// <summary>
    /// Separates stems with optional progress reporting.
    /// <paramref name="progress"/> is called with values 0.0–1.0 as each stem completes.
    /// </summary>
    public async Task<Dictionary<StemType, string>> SeparateWithProgressAsync(
        string inputFilePath,
        string outputDirectory,
        IProgress<float>? progress,
        CancellationToken cancellationToken = default)
    {
        string trackHash  = ComputeFileHash(inputFilePath);
        float  duration   = GetAudioDuration(inputFilePath);
        string modelTag   = _inner.ModelTag;

        // ── 1. Cache hit check ────────────────────────────────────────────
        var cached = await TryGetAllStemsFromCacheAsync(trackHash, duration, modelTag, cancellationToken);
        if (cached != null)
        {
            _logger.LogInformation("Stem cache hit for {Hash} (model: {Tag})", trackHash[..8], modelTag);
            progress?.Report(1.0f);
            return cached;
        }

        // ── 2. Run separator ──────────────────────────────────────────────
        _logger.LogInformation("Running stem separation on {File}", Path.GetFileName(inputFilePath));
        var stems = await _inner.SeparateAsync(inputFilePath, outputDirectory, cancellationToken);

        // ── 3. Store each stem in cache and report progress ───────────────
        int total = stems.Count;
        int done  = 0;

        foreach (var (stemType, stemPath) in stems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string stemName = stemType.ToString().ToLowerInvariant();

            await _cache.StoreStemAsync(trackHash, 0f, duration, stemName, stemPath, modelTag);

            done++;
            progress?.Report((float)done / total);
            _logger.LogDebug("Cached stem {Stem} for {Hash}", stemName, trackHash[..8]);
        }

        return stems;
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns a populated dictionary if ALL 4 stems are cache-present; null otherwise.
    /// </summary>
    private async Task<Dictionary<StemType, string>?> TryGetAllStemsFromCacheAsync(
        string trackHash,
        float duration,
        string modelTag,
        CancellationToken ct)
    {
        var stemTypes = new[] { StemType.Vocals, StemType.Drums, StemType.Bass, StemType.Other };
        var result    = new Dictionary<StemType, string>();

        foreach (var st in stemTypes)
        {
            ct.ThrowIfCancellationRequested();
            string stemName = st.ToString().ToLowerInvariant();
            string? path    = await _cache.TryGetCachedStemAsync(trackHash, 0f, duration, stemName, modelTag);
            if (path == null) return null;   // miss → skip remaining
            result[st] = path;
        }

        return result;
    }

    /// <summary>SHA-256 of the first 64 KB + file size for a fast, stable hash.</summary>
    private static string ComputeFileHash(string filePath)
    {
        const int headerBytes = 65536;
        using var sha  = SHA256.Create();
        using var file = File.OpenRead(filePath);

        long size  = file.Length;
        var  buf   = new byte[Math.Min(headerBytes, size)];
        int  read  = file.Read(buf, 0, buf.Length);

        // Mix in file size so different-length files with identical headers differ
        var sizeBytes = BitConverter.GetBytes(size);
        sha.TransformBlock(buf, 0, read, null, 0);
        sha.TransformFinalBlock(sizeBytes, 0, sizeBytes.Length);

        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>Returns the duration of an audio file in seconds, or 0 on failure.</summary>
    private static float GetAudioDuration(string filePath)
    {
        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(filePath);
            return (float)reader.TotalTime.TotalSeconds;
        }
        catch
        {
            return 0f;
        }
    }
}
