using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Computes 12-dimensional chroma vectors and tracks harmonic rhythm resets, chord-loop shifts, and modulations.
/// </summary>
public sealed class HarmonicPhaseTracker
{
    private static readonly string[] TonalKeys = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>
    /// Computes chroma vectors over time for the given signal.
    /// Each chroma vector has 12 elements representing semitone intensities.
    /// </summary>
    public List<float[]> ComputeChromaVectors(float[] signal, int sampleRate, double windowSeconds = 0.5, double hopSeconds = 0.25)
    {
        var chromaVectors = new List<float[]>();
        if (signal == null || signal.Length == 0) return chromaVectors;

        int windowSize = (int)(windowSeconds * sampleRate);
        int hopSize = (int)(hopSeconds * sampleRate);

        // Limit window size to power of 2 for FFT
        windowSize = Math.Max(512, NextPowerOfTwo(windowSize));
        hopSize = Math.Max(256, hopSize);

        for (int i = 0; i < signal.Length - windowSize; i += hopSize)
        {
            var frame = new float[windowSize];
            Array.Copy(signal, i, frame, 0, windowSize);

            // Apply Hanning Window
            for (int n = 0; n < windowSize; n++)
            {
                frame[n] *= (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / (windowSize - 1))));
            }

            // FFT
            double[] magnitudes = ComputeFFTMagnitude(frame);

            // Compute Chroma Vector
            var chroma = new float[12];
            for (int bin = 1; bin < magnitudes.Length; bin++)
            {
                double freq = (double)bin * sampleRate / windowSize;
                if (freq < 27.5 || freq > 2000.0) continue; // Focus on fundamental frequency ranges

                int midiNote = FrequencyToMidi(freq);
                int semitone = midiNote % 12;

                chroma[semitone] += (float)magnitudes[bin];
            }

            // Normalize (L2 norm)
            float norm = (float)Math.Sqrt(chroma.Sum(val => val * val));
            if (norm > 1e-4f)
            {
                for (int s = 0; s < 12; s++)
                {
                    chroma[s] /= norm;
                }
            }

            chromaVectors.Add(chroma);
        }

        return chromaVectors;
    }

    /// <summary>
    /// Detects harmonic rhythm resets (abrupt changes in chord structure).
    /// </summary>
    public List<double> DetectHarmonicResets(List<float[]> chromaVectors, double hopSeconds, double threshold = 0.35)
    {
        var resets = new List<double>();
        if (chromaVectors.Count < 2) return resets;

        for (int i = 1; i < chromaVectors.Count; i++)
        {
            double similarity = CosineSimilarity(chromaVectors[i - 1], chromaVectors[i]);
            // A sharp drop in similarity indicates a chord transition
            double distance = 1.0 - similarity;
            if (distance > threshold)
            {
                resets.Add(i * hopSeconds);
            }
        }

        return resets;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dotProduct = 0.0;
        double normA = 0.0;
        double normB = 0.0;

        for (int i = 0; i < 12; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA <= 0.0 || normB <= 0.0) return 0.0;
        return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static int FrequencyToMidi(double hz)
    {
        // MIDI 69 is A4 (440 Hz)
        return (int)Math.Round(12.0 * Math.Log2(hz / 440.0) + 69.0);
    }

    private static int NextPowerOfTwo(int val)
    {
        int p = 1;
        while (p < val) p <<= 1;
        return p;
    }

    private static double[] ComputeFFTMagnitude(float[] frame)
    {
        int n = frame.Length;
        int numBins = n / 2 + 1;
        var magnitudes = new double[numBins];

        // Discrete Fourier Transform (optimized slightly)
        for (int k = 0; k < numBins; k++)
        {
            double real = 0.0;
            double imag = 0.0;
            for (int idx = 0; idx < n; idx++)
            {
                double angle = -2.0 * Math.PI * k * idx / n;
                real += frame[idx] * Math.Cos(angle);
                imag += frame[idx] * Math.Sin(angle);
            }
            magnitudes[k] = Math.Sqrt(real * real + imag * imag);
        }

        return magnitudes;
    }
}
