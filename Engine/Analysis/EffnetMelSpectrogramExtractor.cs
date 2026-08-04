using System;
using System.Collections.Generic;
using NWaves.Filters.Fda;
using NWaves.Transforms;
using NWaves.Windows;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Computes mel-spectrogram patches matching Essentia's <c>TensorflowInputMusiCNN</c>
/// preprocessing — the recipe the EffnetDiscogs family of ONNX models (base embedding +
/// genre/mood classifier heads) expects as input. ORBIT's bundled Essentia CLI binary turned
/// out to silently ignore the TensorFlow-model layer entirely (confirmed empirically — see
/// project history), so these models are run directly via ONNX Runtime instead; that means
/// preprocessing that Essentia would normally do internally has to be reproduced here exactly.
///
/// Parameters cross-verified against three independent MTG/Essentia sources (essentia-labs
/// blog, essentia.js docs, and a GitHub issue reproducing the recipe in Librosa), all agreeing
/// on: 16 kHz, 512-sample frames, 256-sample hop, 96 Slaney-scale mel bands, power spectrum,
/// log(10000·x + 1) compression. EffnetDiscogs specifically patches the mel-spectrogram into
/// 128-frame windows (confirmed via the model's own published input shape, ["n", 128, 96]) —
/// this is distinct from the separate MusiCNN-family models (danceability, voice/instrumental,
/// tonal/DJ-tool, genre_electronic), which use 187-frame patches and are NOT covered here.
///
/// Residual uncertainty: the exact mel-filter gain normalization convention ("unit_tri" in
/// Essentia's profile terminology) and window function were not found in a primary source —
/// mapped to NWaves' `MelBankSlaney(normalizeGain: false)` (unit-height triangles, matching
/// "unit_tri" literally) and a standard Hann window respectively. Validate output against a
/// track with a known genre before trusting this in production.
/// </summary>
public static class EffnetMelSpectrogramExtractor
{
    public const int SampleRate = 16000;
    public const int FrameSize = 512;
    public const int HopSize = 256;
    public const int MelBands = 96;
    public const int PatchFrames = 128;

    /// <summary>
    /// Downsamples mono audio at an arbitrary source rate to 16 kHz via linear interpolation.
    /// Not audiophile-grade, but more than sufficient for feeding a classifier model — small
    /// resampling-quality differences here don't meaningfully change classification results,
    /// unlike the mel-filterbank math itself.
    /// </summary>
    public static float[] ResampleTo16k(float[] monoAudio, int sourceSampleRate)
    {
        if (sourceSampleRate == SampleRate) return monoAudio;
        if (monoAudio.Length == 0) return monoAudio;

        double ratio = (double)sourceSampleRate / SampleRate;
        int outLength = (int)(monoAudio.Length / ratio);
        var resampled = new float[outLength];

        for (int i = 0; i < outLength; i++)
        {
            double srcPos = i * ratio;
            int idx0 = (int)srcPos;
            int idx1 = Math.Min(idx0 + 1, monoAudio.Length - 1);
            float frac = (float)(srcPos - idx0);
            resampled[i] = monoAudio[idx0] * (1f - frac) + monoAudio[idx1] * frac;
        }

        return resampled;
    }

    /// <summary>
    /// Splits mono 16 kHz audio into non-overlapping mel-spectrogram patches shaped
    /// [PatchFrames, MelBands] (row-major: time-major, matching the model's documented
    /// ["n", 128, 96] input). Drops a final partial patch shorter than <see cref="PatchFrames"/>.
    /// </summary>
    public static List<float[]> ExtractPatches(float[] monoAudio16k)
    {
        var patches = new List<float[]>();
        if (monoAudio16k == null || monoAudio16k.Length < FrameSize)
            return patches;

        var melFrames = ComputeMelFrames(monoAudio16k);
        int patchCount = melFrames.Count / PatchFrames;

        for (int p = 0; p < patchCount; p++)
        {
            var patch = new float[PatchFrames * MelBands];
            for (int t = 0; t < PatchFrames; t++)
                Array.Copy(melFrames[p * PatchFrames + t], 0, patch, t * MelBands, MelBands);
            patches.Add(patch);
        }

        return patches;
    }

    /// <summary>
    /// Computes one 96-band log-mel-energy vector per STFT hop across the whole signal.
    /// </summary>
    private static List<float[]> ComputeMelFrames(float[] monoAudio16k)
    {
        var fft = new RealFft(FrameSize);
        var window = Window.OfType(WindowType.Hann, FrameSize);
        int numBins = FrameSize / 2 + 1;

        // Slaney-style triangular mel filterbank — matches Essentia's warpingFormula: 'slaneyMel'.
        // normalizeGain: false → unit-height triangles ("unit_tri" in Essentia's own terminology),
        // not area-normalized gain.
        var filterbank = FilterBanks.MelBankSlaney(
            MelBands, FrameSize, SampleRate, lowFreq: 0, highFreq: SampleRate / 2.0, normalizeGain: false);

        int frameCount = (monoAudio16k.Length - FrameSize) / HopSize + 1;
        var melFrames = new List<float[]>(Math.Max(0, frameCount));

        var frameBuffer = new float[FrameSize];
        var re = new float[FrameSize];
        var im = new float[FrameSize];
        var power = new float[numBins];

        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * HopSize;

            for (int n = 0; n < FrameSize; n++)
                frameBuffer[n] = monoAudio16k[offset + n] * window[n];

            fft.Direct(frameBuffer, re, im);

            // Power spectrum (Essentia power=2.0 — magnitude squared, not just magnitude).
            for (int k = 0; k < numBins; k++)
                power[k] = re[k] * re[k] + im[k] * im[k];

            // Applied directly (not via NWaves' FilterBanks.Apply helper) — that method's inner
            // loop indexes its spectrogram argument using the filter-index variable rather than
            // the frame-index variable, so it only works when filter count == frame count. Not
            // our case (96 filters, 1 frame at a time here), so a plain dot product instead.
            var logMel = new float[MelBands];
            for (int b = 0; b < MelBands; b++)
            {
                float energy = 0f;
                var filter = filterbank[b];
                for (int k = 0; k < numBins; k++)
                    energy += filter[k] * power[k];

                logMel[b] = (float)Math.Log(10000.0 * energy + 1.0);
            }

            melFrames.Add(logMel);
        }

        return melFrames;
    }
}
