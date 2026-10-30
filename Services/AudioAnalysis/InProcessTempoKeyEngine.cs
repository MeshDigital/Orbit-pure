using System;
using System.Collections.Generic;
using System.Linq;
using NWaves.Transforms;
using NWaves.Windows;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Engine.Analysis;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// In-process BPM / key / beat-grid / rhythm descriptor extraction using NWaves FFT over NAudio-decoded
/// PCM. This is the "NWaves/NAudio profile" alternative to the Essentia CLI: it produces the same
/// <see cref="EssentiaOutput"/> shape the rest of the pipeline consumes, so
/// <see cref="BpmDetectionService"/>, <see cref="KeyDetectionService"/> and
/// <see cref="BeatgridDetectionService"/> run unchanged on its result.
///
///   BPM   — onset-strength (spectral-flux novelty) autocorrelation, the standard MIR tempo method.
///   Key   — 12-bin chroma correlated against Krumhansl and EDMA major/minor profiles.
///   Beats — a constant grid from the detected BPM plus an onset-aligned phase;
///           <see cref="BeatGridFitter"/> refines it downstream exactly as it does Essentia's ticks.
/// </summary>
public sealed class InProcessTempoKeyEngine
{
    private const int FftSize = 2048;
    private const int HopSize = 512;
    private const int MinBpm = 40;
    private const int MaxBpm = 208;

    private static readonly string[] NoteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    // Krumhansl–Schmuckler key profiles (tonic-first), the standard MIR reference.
    private static readonly float[] KrumhanslMajor = [6.35f, 2.23f, 3.48f, 2.33f, 4.38f, 4.09f, 2.52f, 5.19f, 2.39f, 3.66f, 2.29f, 2.88f];
    private static readonly float[] KrumhanslMinor = [6.33f, 2.68f, 3.52f, 5.38f, 2.60f, 3.53f, 2.54f, 4.75f, 3.98f, 2.69f, 3.34f, 3.17f];
    // Essentia's EDMA profiles (major; minor is shared with Krumhansl in Essentia's reference).
    private static readonly float[] EdmaMajor = [6.86f, 2.39f, 3.50f, 2.62f, 4.14f, 3.64f, 2.41f, 4.84f, 2.37f, 3.46f, 2.29f, 2.53f];
    private static readonly float[] EdmaMinor = [6.33f, 2.68f, 3.52f, 5.38f, 2.60f, 3.53f, 2.54f, 4.75f, 3.98f, 2.69f, 3.34f, 3.17f];

    private readonly SpectralFluxNoveltyEngine _novelty = new();

    /// <summary>
    /// Analyses a mono PCM buffer and returns an <see cref="EssentiaOutput"/>-shaped result. Returns
    /// an empty output (no rhythm/tonal/lowlevel) when the signal is too short to analyse, so callers
    /// degrade the same way they do when the Essentia binary is absent.
    /// </summary>
    public EssentiaOutput Analyze(float[] mono, int sampleRate, double durationSeconds)
    {
        if (mono is null || mono.Length < FftSize || sampleRate <= 0)
            return new EssentiaOutput();

        // Near-silent input (blank/corrupt file) has no tempo or key to detect.
        double rmsSq = 0;
        for (int i = 0; i < mono.Length; i++)
            rmsSq += (double)mono[i] * mono[i];
        if (Math.Sqrt(rmsSq / mono.Length) < 1e-6)
            return new EssentiaOutput();

        var novelty = _novelty.ComputeNoveltyFunction(mono, sampleRate, FftSize, HopSize);
        double hopSeconds = (double)HopSize / sampleRate;

        (float bpm, float confidence, float[] histogram) = EstimateTempo(novelty, hopSeconds);
        double onsetRate = CountOnsets(novelty, hopSeconds, durationSeconds);

        var (edma, krumhansl) = EstimateKey(mono, sampleRate);
        var ticks = SynthesiseBeats(novelty, hopSeconds, durationSeconds, bpm);
        (float centroid, float complexity, float rms, float dynamicComplexity) = ComputeLowLevel(mono, sampleRate);

        return new EssentiaOutput
        {
            Rhythm = new RhythmData
            {
                Bpm = bpm,
                BpmHistogram = histogram,
                BpmHistogramFirstPeakWeight = confidence,
                OnsetRate = (float)onsetRate,
                Danceability = EstimateDanceability(onsetRate, confidence),
                Ticks = ticks,
            },
            Tonal = new TonalData
            {
                KeyEdma = edma,
                KeyKrumhansl = krumhansl,
            },
            LowLevel = new LowLevelData
            {
                SpectralCentroid = new StatsData { Mean = centroid },
                SpectralComplexity = new StatsData { Mean = complexity },
                Rms = new StatsData { Mean = rms },
                DynamicComplexity = dynamicComplexity,
            },
        };
    }

    // ── tempo ─────────────────────────────────────────────────────────────

    private static (float Bpm, float Confidence, float[] Histogram) EstimateTempo(float[] novelty, double hopSeconds)
    {
        int n = novelty.Length;
        if (n < 64)
            return (0f, 0f, Array.Empty<float>());

        int minLag = Math.Max(1, (int)Math.Round(60.0 / (MaxBpm * hopSeconds)));
        int maxLag = Math.Min(n - 1, (int)Math.Round(60.0 / (MinBpm * hopSeconds)));
        if (maxLag <= minLag)
            return (0f, 0f, Array.Empty<float>());

        // Unbiased autocorrelation (normalised by the overlap length at each lag).
        var ac = new double[maxLag + 1];
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = lag; i < n; i++)
                sum += (double)novelty[i] * novelty[i - lag];
            ac[lag] = sum / (n - lag);
        }

        int bestLag = minLag;
        double bestAc = double.MinValue;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            if (ac[lag] > bestAc) { bestAc = ac[lag]; bestLag = lag; }
        }

        // Parabolic interpolation around the peak for sub-lag precision — a whole-lag read at a
        // 512-sample hop quantises the tempo by ~1 BPM at DnB speeds; the true peak sits between lags.
        double refinedLag = bestLag;
        if (bestLag > minLag && bestLag < maxLag)
        {
            double denom = ac[bestLag - 1] - 2 * ac[bestLag] + ac[bestLag + 1];
            if (Math.Abs(denom) > 1e-12)
                refinedLag = bestLag + 0.5 * (ac[bestLag - 1] - ac[bestLag + 1]) / denom;
        }

        double rawBpm = 60.0 / (refinedLag * hopSeconds);

        // Build a 1-BPM-bin histogram from the autocorrelation so BpmDetectionService's peakedness
        // and stability math (and its half-time correction) work on a real tempo distribution.
        var hist = new float[MaxBpm - MinBpm + 1];
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double bpm = 60.0 / (lag * hopSeconds);
            int bin = (int)Math.Round(bpm) - MinBpm;
            bin = Math.Clamp(bin, 0, hist.Length - 1);
            hist[bin] += (float)Math.Max(0, ac[lag]);
        }

        float maxMass = hist.Length > 0 ? hist.Max() : 0f;
        float totalMass = hist.Sum();
        if (maxMass > 0f)
            for (int i = 0; i < hist.Length; i++) hist[i] /= maxMass;

        float confidence = totalMass > 0f ? maxMass / totalMass : 0f;
        return ((float)Math.Round(rawBpm, 3), confidence, hist);
    }

    private static double CountOnsets(float[] novelty, double hopSeconds, double durationSeconds)
    {
        if (durationSeconds <= 0 || novelty.Length < 3)
            return 0;

        int count = 0;
        for (int i = 1; i < novelty.Length - 1; i++)
        {
            if (novelty[i] > novelty[i - 1] && novelty[i] >= novelty[i + 1] && novelty[i] >= 0.25f)
                count++;
        }
        return count / durationSeconds;
    }

    // ── key ───────────────────────────────────────────────────────────────

    private static (KeyData Edma, KeyData Krumhansl) EstimateKey(float[] mono, int sampleRate)
    {
        var chroma = ComputeChroma(mono, sampleRate);
        if (chroma is null)
            return (new KeyData(), new KeyData());

        var edma = BestKey(chroma, EdmaMajor, EdmaMinor);
        var krumhansl = BestKey(chroma, KrumhanslMajor, KrumhanslMinor);

        return (
            new KeyData { Key = edma.Key, Scale = edma.Scale, Strength = edma.Strength },
            new KeyData { Key = krumhansl.Key, Scale = krumhansl.Scale, Strength = krumhansl.Strength });
    }

    private static float[]? ComputeChroma(float[] mono, int sampleRate)
    {
        const int fftSize = 4096;
        const int hopSize = 2048;
        if (mono.Length < fftSize)
            return null;

        var fft = new RealFft(fftSize);
        var window = Window.OfType(WindowType.Hann, fftSize);
        var frame = new float[fftSize];
        var re = new float[fftSize];
        var im = new float[fftSize];
        var chroma = new double[12];

        int frames = 0;
        for (int offset = 0; offset + fftSize <= mono.Length; offset += hopSize)
        {
            for (int i = 0; i < fftSize; i++)
                frame[i] = mono[offset + i] * window[i];

            fft.Direct(frame, re, im);

            for (int k = 1; k <= fftSize / 2; k++)
            {
                double freq = (double)k * sampleRate / fftSize;
                if (freq < 27.5 || freq > 2000.0) continue;

                double mag = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);
                int midi = (int)Math.Round(12.0 * Math.Log2(freq / 440.0) + 69.0);
                int semitone = ((midi % 12) + 12) % 12;
                chroma[semitone] += mag;
            }
            frames++;
        }

        if (frames == 0)
            return null;

        double norm = 0;
        for (int i = 0; i < 12; i++) norm += chroma[i] * chroma[i];
        norm = Math.Sqrt(norm);
        if (norm <= 1e-9)
            return null;

        var result = new float[12];
        for (int i = 0; i < 12; i++) result[i] = (float)(chroma[i] / norm);
        return result;
    }

    private static (string Key, string Scale, float Strength) BestKey(float[] chroma, float[] major, float[] minor)
    {
        float best = float.MinValue;
        string key = "C";
        string scale = "major";
        for (int root = 0; root < 12; root++)
        {
            float majorScore = Correlate(chroma, major, root);
            if (majorScore > best) { best = majorScore; key = NoteNames[root]; scale = "major"; }
            float minorScore = Correlate(chroma, minor, root);
            if (minorScore > best) { best = minorScore; key = NoteNames[root]; scale = "minor"; }
        }
        return (key, scale, best);
    }

    private static float Correlate(float[] chroma, float[] profile, int root)
    {
        // Expected chroma for a tonic at pitch class `root`: profile interval shifted onto it.
        var shifted = new float[12];
        for (int i = 0; i < 12; i++)
            shifted[i] = profile[((i - root) % 12 + 12) % 12];

        double meanX = 0, meanY = 0;
        for (int i = 0; i < 12; i++) { meanX += chroma[i]; meanY += shifted[i]; }
        meanX /= 12; meanY /= 12;

        double cov = 0, varX = 0, varY = 0;
        for (int i = 0; i < 12; i++)
        {
            double dx = chroma[i] - meanX;
            double dy = shifted[i] - meanY;
            cov += dx * dy;
            varX += dx * dx;
            varY += dy * dy;
        }

        if (varX <= 0 || varY <= 0) return 0f;
        return (float)(cov / Math.Sqrt(varX * varY));
    }

    // ── beats ─────────────────────────────────────────────────────────────

    private static float[] SynthesiseBeats(float[] novelty, double hopSeconds, double durationSeconds, float bpm)
    {
        if (bpm <= 0 || durationSeconds <= 0)
            return Array.Empty<float>();

        double period = 60.0 / bpm;
        double phase = EstimateBeatPhase(novelty, hopSeconds, period);

        int count = (int)Math.Floor((durationSeconds - phase) / period) + 1;
        if (count <= 0)
            return Array.Empty<float>();

        var beats = new float[count];
        for (int k = 0; k < count; k++)
            beats[k] = (float)(phase + k * period);
        return beats;
    }

    private static double EstimateBeatPhase(float[] novelty, double hopSeconds, double period)
    {
        const int bins = 48;
        var hist = new double[bins];
        for (int i = 1; i < novelty.Length - 1; i++)
        {
            if (novelty[i] > novelty[i - 1] && novelty[i] >= novelty[i + 1] && novelty[i] >= 0.25f)
            {
                double t = i * hopSeconds;
                double phase = t - Math.Floor(t / period) * period;
                int bin = Math.Min(bins - 1, (int)(phase / period * bins));
                hist[bin] += novelty[i];
            }
        }

        int best = 0;
        for (int b = 1; b < bins; b++)
            if (hist[b] > hist[best]) best = b;

        return (best + 0.5) / bins * period;
    }

    // ── low-level descriptors ─────────────────────────────────────────────

    private static (float Centroid, float Complexity, float Rms, float DynamicComplexity) ComputeLowLevel(float[] mono, int sampleRate)
    {
        const int fftSize = 2048;
        const int hopSize = 1024;
        if (mono.Length < fftSize)
            return (0f, 0f, 0f, 0f);

        var fft = new RealFft(fftSize);
        var window = Window.OfType(WindowType.Hann, fftSize);
        var frame = new float[fftSize];
        var re = new float[fftSize];
        var im = new float[fftSize];

        double centroidSum = 0;
        double complexitySum = 0;
        var frameRms = new List<double>();
        int frames = 0;

        for (int offset = 0; offset + fftSize <= mono.Length; offset += hopSize)
        {
            double sumSq = 0;
            for (int i = 0; i < fftSize; i++)
            {
                frame[i] = mono[offset + i] * window[i];
                sumSq += frame[i] * frame[i];
            }
            frameRms.Add(Math.Sqrt(sumSq / fftSize));

            fft.Direct(frame, re, im);
            double totalMag = 0, weightedFreq = 0, sumMagSq = 0;
            for (int k = 1; k <= fftSize / 2; k++)
            {
                double mag = Math.Sqrt((double)re[k] * re[k] + (double)im[k] * im[k]);
                totalMag += mag;
                weightedFreq += mag * ((double)k * sampleRate / fftSize);
                sumMagSq += mag * mag;
            }

            if (totalMag > 0)
            {
                centroidSum += weightedFreq / totalMag;
                // Sparsity-based complexity: high when a few peaks dominate the spectrum.
                double bins = fftSize / 2.0;
                double sparsity = totalMag * totalMag / Math.Max(1e-9, bins * sumMagSq);
                complexitySum += 1.0 - Math.Clamp(sparsity, 0, 1);
            }
            frames++;
        }

        if (frames == 0)
            return (0f, 0f, 0f, 0f);

        float centroid = (float)(centroidSum / frames);
        float complexity = (float)(complexitySum / frames);

        double globalMeanSq = frameRms.Average(v => v * v);
        float rms = (float)Math.Sqrt(globalMeanSq);

        double meanRms = frameRms.Average();
        double varRms = frameRms.Average(v => (v - meanRms) * (v - meanRms));
        float dynamicComplexity = meanRms > 1e-9 ? (float)(Math.Sqrt(varRms) / meanRms * 3.0) : 0f;

        return (centroid, complexity, rms, dynamicComplexity);
    }

    private static float EstimateDanceability(double onsetRate, float tempoConfidence)
    {
        float rhythm = (float)Math.Clamp(onsetRate / 4.0, 0, 1); // ~4 onsets/sec ≈ very danceable
        return Math.Clamp(0.55f * rhythm + 0.45f * tempoConfidence, 0f, 1f);
    }
}
