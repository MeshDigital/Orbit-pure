using System;
using SLSKDONET.Models.Musical;
using Xunit;

namespace SLSKDONET.Tests.Models.Musical;

public class CrowdProfileVectorTests
{
    private static CrowdProfileVector Vector(
        double energy, double aggression, double emotion, double darkness, double groove, double mainstream)
        => new()
        {
            Energy = new CrowdMetric(energy, 0),
            Aggression = new CrowdMetric(aggression, 0),
            Emotion = new CrowdMetric(emotion, 0),
            Darkness = new CrowdMetric(darkness, 0),
            Groove = new CrowdMetric(groove, 0),
            Mainstream = new CrowdMetric(mainstream, 0),
        };

    private static readonly CrowdProfileVector Zero = Vector(0, 0, 0, 0, 0, 0);

    // ── Euclidean ──────────────────────────────────────────────────────

    [Fact]
    public void Euclidean_Identical_IsZero()
        => Assert.Equal(0.0, CrowdProfileVector.EuclideanDistance(Vector(50, 50, 50, 50, 50, 50), Vector(50, 50, 50, 50, 50, 50)), 9);

    [Fact]
    public void Euclidean_ThreeFourFive()
        => Assert.Equal(5.0, CrowdProfileVector.EuclideanDistance(Zero, Vector(3, 4, 0, 0, 0, 0)), 9);

    [Fact]
    public void Euclidean_ZeroToNonZero_IsItsNorm()
        => Assert.Equal(Math.Sqrt(24.0), CrowdProfileVector.EuclideanDistance(Zero, Vector(2, 2, 2, 2, 2, 2)), 9);

    // ── Manhattan ──────────────────────────────────────────────────────

    [Fact]
    public void Manhattan_Identical_IsZero()
        => Assert.Equal(0.0, CrowdProfileVector.ManhattanDistance(Vector(10, 10, 10, 10, 10, 10), Vector(10, 10, 10, 10, 10, 10)), 9);

    [Fact]
    public void Manhattan_IsSumOfAbsoluteDifferences()
        => Assert.Equal(21.0, CrowdProfileVector.ManhattanDistance(Zero, Vector(3, 4, 1, 2, 5, 6)), 9);

    // ── Cosine ─────────────────────────────────────────────────────────

    [Fact]
    public void Cosine_Identical_IsZero()
        => AssertCosine(0.0, CrowdProfileVector.CosineDistance(Vector(10, 20, 30, 40, 50, 60), Vector(10, 20, 30, 40, 50, 60)));

    [Fact]
    public void Cosine_ScaledVectors_IsZero()
        => AssertCosine(0.0, CrowdProfileVector.CosineDistance(Vector(5, 10, 15, 20, 25, 30), Vector(10, 20, 30, 40, 50, 60)));

    [Fact]
    public void Cosine_ZeroVector_DoesNotDivideByZero()
    {
        // Degenerate magnitude → distance defined as 1 (orthogonal), not NaN/Infinity.
        AssertCosine(1.0, CrowdProfileVector.CosineDistance(Zero, Vector(10, 20, 30, 40, 50, 60)));
        AssertCosine(1.0, CrowdProfileVector.CosineDistance(Zero, Zero));
    }

    // ── CrowdMetric clamping ───────────────────────────────────────────

    [Fact]
    public void CrowdMetric_ClampsMeanAndVariance()
    {
        var high = new CrowdMetric(150.0, -5.0);
        Assert.Equal(100.0, high.Mean, 9);
        Assert.Equal(0.0, high.Variance, 9);

        var low = new CrowdMetric(-10.0, 12.0);
        Assert.Equal(0.0, low.Mean, 9);
        Assert.Equal(12.0, low.Variance, 9);
    }

    private static void AssertCosine(double expected, double actual, double tolerance = 1e-9)
        => Assert.True(Math.Abs(expected - actual) < tolerance, $"expected {expected}, got {actual}");
}
