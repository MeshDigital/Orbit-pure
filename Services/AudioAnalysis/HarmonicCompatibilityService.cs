using SLSKDONET.Models;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Reusable pairwise harmonic scorer for A10.x similarity and optimizer layers.
/// </summary>
public sealed class HarmonicCompatibilityService
{
    private readonly HarmonicAnalysisService _harmonicAnalysisService;

    public HarmonicCompatibilityService(HarmonicAnalysisService harmonicAnalysisService)
    {
        _harmonicAnalysisService = harmonicAnalysisService;
    }

    public float Score(TrackFingerprint a, TrackFingerprint b)
    {
        if (a.Harmonic is null || b.Harmonic is null)
            return 0f;

        return _harmonicAnalysisService.ComputeCompatibility(a.Harmonic, b.Harmonic);
    }
}