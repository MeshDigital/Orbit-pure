using System;

namespace SLSKDONET.Models.Musical;

/// <summary>A single crowd dimension expressed as a distribution (mean + variance) over the track.</summary>
public readonly struct CrowdMetric
{
    public double Mean { get; }
    public double Variance { get; }

    public CrowdMetric(double mean, double variance)
    {
        Mean = Math.Clamp(mean, 0d, 100d);
        Variance = Math.Max(0d, variance);
    }
}

/// <summary>
/// Phase 2 (Knowledge Lifecycle): per-track crowd profile. Each dimension is a distribution over
/// the track's evolution, so flow analysis can reason about variance (a track that <em>builds</em>
/// energy vs. one that holds a flat level) rather than a single static scalar. Powers Bridge
/// Discovery and Transition Fit via vector distance (Euclidean / Cosine / Manhattan).
/// </summary>
public sealed class CrowdProfileVector
{
    public CrowdMetric Energy { get; set; }
    public CrowdMetric Aggression { get; set; }
    public CrowdMetric Emotion { get; set; }
    public CrowdMetric Darkness { get; set; }
    public CrowdMetric Groove { get; set; }
    public CrowdMetric Mainstream { get; set; }

    private double[] Means => new[]
    {
        Energy.Mean, Aggression.Mean, Emotion.Mean, Darkness.Mean, Groove.Mean, Mainstream.Mean,
    };

    public static double EuclideanDistance(CrowdProfileVector a, CrowdProfileVector b)
    {
        var am = a.Means;
        var bm = b.Means;
        double sum = 0;
        for (var i = 0; i < am.Length; i++)
        {
            var d = am[i] - bm[i];
            sum += d * d;
        }
        return Math.Sqrt(sum);
    }

    public static double ManhattanDistance(CrowdProfileVector a, CrowdProfileVector b)
    {
        var am = a.Means;
        var bm = b.Means;
        double sum = 0;
        for (var i = 0; i < am.Length; i++)
            sum += Math.Abs(am[i] - bm[i]);
        return sum;
    }

    /// <summary>1 − cosine similarity; 0 = identical direction, 1 = orthogonal.</summary>
    public static double CosineDistance(CrowdProfileVector a, CrowdProfileVector b)
    {
        var am = a.Means;
        var bm = b.Means;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < am.Length; i++)
        {
            dot += am[i] * bm[i];
            na += am[i] * am[i];
            nb += bm[i] * bm[i];
        }
        if (na == 0 || nb == 0)
            return 1.0;
        return 1.0 - (dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}
