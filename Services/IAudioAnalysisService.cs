using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

public interface IAudioAnalysisService
{
    /// <summary>
    /// Analyzes the given audio file using FFmpeg/FFprobe to extract technical metadata.
    /// </summary>
    /// <param name="filePath">Absolute path to the audio file.</param>
    /// <param name="trackUniqueHash">Unique hash of the track to link results to.</param>
    /// <param name="progress">Optional progress sink (percent, step label).</param>
    /// <param name="correlationId">Optional correlation id used in the logs.</param>
    /// <param name="engines">
    /// Which analysis engines to run. Defaults to every engine one pipeline pass can produce; pass
    /// only the missing ones to re-run a track partially (see <see cref="AnalysisEngine"/>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A populated AudioAnalysisEntity or null if analysis fails.</returns>
    Task<AudioAnalysisEntity?> AnalyzeFileAsync(string filePath, string trackUniqueHash, IProgress<(int Percent, string Step)>? progress = null, string? correlationId = null, AnalysisEngine engines = AnalysisEngine.Pipeline, System.Threading.CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves stored analysis data for a track.
    /// </summary>
    Task<AudioAnalysisEntity?> GetAnalysisAsync(string trackUniqueHash);
}
