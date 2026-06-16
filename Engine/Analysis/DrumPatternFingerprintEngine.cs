using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Separates percussive elements using HPSS (Harmonic-Percussive Source Separation)
/// and analyzes running 4-bar signatures to find breakdown and drop switch points.
/// </summary>
public sealed class DrumPatternFingerprintEngine
{
    /// <summary>
    /// Runs a simplified HPSS on the input signal and outputs a percussive energy curve.
    /// </summary>
    public float[] IsolatePercussiveSignal(float[] signal, int sampleRate, double frameSizeSecs = 0.04, double hopSizeSecs = 0.02)
    {
        if (signal == null || signal.Length == 0) return Array.Empty<float>();

        int frameSize = (int)(frameSizeSecs * sampleRate);
        int hopSize = (int)(hopSizeSecs * sampleRate);

        frameSize = Math.Max(256, NextPowerOfTwo(frameSize));
        hopSize = Math.Max(128, hopSize);

        int numBins = frameSize / 2 + 1;
        var spectrogram = new List<double[]>();

        // 1. Generate Spectrogram Magnitude Matrix
        for (int i = 0; i < signal.Length - frameSize; i += hopSize)
        {
            var frame = new float[frameSize];
            Array.Copy(signal, i, frame, 0, frameSize);
            spectrogram.Add(ComputeSpectrogramFrame(frame));
        }

        if (spectrogram.Count < 5) return new float[spectrogram.Count];

        int numFrames = spectrogram.Count;

        // 2. Perform Median Filtering (HPSS)
        // We will separate the percussive elements (median filter vertically along frequency)
        // and harmonic elements (median filter horizontally along time).
        var percussiveMatrix = new double[numFrames][];
        for (int i = 0; i < numFrames; i++)
        {
            percussiveMatrix[i] = new double[numBins];
        }

        int filterLenHarmonic = 17; // time window
        int filterLenPercussive = 17; // freq window

        // Median filter percussive component
        for (int f = 0; f < numFrames; f++)
        {
            for (int k = 0; k < numBins; k++)
            {
                // Percussive filter runs vertically (along bins k)
                int startBin = Math.Max(0, k - filterLenPercussive / 2);
                int endBin = Math.Min(numBins - 1, k + filterLenPercussive / 2);
                
                var values = new List<double>();
                for (int b = startBin; b <= endBin; b++)
                {
                    values.Add(spectrogram[f][b]);
                }
                
                double percMedian = Median(values);

                // Harmonic filter runs horizontally (along frames f)
                int startFrame = Math.Max(0, f - filterLenHarmonic / 2);
                int endFrame = Math.Min(numFrames - 1, f + filterLenHarmonic / 2);
                
                var frameVals = new List<double>();
                for (int fr = startFrame; fr <= endFrame; fr++)
                {
                    frameVals.Add(spectrogram[fr][k]);
                }

                double harmMedian = Median(frameVals);

                // Binary or soft mask assignment
                if (percMedian > harmMedian)
                {
                    percussiveMatrix[f][k] = spectrogram[f][k];
                }
                else
                {
                    percussiveMatrix[f][k] = 0.0;
                }
            }
        }

        // 3. Compress percussive matrix to single energy curve
        var percussiveEnergy = new float[numFrames];
        for (int f = 0; f < numFrames; f++)
        {
            double sum = 0.0;
            for (int k = 0; k < numBins; k++)
            {
                sum += percussiveMatrix[f][k];
            }
            percussiveEnergy[f] = (float)sum;
        }

        return percussiveEnergy;
    }

    /// <summary>
    /// Checks 4-bar signatures and detects drum pattern mismatches.
    /// </summary>
    public List<double> DetectPatternMismatches(
        float[] percussiveEnergy, 
        double frameDurationSeconds, 
        double bpm, 
        double threshold = 0.45)
    {
        var mismatchTimes = new List<double>();
        if (percussiveEnergy.Length == 0 || bpm <= 0) return mismatchTimes;

        double beatDuration = 60.0 / bpm;
        // 4 bars in 4/4 is 16 beats
        double signatureDuration = beatDuration * 16;
        int framesPerSignature = (int)(signatureDuration / frameDurationSeconds);

        if (framesPerSignature <= 0) return mismatchTimes;

        var signatures = new List<float[]>();
        var timeStamps = new List<double>();

        for (int i = 0; i < percussiveEnergy.Length - framesPerSignature; i += framesPerSignature)
        {
            var sig = new float[framesPerSignature];
            Array.Copy(percussiveEnergy, i, sig, 0, framesPerSignature);

            // Normalize signature
            float max = sig.Max();
            if (max > 1e-4f)
            {
                for (int j = 0; j < sig.Length; j++) sig[j] /= max;
            }

            signatures.Add(sig);
            timeStamps.Add(i * frameDurationSeconds);
        }

        for (int i = 1; i < signatures.Count; i++)
        {
            double diff = ComputeSignatureDifference(signatures[i - 1], signatures[i]);
            if (diff > threshold)
            {
                mismatchTimes.Add(timeStamps[i]);
            }
        }

        return mismatchTimes;
    }

    private static double ComputeSignatureDifference(float[] a, float[] b)
    {
        int length = Math.Min(a.Length, b.Length);
        if (length == 0) return 0.0;

        double dist = 0.0;
        for (int i = 0; i < length; i++)
        {
            double diff = a[i] - b[i];
            dist += diff * diff;
        }

        return Math.Sqrt(dist) / Math.Sqrt(length);
    }

    private static double Median(List<double> xs)
    {
        if (xs.Count == 0) return 0.0;
        xs.Sort();
        int mid = xs.Count / 2;
        return xs.Count % 2 != 0 ? xs[mid] : (xs[mid - 1] + xs[mid]) / 2.0;
    }

    private static int NextPowerOfTwo(int val)
    {
        int p = 1;
        while (p < val) p <<= 1;
        return p;
    }

    private static double[] ComputeSpectrogramFrame(float[] frame)
    {
        int n = frame.Length;
        int numBins = n / 2 + 1;
        var magnitudes = new double[numBins];

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
