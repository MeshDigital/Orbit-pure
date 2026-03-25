using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

/// <summary>
/// Analyses audio files to determine whether they are genuine lossless recordings
/// or lossy encodes transcoded to a lossless container ("fake FLAC" / "upscaled").
/// </summary>
public interface IAudioIntegrityService
{
    /// <summary>
    /// Performs a full spectral-integrity check on the given audio file.
    /// </summary>
    /// <param name="filePath">Absolute path to the audio file (FLAC, WAV, MP3, AIFF, …).</param>
    /// <param name="cancellationToken">Token to cancel long-running analysis.</param>
    Task<SpectralIntegrityResult> AnalyseAsync(string filePath, CancellationToken cancellationToken = default);
}
