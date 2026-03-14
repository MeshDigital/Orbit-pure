using System.Threading.Tasks;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services;

public interface IAudioAnalysisService
{
    /// <summary>
    /// Analyzes the given audio file using FFmpeg/FFprobe to extract technical metadata.
    /// </summary>
    /// <param name="filePath">Absolute path to the audio file.</param>
    /// <param name="trackUniqueHash">Unique hash of the track to link results to.</param>
    /// <returns>A populated AudioAnalysisEntity or null if analysis fails.</returns>
    Task<AudioAnalysisEntity?> AnalyzeFileAsync(string filePath, string trackUniqueHash, string? correlationId = null, System.Threading.CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves stored analysis data for a track.
    /// </summary>
    Task<AudioAnalysisEntity?> GetAnalysisAsync(string trackUniqueHash);
}
