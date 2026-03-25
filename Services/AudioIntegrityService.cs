using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NWaves.Transforms;
using NWaves.Windows;
using TagLib;

namespace SLSKDONET.Services;

/// <summary>
/// Professional spectral-integrity service that detects whether an audio file is a genuine
/// lossless recording or a lossy encode (MP3, AAC, Ogg Vorbis, …) re-packaged into a
/// lossless container ("fake FLAC" / "upscaled").
///
/// Algorithm overview
/// ──────────────────
/// 1. Decode the first mono channel to raw 32-bit float PCM via NAudio AudioFileReader.
/// 2. Read container metadata (bit depth, sample rate, declared bitrate) via TagLibSharp.
/// 3. Extract the middle 30 s of the track to avoid silence/fade artefacts.
/// 4. Apply a Hann window to overlapping 4096-sample frames and compute each frame's
///    real-valued FFT power spectrum using NWaves RealFft.
/// 5. Temporally average the per-frame power spectra (in linear scale).
/// 6. Scan the averaged spectrum from Nyquist downward to locate the spectral cutoff:
///    the highest frequency bin whose magnitude exceeds a noise-floor threshold.
/// 7. Measure the rolloff steepness (dB/kHz) in the 2 kHz window just below the cutoff.
/// 8. Derive a confidence-weighted verdict:
///      ≥ 20 kHz cutoff + shallow slope → GenuineLossless
///      18–20 kHz                        → TranscodedHighBitrate
///      15–18 kHz                        → TranscodedMediumBitrate
///      &lt; 15 kHz                         → TranscodedLowBitrate
/// </summary>
public sealed class AudioIntegrityService : IAudioIntegrityService
{
    // ── tuneable analysis parameters ──────────────────────────────────────────

    /// <summary>Number of PCM samples per FFT frame.  Must be a power of two.</summary>
    private const int FftSize = 4096;

    /// <summary>Frame hop expressed as a fraction of FftSize (50 % overlap).</summary>
    private const int HopDivisor = 2;

    /// <summary>Target mono sample rate for analysis.  Files are decoded at their native SR.</summary>
    private const int AnalysisSampleRate = 44100;

    /// <summary>Duration of audio extracted from the middle of the track for analysis.</summary>
    private const int AnalysisDurationSeconds = 30;

    /// <summary>
    /// A magnitude bin is considered "signal" rather than noise when it is at least this
    /// many dB above the estimated noise floor.
    /// </summary>
    private const double SignalAboveNoiseDb = 18.0;

    /// <summary>Rolloff window (Hz on each side of the cutoff) used to measure slope.</summary>
    private const double SlopeWindowHz = 2000.0;

    /// <summary>Cutoff below which a file is classified as TranscodedLowBitrate.</summary>
    private const double LowBitrateCutoffHz = 15_000.0;

    /// <summary>Cutoff below which a file is classified as TranscodedMediumBitrate.</summary>
    private const double MediumBitrateCutoffHz = 18_000.0;

    /// <summary>Cutoff above which a file is classified as GenuineLossless.</summary>
    private const double GenuineLosslessCutoffHz = 20_000.0;

    /// <summary>
    /// Maximum declared bitrate (kbps) in a container for metadata to be considered
    /// suspicious for a file presented as lossless.  Lossless FLAC typically encodes at
    /// 400–1 400 kbps depending on content; values at or below this suggest a lossy source.
    /// </summary>
    private const int SuspiciousBitrateThresholdKbps = 400;

    // ─────────────────────────────────────────────────────────────────────────

    private readonly ILogger<AudioIntegrityService> _logger;

    public AudioIntegrityService(ILogger<AudioIntegrityService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<SpectralIntegrityResult> AnalyseAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Spectral integrity analysis starting: {Path}", filePath);

        if (!System.IO.File.Exists(filePath))
        {
            return Failed("File not found");
        }

        try
        {
            // Run the CPU-intensive work on a dedicated thread-pool thread so it does
            // not block the UI/async scheduler.
            return await Task.Factory.StartNew(
                () => RunAnalysis(filePath, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Spectral analysis cancelled for: {Path}", filePath);
            return Failed("Analysis cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spectral analysis failed for: {Path}", filePath);
            return Failed($"Analysis failed: {ex.Message}");
        }
    }

    // ── internal analysis pipeline ────────────────────────────────────────────

    private SpectralIntegrityResult RunAnalysis(string filePath, CancellationToken ct)
    {
        // ── 1. Read file metadata (non-fatal) ─────────────────────────────────
        var meta = ReadMetadata(filePath);

        // ── 2. Decode PCM samples ─────────────────────────────────────────────
        float[] monoSamples;
        int sampleRate;

        try
        {
            (monoSamples, sampleRate) = DecodeMono(filePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not decode audio from {Path}", filePath);
            return Failed($"Cannot decode audio: {ex.Message}", meta);
        }

        if (monoSamples.Length < FftSize * 4)
        {
            return Failed("Audio too short for spectral analysis", meta);
        }

        // ── 3. Extract middle segment ─────────────────────────────────────────
        var analysisSamples = Math.Min(monoSamples.Length, AnalysisDurationSeconds * sampleRate);
        var startIdx = (monoSamples.Length - analysisSamples) / 2;
        var segment = monoSamples.AsSpan(startIdx, analysisSamples);

        ct.ThrowIfCancellationRequested();

        // ── 4 & 5. Windowed FFT + temporal averaging ──────────────────────────
        var averagedSpectrum = ComputeAveragedPowerSpectrum(segment, ct);

        ct.ThrowIfCancellationRequested();

        // ── 6. Spectral cutoff detection ──────────────────────────────────────
        var nyquist = sampleRate / 2.0;
        var binHz = nyquist / (averagedSpectrum.Length - 1);

        var (cutoffHz, baseline) = FindSpectralCutoff(averagedSpectrum, binHz);

        // ── 7. Rolloff steepness ──────────────────────────────────────────────
        var slopeDpkHz = MeasureRolloffSteepness(averagedSpectrum, binHz, cutoffHz, baseline);

        // ── 8. Band energies ──────────────────────────────────────────────────
        var midBand = BandEnergyDbfs(averagedSpectrum, binHz, 1_000, 15_000);
        var highBand = BandEnergyDbfs(averagedSpectrum, binHz, 15_000, 20_000);
        var ultraHigh = BandEnergyDbfs(averagedSpectrum, binHz, 20_000, nyquist);

        // ── 9. Classify ───────────────────────────────────────────────────────
        return Classify(cutoffHz, slopeDpkHz, midBand, highBand, ultraHigh, meta);
    }

    // ── step 1: file metadata ─────────────────────────────────────────────────

    private static FileMetadata ReadMetadata(string filePath)
    {
        try
        {
            using var tag = TagLib.File.Create(filePath);
            var props = tag.Properties;
            return new FileMetadata(
                BitDepth: props?.BitsPerSample ?? 0,
                SampleRateHz: props?.AudioSampleRate ?? 0,
                BitrateKbps: props?.AudioBitrate ?? 0);
        }
        catch
        {
            // Metadata reading is best-effort; do not abort analysis.
            return new FileMetadata(0, 0, 0);
        }
    }

    // ── step 2: PCM decoding ──────────────────────────────────────────────────

    /// <summary>
    /// Opens the audio file with NAudio's <see cref="AudioFileReader"/> (supports WAV, MP3,
    /// AIFF and FLAC on all platforms) and reads all samples into a mono float array.
    /// </summary>
    private static (float[] Samples, int SampleRate) DecodeMono(string filePath, CancellationToken ct)
    {
        using var reader = new AudioFileReader(filePath);
        var format = reader.WaveFormat;
        var sampleRate = format.SampleRate;
        var channels = format.Channels;

        // We read up to AnalysisDurationSeconds * 2 from the centre, plus some margin.
        // To avoid loading very large files fully, limit to 3× the analysis window.
        var maxSamplesToRead = sampleRate * AnalysisDurationSeconds * 3;

        var buffer = new float[FftSize * channels];
        var accumulated = new System.Collections.Generic.List<float>(maxSamplesToRead);

        int read;
        float invChannels = 1f / channels;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0 && accumulated.Count < maxSamplesToRead)
        {
            ct.ThrowIfCancellationRequested();

            // Downmix multi-channel to mono by averaging channels
            for (int i = 0; i < read; i += channels)
            {
                float sum = 0f;
                for (int ch = 0; ch < channels && i + ch < read; ch++)
                    sum += buffer[i + ch];
                accumulated.Add(sum * invChannels);
            }
        }

        return (accumulated.ToArray(), sampleRate);
    }

    // ── steps 4 & 5: windowed FFT + averaging ─────────────────────────────────

    /// <summary>
    /// Applies a Hann-windowed real FFT to overlapping frames of <paramref name="signal"/>
    /// and returns the temporally averaged power spectrum (magnitude², linear scale).
    /// </summary>
    private static float[] ComputeAveragedPowerSpectrum(ReadOnlySpan<float> signal, CancellationToken ct)
    {
        var fft = new RealFft(FftSize);
        var window = Window.OfType(WindowType.Hann, FftSize);
        var spectrumBins = FftSize / 2 + 1;
        var averaged = new double[spectrumBins];

        var frameBuffer = new float[FftSize];
        var re = new float[FftSize];
        var im = new float[FftSize];
        var hop = FftSize / HopDivisor;
        int frameCount = 0;

        for (int offset = 0; offset + FftSize <= signal.Length; offset += hop)
        {
            ct.ThrowIfCancellationRequested();

            // Copy frame and apply Hann window
            signal.Slice(offset, FftSize).CopyTo(frameBuffer);
            for (int i = 0; i < FftSize; i++)
                frameBuffer[i] *= window[i];

            // Forward FFT
            fft.Direct(frameBuffer, re, im);

            // Accumulate power (magnitude²) into the averaged array
            for (int b = 0; b < spectrumBins; b++)
            {
                var mag = re[b] * re[b] + im[b] * im[b];
                averaged[b] += mag;
            }

            frameCount++;
        }

        if (frameCount == 0)
            return new float[spectrumBins];

        // Normalise
        var result = new float[spectrumBins];
        for (int b = 0; b < spectrumBins; b++)
            result[b] = (float)(averaged[b] / frameCount);

        return result;
    }

    // ── step 6: spectral cutoff detection ─────────────────────────────────────

    /// <summary>
    /// Locates the spectral cutoff frequency by:
    /// 1. Computing a reference baseline from the reliable 500 Hz – 10 kHz band
    ///    (both genuine lossless and lossy audio always carry signal here).
    /// 2. Scanning from Nyquist downward for the highest bin whose power is at least
    ///    <see cref="SignalAboveNoiseDb"/> dB below that baseline.
    ///
    /// Using a mid-frequency baseline instead of estimating noise from the top bins
    /// correctly handles broadband signals such as white noise or real music — where
    /// the top bins carry meaningful content — as well as narrowband signals
    /// low-pass–filtered below Nyquist (the canonical "fake FLAC" profile).
    /// </summary>
    private static (double CutoffHz, double Baseline) FindSpectralCutoff(
        float[] spectrum, double binHz)
    {
        int bins = spectrum.Length;

        // Compute baseline from the 500 Hz–10 kHz reference band.
        var loBaselineBin = Math.Max(1, (int)(500.0 / binHz));
        var hiBaselineBin = Math.Min(bins - 1, (int)(10_000.0 / binHz));

        double baselineSum = 0;
        int baselineCount = 0;
        for (int b = loBaselineBin; b <= hiBaselineBin; b++)
        {
            baselineSum += spectrum[b];
            baselineCount++;
        }

        var baseline = baselineCount > 0 ? baselineSum / baselineCount : double.Epsilon;
        if (baseline <= 0) baseline = double.Epsilon;

        // Threshold: bins at least SignalAboveNoiseDb below baseline are considered noise.
        // A bin must be ABOVE this threshold to be counted as carrying signal.
        var threshold = baseline * Math.Pow(10.0, -SignalAboveNoiseDb / 10.0);

        // Scan from Nyquist downward
        for (int b = bins - 1; b >= 0; b--)
        {
            if (spectrum[b] >= threshold)
                return (b * binHz, baseline);
        }

        return (0.0, baseline);
    }

    // ── step 7: rolloff steepness ─────────────────────────────────────────────

    /// <summary>
    /// Measures the steepness of the energy drop in a ±<see cref="SlopeWindowHz"/> window
    /// around <paramref name="cutoffHz"/> and returns the slope in dB/kHz.
    /// </summary>
    private static double MeasureRolloffSteepness(
        float[] spectrum, double binHz, double cutoffHz, double noiseFloor)
    {
        if (cutoffHz <= 0 || binHz <= 0) return 0.0;

        var peakBin = (int)(cutoffHz / binHz);
        var windowBins = (int)(SlopeWindowHz / binHz);

        var loBin = Math.Max(0, peakBin - windowBins);
        var hiBin = Math.Min(spectrum.Length - 1, peakBin + windowBins / 4);

        if (hiBin <= loBin) return 0.0;

        // Convert to dBFS, clamped at noise floor to avoid −∞
        double DbFs(int bin) =>
            10.0 * Math.Log10(Math.Max(spectrum[bin], noiseFloor));

        var energyAtPeak = DbFs(peakBin);
        var energyAtLo = DbFs(loBin);

        var deltaDb = energyAtPeak - energyAtLo;          // positive = rising toward cutoff
        var deltaKhz = (peakBin - loBin) * binHz / 1000.0;

        return deltaKhz > 0 ? deltaDb / deltaKhz : 0.0;
    }

    // ── band energy helper ────────────────────────────────────────────────────

    private static double BandEnergyDbfs(float[] spectrum, double binHz, double loHz, double hiHz)
    {
        var loBin = Math.Max(0, (int)(loHz / binHz));
        var hiBin = Math.Min(spectrum.Length - 1, (int)(hiHz / binHz));

        if (hiBin < loBin) return -120.0;

        double sum = 0;
        for (int b = loBin; b <= hiBin; b++) sum += spectrum[b];

        var avg = sum / (hiBin - loBin + 1);
        return avg > 0 ? 10.0 * Math.Log10(avg) : -120.0;
    }

    // ── step 9: classify ──────────────────────────────────────────────────────

    private SpectralIntegrityResult Classify(
        double cutoffHz,
        double slopeDpkHz,
        double midBandDbfs,
        double highBandDbfs,
        double ultraHighDbfs,
        FileMetadata meta)
    {
        // A very steep slope (≥ 30 dB/kHz) near the cutoff is a strong lossy indicator.
        // A shallow slope (< 10 dB/kHz) near the cutoff suggests natural rolloff.
        bool steepCutoff = slopeDpkHz >= 30.0;
        bool veryShallowRolloff = slopeDpkHz < 10.0;

        // Bit-depth sanity: lossy files re-wrapped may still be declared as 24-bit.
        // A declared bitrate ≤ SuspiciousBitrateThresholdKbps for a "lossless" file is a metadata red flag.
        bool metadataSuspicious = meta.BitrateKbps > 0 && meta.BitrateKbps <= SuspiciousBitrateThresholdKbps;

        AudioAuthenticityVerdict verdict;
        double confidence;
        string reason;

        if (cutoffHz >= GenuineLosslessCutoffHz)
        {
            // High cutoff — genuine OR very-high-bitrate transcode (rare)
            if (steepCutoff && metadataSuspicious)
            {
                verdict = AudioAuthenticityVerdict.TranscodedHighBitrate;
                confidence = 0.65;
                reason = $"Spectral content reaches {cutoffHz / 1000:F1} kHz but the sharp " +
                         $"rolloff ({slopeDpkHz:F0} dB/kHz) and declared bitrate " +
                         $"({meta.BitrateKbps} kbps) suggest a high-bitrate lossy source.";
            }
            else
            {
                confidence = veryShallowRolloff ? 0.92 : 0.78;
                verdict = AudioAuthenticityVerdict.GenuineLossless;
                reason = $"Spectral content extends to {cutoffHz / 1000:F1} kHz " +
                         $"with a natural rolloff slope ({slopeDpkHz:F0} dB/kHz). " +
                         "No lossy-encoder cutoff pattern detected.";
            }
        }
        else if (cutoffHz >= MediumBitrateCutoffHz)
        {
            // 18–20 kHz — classic 256–320 kbps MP3 / 256 kbps AAC territory
            confidence = steepCutoff ? 0.88 : 0.68;
            verdict = AudioAuthenticityVerdict.TranscodedHighBitrate;
            reason = $"Spectral cutoff at {cutoffHz / 1000:F1} kHz" +
                     (steepCutoff ? $" with steep rolloff ({slopeDpkHz:F0} dB/kHz)" : "") +
                     " is consistent with a 256–320 kbps lossy source.";
        }
        else if (cutoffHz >= LowBitrateCutoffHz)
        {
            // 15–18 kHz — typical 128–192 kbps MP3
            confidence = steepCutoff ? 0.92 : 0.75;
            verdict = AudioAuthenticityVerdict.TranscodedMediumBitrate;
            reason = $"Spectral cutoff at {cutoffHz / 1000:F1} kHz" +
                     (steepCutoff ? $" with steep rolloff ({slopeDpkHz:F0} dB/kHz)" : "") +
                     " is consistent with a 128–192 kbps lossy source.";
        }
        else
        {
            // < 15 kHz — low-bitrate lossy
            confidence = 0.95;
            verdict = AudioAuthenticityVerdict.TranscodedLowBitrate;
            reason = $"Spectral cutoff at {cutoffHz / 1000:F1} kHz indicates a low-bitrate " +
                     "lossy source (≤ 128 kbps).";
        }

        _logger.LogInformation(
            "Analysis complete: verdict={Verdict} confidence={Confidence:P0} cutoff={Cutoff:F0} Hz slope={Slope:F0} dB/kHz",
            verdict, confidence, cutoffHz, slopeDpkHz);

        return new SpectralIntegrityResult
        {
            Verdict = verdict,
            Confidence = confidence,
            Reason = reason,
            SpectralCutoffHz = cutoffHz,
            RolloffSteepnessDpkHz = slopeDpkHz,
            MidBandEnergyDbfs = midBandDbfs,
            HighBandEnergyDbfs = highBandDbfs,
            UltraHighBandEnergyDbfs = ultraHighDbfs,
            FileBitDepth = meta.BitDepth,
            FileSampleRateHz = meta.SampleRateHz,
            FileBitrateKbps = meta.BitrateKbps,
        };
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static SpectralIntegrityResult Failed(string reason, FileMetadata? meta = null) =>
        new()
        {
            Verdict = AudioAuthenticityVerdict.Unknown,
            Confidence = 0,
            Reason = reason,
            FileBitDepth = meta?.BitDepth ?? 0,
            FileSampleRateHz = meta?.SampleRateHz ?? 0,
            FileBitrateKbps = meta?.BitrateKbps ?? 0,
        };

    private sealed record FileMetadata(int BitDepth, int SampleRateHz, int BitrateKbps);
}