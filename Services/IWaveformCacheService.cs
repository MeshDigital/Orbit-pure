using SLSKDONET.Models;

namespace SLSKDONET.Services;

public interface IWaveformCacheService
{
    float[] GetOrCreateRmsProfile(string cacheKey, WaveformAnalysisData data, int targetBins);
    void Invalidate(string? cacheKey = null);
}
