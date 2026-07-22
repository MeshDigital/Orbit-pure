using System;
using System.Collections.Generic;
using NWaves.Transforms;
using NWaves.Windows;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Computes a half-wave rectified spectral flux novelty curve — the primary signal
/// used to detect energy onset events (builds, drops) in EDM and DnB.
///
/// Algorithm:
///   1. Compute STFT magnitude spectra at each hop.
///   2. For each bin, take the positive difference (half-wave rectification):
///      flux[t] = Σ max(0, |X[t,k]| - |X[t-1,k]|)
///   3. Smooth with a Gaussian kernel to reduce noise.
///   4. Subtract a local mean (novelty function) to enhance transient peaks.
///   5. Peak-pick for onset candidates.
///
/// This is what librosa.onset.onset_strength computes and what drives EDMFormer's
/// input feature stack. Outperforms RMS delta for EDM because it tracks spectral
/// change rather than just loudness change — critical for detecting drops where
/// a breakdown's sustained pad gives way to a loud kick+bass.
/// </summary>
public sealed class SpectralFluxNoveltyEngine
{
    private const int DefaultFftSize = 2048;
    private const int DefaultHopSize = 512;
    private const double GaussianSigmaSeconds = 0.1;
    private const double LocalMeanWindowSeconds = 2.0;

    /// <summary>
    /// Computes the spectral flux novelty curve for a mono audio signal.
    /// Returns one value per hop frame (resolution = hopSize / sampleRate seconds).
    /// </summary>
    public float[] ComputeNoveltyFunction(float[] monoSignal, int sampleRate,
        int fftSize = DefaultFftSize, int hopSize = DefaultHopSize)
    {
        if (monoSignal == null || monoSignal.Length == 0) return Array.Empty<float>();

        int numFrames = (monoSignal.Length - fftSize) / hopSize + 1;
        if (numFrames <= 1) return Array.Empty<float>();

        // Step 1: compute magnitude spectra per frame, via NWaves' real FFT — the original
        // implementation here was a naive O(fftSize) per-bin direct DFT (nested loop with
        // Math.Cos/Math.Sin per sample per bin), which for a 4-minute track works out to
        // roughly 20,000 frames x ~2M inner iterations each: tens of minutes per track.
        // RealFft (already used elsewhere in this codebase, e.g. WaveformExtractionService)
        // does the same job in O(fftSize log fftSize) per frame.
        int numBins = fftSize / 2 + 1;
        var fft = new RealFft(fftSize);
        var window = Window.OfType(WindowType.Hann, fftSize);
        var frameBuffer = new float[fftSize];
        var re = new float[fftSize];
        var im = new float[fftSize];

        var magnitudes = new float[numFrames][];
        for (int f = 0; f < numFrames; f++)
        {
            int offset = f * hopSize;
            magnitudes[f] = ComputeMagnitudeSpectrum(monoSignal, offset, fftSize, numBins, fft, window, frameBuffer, re, im);
        }

        // Step 2: half-wave rectified spectral flux
        var flux = new float[numFrames];
        for (int f = 1; f < numFrames; f++)
        {
            float sum = 0f;
            for (int k = 0; k < numBins; k++)
            {
                float delta = magnitudes[f][k] - magnitudes[f - 1][k];
                if (delta > 0f) sum += delta;
            }
            flux[f] = sum;
        }

        // Step 3: Gaussian smoothing
        double hopSeconds = (double)hopSize / sampleRate;
        flux = ApplyGaussianSmoothing(flux, hopSeconds, GaussianSigmaSeconds);

        // Step 4: subtract local mean to get novelty (suppress sustained energy)
        flux = SubtractLocalMean(flux, hopSeconds, LocalMeanWindowSeconds);

        // Step 5: half-wave rectify again (only positive novelty counts)
        for (int i = 0; i < flux.Length; i++)
            flux[i] = Math.Max(0f, flux[i]);

        // Normalize to [0, 1]
        float maxVal = 0f;
        foreach (var v in flux) if (v > maxVal) maxVal = v;
        if (maxVal > 1e-6f)
            for (int i = 0; i < flux.Length; i++)
                flux[i] /= maxVal;

        return flux;
    }

    /// <summary>
    /// Detects onset peaks in the novelty curve above a dynamic threshold.
    /// Returns timestamps in seconds.
    /// </summary>
    public List<(double TimestampSeconds, float Strength)> PickOnsetPeaks(
        float[] novelty, int sampleRate, int hopSize,
        double minPeakStrength = 0.35, double minGapSeconds = 0.2)
    {
        var peaks = new List<(double, float)>();
        if (novelty == null || novelty.Length < 3) return peaks;

        double hopSeconds = (double)hopSize / sampleRate;
        int minGapFrames = (int)Math.Ceiling(minGapSeconds / hopSeconds);
        int lastPeakFrame = -minGapFrames;

        for (int i = 1; i < novelty.Length - 1; i++)
        {
            if (novelty[i] > novelty[i - 1] && novelty[i] >= novelty[i + 1]
                && novelty[i] >= minPeakStrength
                && (i - lastPeakFrame) >= minGapFrames)
            {
                peaks.Add((i * hopSeconds, novelty[i]));
                lastPeakFrame = i;
            }
        }

        return peaks;
    }

    /// <summary>
    /// Detects a "build-up → drop" signature: novelty rises then spikes sharply.
    /// Returns the drop timestamp and pre-drop build region start.
    /// </summary>
    public List<(double DropSeconds, double BuildStartSeconds, float DropStrength)> DetectDropSignatures(
        float[] novelty, int sampleRate, int hopSize,
        double buildWindowSeconds = 8.0, double minDropStrength = 0.55)
    {
        var drops = new List<(double, double, float)>();
        if (novelty == null || novelty.Length < 3) return drops;

        double hopSeconds = (double)hopSize / sampleRate;
        int buildWindowFrames = (int)(buildWindowSeconds / hopSeconds);

        var peaks = PickOnsetPeaks(novelty, sampleRate, hopSize, minDropStrength, 1.0);
        foreach (var (ts, strength) in peaks)
        {
            int peakFrame = (int)(ts / hopSeconds);
            int buildStart = Math.Max(0, peakFrame - buildWindowFrames);

            // Confirm build: novelty should be rising in the window before the peak
            float buildSlope = ComputeLinearSlope(novelty, buildStart, peakFrame);
            if (buildSlope > 0.005f)
            {
                drops.Add((ts, buildStart * hopSeconds, strength));
            }
        }

        return drops;
    }

    // ── internal helpers ───────────────────────────────────────────────────

    private static float[] ComputeMagnitudeSpectrum(
        float[] signal, int offset, int fftSize, int numBins,
        RealFft fft, float[] window, float[] frameBuffer, float[] re, float[] im)
    {
        int len = Math.Min(fftSize, signal.Length - offset);

        Array.Clear(frameBuffer, 0, fftSize);
        for (int n = 0; n < len; n++)
            frameBuffer[n] = signal[offset + n] * window[n];

        fft.Direct(frameBuffer, re, im);

        var mags = new float[numBins];
        for (int k = 0; k < numBins; k++)
            mags[k] = (float)Math.Sqrt(re[k] * re[k] + im[k] * im[k]);

        return mags;
    }

    private static float[] ApplyGaussianSmoothing(float[] signal, double hopSeconds, double sigmaSeconds)
    {
        int sigmaFrames = Math.Max(1, (int)(sigmaSeconds / hopSeconds));
        int kernelRadius = sigmaFrames * 3;
        var kernel = new float[kernelRadius * 2 + 1];
        float kernelSum = 0f;

        for (int i = 0; i < kernel.Length; i++)
        {
            int d = i - kernelRadius;
            kernel[i] = (float)Math.Exp(-0.5 * d * d / (sigmaFrames * sigmaFrames));
            kernelSum += kernel[i];
        }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= kernelSum;

        var smoothed = new float[signal.Length];
        for (int i = 0; i < signal.Length; i++)
        {
            float sum = 0f;
            for (int j = 0; j < kernel.Length; j++)
            {
                int src = i + j - kernelRadius;
                if (src >= 0 && src < signal.Length)
                    sum += kernel[j] * signal[src];
            }
            smoothed[i] = sum;
        }

        return smoothed;
    }

    private static float[] SubtractLocalMean(float[] signal, double hopSeconds, double windowSeconds)
    {
        int halfWindow = Math.Max(1, (int)(windowSeconds / hopSeconds / 2));
        var result = new float[signal.Length];

        for (int i = 0; i < signal.Length; i++)
        {
            int start = Math.Max(0, i - halfWindow);
            int end = Math.Min(signal.Length - 1, i + halfWindow);
            float mean = 0f;
            for (int j = start; j <= end; j++) mean += signal[j];
            mean /= (end - start + 1);
            result[i] = signal[i] - mean;
        }

        return result;
    }

    private static float ComputeLinearSlope(float[] signal, int start, int end)
    {
        if (end <= start) return 0f;
        int n = end - start;
        float sumX = 0f, sumY = 0f, sumXY = 0f, sumX2 = 0f;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += signal[start + i];
            sumXY += i * signal[start + i];
            sumX2 += i * i;
        }
        float denom = n * sumX2 - sumX * sumX;
        return Math.Abs(denom) < 1e-6f ? 0f : (n * sumXY - sumX * sumY) / denom;
    }
}
