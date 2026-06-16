using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Engine.Snapping;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Extracts transients from raw audio signals, computes MFCC windows around them,
/// and runs K-Means clustering to classify transients (Kick, Snare, Perc, FX)
/// for false-positive structural boundary elimination.
/// </summary>
public sealed class TransientClusteringEngine
{
    private readonly Random _random = new(42);

    /// <summary>
    /// Detects transient onset timestamps in a raw mono float audio signal.
    /// </summary>
    public IReadOnlyList<double> DetectTransients(float[] signal, int sampleRate, double thresholdDb = 3.0)
    {
        var transients = new List<double>();
        if (signal == null || signal.Length == 0) return transients;

        int windowSize = 512;
        int hopSize = 256;
        double minGapSeconds = 0.15; // Min 150ms between transients
        int minGapSamples = (int)(minGapSeconds * sampleRate);

        double prevEnergy = 0.0;
        int lastTransientSample = -minGapSamples;

        for (int i = 0; i < signal.Length - windowSize; i += hopSize)
        {
            // Compute RMS energy in dB
            double rms = 0.0;
            for (int j = 0; j < windowSize; j++)
            {
                rms += signal[i + j] * signal[i + j];
            }
            rms = Math.Sqrt(rms / windowSize);
            double energyDb = 20.0 * Math.Log10(Math.Max(rms, 1e-5));

            if (i == 0)
            {
                prevEnergy = energyDb;
                continue;
            }

            // Energy delta
            double delta = energyDb - prevEnergy;
            if (delta > thresholdDb && (i - lastTransientSample) > minGapSamples)
            {
                transients.Add((double)i / sampleRate);
                lastTransientSample = i;
            }

            prevEnergy = energyDb;
        }

        return transients;
    }

    /// <summary>
    /// Computes 13 MFCC coefficients for a window around a transient.
    /// </summary>
    public float[] ExtractMfccWindow(float[] signal, int sampleRate, double transientSeconds)
    {
        int transientSample = (int)(transientSeconds * sampleRate);
        int frameSize = 1024;
        
        // Center window around transient
        int start = Math.Max(0, transientSample - frameSize / 2);
        int length = Math.Min(frameSize, signal.Length - start);
        
        var frame = new float[frameSize];
        if (length > 0)
        {
            Array.Copy(signal, start, frame, 0, length);
        }

        // Apply Hamming window
        for (int i = 0; i < frameSize; i++)
        {
            double w = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (frameSize - 1));
            frame[i] *= (float)w;
        }

        // Compute Simple Mel-Filterbank Energies (Log Energies)
        // We'll use 20 mel bands and return 13 DCT coefficients
        int melBandsCount = 20;
        int dctCoefficientsCount = 13;
        
        double[] melEnergies = ComputeMelFilterbank(frame, sampleRate, melBandsCount);
        
        // Compute Discrete Cosine Transform (DCT-II)
        var mfcc = new float[dctCoefficientsCount];
        for (int i = 0; i < dctCoefficientsCount; i++)
        {
            double sum = 0.0;
            for (int j = 0; j < melBandsCount; j++)
            {
                sum += melEnergies[j] * Math.Cos(Math.PI * i * (j + 0.5) / melBandsCount);
            }
            mfcc[i] = (float)sum;
        }

        return mfcc;
    }

    /// <summary>
    /// Runs K-Means clustering (K=4) on MFCC vectors and labels them as Kick, Snare, Perc, or FX.
    /// </summary>
    public List<TransientDataPoint> ClusterTransients(IReadOnlyList<double> transientTimes, List<float[]> mfccs)
    {
        var result = new List<TransientDataPoint>();
        if (transientTimes.Count == 0 || mfccs.Count == 0) return result;

        int k = Math.Min(4, mfccs.Count);
        int dimensions = mfccs[0].Length;

        // 1. Initialize Centroids (K-Means++ style)
        var centroids = new List<float[]>();
        centroids.Add(mfccs[_random.Next(mfccs.Count)]);
        
        while (centroids.Count < k)
        {
            // Find vector furthest from existing centroids
            float maxDist = -1f;
            int bestIdx = 0;
            for (int i = 0; i < mfccs.Count; i++)
            {
                float minDistToCentroid = centroids.Select(c => EuclideanDistance(mfccs[i], c)).Min();
                if (minDistToCentroid > maxDist)
                {
                    maxDist = minDistToCentroid;
                    bestIdx = i;
                }
            }
            centroids.Add(mfccs[bestIdx]);
        }

        // 2. Iterate K-Means
        int maxIterations = 20;
        int[] assignments = new int[mfccs.Count];
        
        for (int iter = 0; iter < maxIterations; iter++)
        {
            bool changed = false;
            
            // Assign to closest centroid
            for (int i = 0; i < mfccs.Count; i++)
            {
                int bestCentroid = 0;
                float minDist = float.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    float dist = EuclideanDistance(mfccs[i], centroids[c]);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        bestCentroid = c;
                    }
                }
                if (assignments[i] != bestCentroid)
                {
                    assignments[i] = bestCentroid;
                    changed = true;
                }
            }

            if (!changed) break;

            // Recompute Centroids
            for (int c = 0; c < k; c++)
            {
                var clusterPoints = mfccs.Where((_, idx) => assignments[idx] == c).ToList();
                if (clusterPoints.Count == 0) continue;

                var newCentroid = new float[dimensions];
                for (int d = 0; d < dimensions; d++)
                {
                    newCentroid[d] = clusterPoints.Sum(p => p[d]) / clusterPoints.Count;
                }
                centroids[c] = newCentroid;
            }
        }

        // 3. Label Clusters based on average spectral characteristics
        // Kick: low frequency dominant (MFCC[1] is high, MFCC[5] is low)
        // Snare: mid-high frequency dominant
        // Perc: short/crisp, higher coefficients dominant
        // FX: slow energy spread
        string[] labels = MapClusterCentroidsToClasses(centroids);

        for (int i = 0; i < transientTimes.Count; i++)
        {
            int clusterIdx = assignments[i];
            result.Add(new TransientDataPoint
            {
                Timestamp = transientTimes[i],
                ClusterClass = labels[clusterIdx]
            });
        }

        return result;
    }

    private static float EuclideanDistance(float[] a, float[] b)
    {
        float sum = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            float diff = a[i] - b[i];
            sum += diff * diff;
        }
        return (float)Math.Sqrt(sum);
    }

    private static string[] MapClusterCentroidsToClasses(List<float[]> centroids)
    {
        var classes = new string[centroids.Count];
        var sortedIndices = Enumerable.Range(0, centroids.Count)
            .OrderBy(i => centroids[i][1]) // Order by second coefficient (often captures spectral slope/bass)
            .ToList();

        // Assign labels based on sorted order of spectral characteristics
        // Lowest frequency energy cluster -> Kick
        // Next -> Perc
        // Next -> Snare
        // Highest/Brightest -> FX
        for (int i = 0; i < sortedIndices.Count; i++)
        {
            classes[sortedIndices[i]] = i switch
            {
                0 => "Kick",
                1 => "Perc",
                2 => "Snare",
                _ => "FX"
            };
        }

        return classes;
    }

    private static double[] ComputeMelFilterbank(float[] frame, int sampleRate, int melBandsCount)
    {
        int fftSize = frame.Length;
        int numBins = fftSize / 2 + 1;
        
        // Compute FFT magnitudes
        var magnitudes = new double[numBins];
        for (int k = 0; k < numBins; k++)
        {
            double real = 0.0;
            double imag = 0.0;
            for (int n = 0; n < fftSize; n++)
            {
                double angle = -2.0 * Math.PI * k * n / fftSize;
                real += frame[n] * Math.Cos(angle);
                imag += frame[n] * Math.Sin(angle);
            }
            magnitudes[k] = Math.Sqrt(real * real + imag * imag);
        }

        // Convert Hz boundaries to Mel scale
        double minMel = HzToMel(0);
        double maxMel = HzToMel(sampleRate / 2.0);
        
        var melPoints = new double[melBandsCount + 2];
        for (int i = 0; i < melPoints.Length; i++)
        {
            melPoints[i] = minMel + i * (maxMel - minMel) / (melBandsCount + 1);
        }

        var hzPoints = melPoints.Select(MelToHz).ToArray();
        var bins = hzPoints.Select(hz => (int)Math.Floor((fftSize + 1) * hz / sampleRate)).ToArray();

        var energies = new double[melBandsCount];
        for (int m = 1; m <= melBandsCount; m++)
        {
            int fMinus = bins[m - 1];
            int fCurrent = bins[m];
            int fPlus = bins[m + 1];

            double sum = 0.0;
            for (int k = fMinus; k < fCurrent; k++)
            {
                double weight = (k - fMinus) / (double)(fCurrent - fMinus);
                sum += weight * magnitudes[k];
            }
            for (int k = fCurrent; k <= fPlus && k < numBins; k++)
            {
                double weight = (fPlus - k) / (double)(fPlus - fCurrent);
                sum += weight * magnitudes[k];
            }

            energies[m - 1] = Math.Log(Math.Max(sum, 1e-5));
        }

        return energies;
    }

    private static double HzToMel(double hz) => 1127.0 * Math.Log(1.0 + hz / 700.0);
    private static double MelToHz(double mel) => 700.0 * (Math.Exp(mel / 1127.0) - 1.0);
}
