using System;
using System.Collections.Concurrent;
using SLSKDONET.Models;
using SLSKDONET.Services.Timeline;

namespace SLSKDONET.Services;

/// <summary>
/// Caches downsampled waveform RMS profiles per track key and target bin count.
/// </summary>
public sealed class WaveformCacheService : IWaveformCacheService
{
    private readonly ConcurrentDictionary<string, float[]> _cache = new(StringComparer.Ordinal);

    public float[] GetOrCreateRmsProfile(string cacheKey, WaveformAnalysisData data, int targetBins)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            // No stable identity: build on demand with no long-term caching.
            return WaveformRenderer.ComputeRmsProfile(data?.RmsData ?? Array.Empty<byte>(), targetBins);
        }

        var key = $"{cacheKey}:{Math.Max(1, targetBins)}";
        return _cache.GetOrAdd(key, _ =>
            WaveformRenderer.ComputeRmsProfile(data?.RmsData ?? Array.Empty<byte>(), targetBins));
    }

    public void Invalidate(string? cacheKey = null)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            _cache.Clear();
            return;
        }

        var prefix = $"{cacheKey}:";
        foreach (var key in _cache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }
}
