namespace SLSKDONET.Services;

/// <summary>
/// Verdict returned by <see cref="IAudioIntegrityService.AnalyseAsync"/>.
/// </summary>
public enum AudioAuthenticityVerdict
{
    /// <summary>Analysis could not be completed (file missing, corrupt, unsupported format, …).</summary>
    Unknown,

    /// <summary>
    /// Strong evidence of genuine lossless audio: spectral content extends to 20 kHz or beyond
    /// with no detectable lossy-encoder cutoff pattern.
    /// </summary>
    GenuineLossless,

    /// <summary>
    /// High-bitrate lossy source (e.g. 256–320 kbps MP3 / AAC) re-encoded into a lossless
    /// container.  Spectral cutoff is between 18–20 kHz.
    /// </summary>
    TranscodedHighBitrate,

    /// <summary>
    /// Medium-bitrate lossy source (e.g. 160–192 kbps) re-encoded into a lossless container.
    /// Spectral cutoff is between 15–18 kHz.
    /// </summary>
    TranscodedMediumBitrate,

    /// <summary>
    /// Low-bitrate lossy source (below 160 kbps) re-encoded into a lossless container.
    /// Spectral cutoff is &lt; 15 kHz.
    /// </summary>
    TranscodedLowBitrate,
}

/// <summary>
/// Detailed result from a spectral integrity analysis.
/// </summary>
public sealed class SpectralIntegrityResult
{
    // ── core verdict ─────────────────────────────────────────────────────────

    /// <summary>High-level authenticity verdict.</summary>
    public AudioAuthenticityVerdict Verdict { get; init; }

    /// <summary>
    /// Confidence in the verdict, expressed as a value between 0 and 1.
    /// Higher is more certain.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>Human-readable explanation of the verdict.</summary>
    public string Reason { get; init; } = string.Empty;

    // ── spectral measurements ─────────────────────────────────────────────────

    /// <summary>
    /// Detected spectral cutoff frequency in Hz.
    /// This is the highest frequency bin that carries significant energy.
    /// A value at or above 20 000 Hz is a strong indicator of genuine lossless audio.
    /// </summary>
    public double SpectralCutoffHz { get; init; }

    /// <summary>
    /// Steepness of the spectral rolloff near the cutoff (dB per kHz).
    /// A very steep slope (≥ 20 dB/kHz) is characteristic of lossy encoding.
    /// </summary>
    public double RolloffSteepnessDpkHz { get; init; }

    /// <summary>Average energy of the 1–15 kHz band in dBFS.</summary>
    public double MidBandEnergyDbfs { get; init; }

    /// <summary>Average energy of the 15–20 kHz band in dBFS.</summary>
    public double HighBandEnergyDbfs { get; init; }

    /// <summary>Average energy above 20 kHz in dBFS (only meaningful for high-SR files).</summary>
    public double UltraHighBandEnergyDbfs { get; init; }

    // ── file metadata ─────────────────────────────────────────────────────────

    /// <summary>Bit depth reported by file metadata (e.g. 16, 24). 0 if unknown.</summary>
    public int FileBitDepth { get; init; }

    /// <summary>Sample rate reported by file metadata in Hz (e.g. 44100, 96000).</summary>
    public int FileSampleRateHz { get; init; }

    /// <summary>Declared bitrate from the file container in kbps. 0 if unknown.</summary>
    public int FileBitrateKbps { get; init; }

    // ── backward-compat shim ──────────────────────────────────────────────────

    /// <summary>
    /// Convenience property: <c>true</c> when the verdict is
    /// <see cref="AudioAuthenticityVerdict.GenuineLossless"/>.
    /// </summary>
    public bool IsGenuineLossless => Verdict == AudioAuthenticityVerdict.GenuineLossless;
}
