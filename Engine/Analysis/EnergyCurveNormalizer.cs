using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Normalizes audio energy curves using a 3-tier topology: Local 32-beat blocks,
/// Global track ceiling, and Genre-profile dynamic balancing.
/// </summary>
public sealed class EnergyCurveNormalizer
{
    /// <summary>
    /// Performs 3-tier energy curve normalization.
    /// </summary>
    /// <param name="rawEnergy">Raw, windowed energy values.</param>
    /// <param name="bpm">Track tempo in BPM.</param>
    /// <param name="windowDurationSeconds">The duration of each energy window index in seconds.</param>
    /// <param name="genre">The genre of the track (influences profile compression).</param>
    /// <returns>Normalized energy curve array with values scaled in [0.0, 1.0].</returns>
    public float[] NormalizeEnergyCurve(
        float[] rawEnergy, 
        double bpm, 
        double windowDurationSeconds, 
        string genre = "General")
    {
        if (rawEnergy == null || rawEnergy.Length == 0) return Array.Empty<float>();

        int length = rawEnergy.Length;
        var normalized = new float[length];

        // 1. Global track ceiling normalization
        float globalMax = rawEnergy.Max();
        if (globalMax <= 0f) globalMax = 1f;

        for (int i = 0; i < length; i++)
        {
            normalized[i] = rawEnergy[i] / globalMax;
        }

        // 2. Local 32-beat phrase normalization
        if (bpm > 0)
        {
            double beatDuration = 60.0 / bpm;
            double localBlockDuration = beatDuration * 32.0; // 32-beat window size
            int localBlockSamples = (int)(localBlockDuration / windowDurationSeconds);
            localBlockSamples = Math.Max(4, localBlockSamples);

            for (int i = 0; i < length; i++)
            {
                int start = Math.Max(0, i - localBlockSamples / 2);
                int end = Math.Min(length - 1, i + localBlockSamples / 2);
                
                float localMax = 0f;
                for (int j = start; j <= end; j++)
                {
                    if (normalized[j] > localMax) localMax = normalized[j];
                }

                if (localMax > 1e-4f)
                {
                    // Blend local max to avoid scaling silence to 1.0
                    float localRatio = normalized[i] / localMax;
                    normalized[i] = (normalized[i] * 0.4f) + (localRatio * 0.6f * normalized[i]);
                }
            }
        }

        // 3. Genre-profile normalization
        ApplyGenreProfileBalancing(normalized, genre);

        // Final clamp
        for (int i = 0; i < length; i++)
        {
            normalized[i] = Math.Clamp(normalized[i], 0f, 1f);
        }

        return normalized;
    }

    private static void ApplyGenreProfileBalancing(float[] energy, string genre)
    {
        genre = genre.Trim().ToLowerInvariant();

        // High-energy, highly compressed genres (Techno, House, Hard Dance)
        if (genre.Contains("techno") || genre.Contains("house") || genre.Contains("dance") || genre.Contains("trance"))
        {
            // Boost mid-tier values and expand peak-to-breakdown range
            for (int i = 0; i < energy.Length; i++)
            {
                if (energy[i] > 0.45f)
                {
                    // Smooth compression lift
                    energy[i] = (float)Math.Pow(energy[i], 0.82);
                }
                else
                {
                    // Slightly depress lower sections to emphasize drop boundaries
                    energy[i] = (float)Math.Pow(energy[i], 1.2);
                }
            }
        }
        // Ambient / Acoustic / Chillout genres (wider dynamic profile)
        else if (genre.Contains("ambient") || genre.Contains("acoustic") || genre.Contains("classical") || genre.Contains("chill"))
        {
            // Retain full dynamic contrasts
            for (int i = 0; i < energy.Length; i++)
            {
                energy[i] = (float)Math.Pow(energy[i], 1.15);
            }
        }
    }
}
