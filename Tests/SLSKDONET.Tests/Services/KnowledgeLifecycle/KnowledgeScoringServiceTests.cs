using System;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.KnowledgeLifecycle;
using Xunit;

namespace SLSKDONET.Tests.Services.KnowledgeLifecycle;

public class KnowledgeScoringServiceTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TransitionEvidenceEntity Evidence(double score, double confidence, DateTime createdAt)
        => new() { Score = score, Confidence = confidence, CreatedAt = createdAt };

    // ── Freshness decay ────────────────────────────────────────────────

    [Fact]
    public void Freshness_Now_IsFull()
        => Assert.Equal(100.0, KnowledgeScoringService.ComputeFreshness(Now, Now, false), 6);

    [Fact]
    public void Freshness_OneHalfLife_IsHalf()
    {
        var twoYearsAgo = Now.AddYears(-(int)KnowledgeScoringService.HalfLifeYears);
        Assert.Equal(50.0, KnowledgeScoringService.ComputeFreshness(twoYearsAgo, Now, false), 0.1);
    }

    [Fact]
    public void Freshness_SevenYears_IsAboutEightPointEight()
    {
        var sevenYearsAgo = Now.AddYears(-7);
        var freshness = KnowledgeScoringService.ComputeFreshness(sevenYearsAgo, Now, false);
        Assert.InRange(freshness, 8.7, 9.0);
    }

    [Fact]
    public void Freshness_Ancient_DecaysTowardZero()
    {
        var ancient = Now.AddYears(-40);
        var freshness = KnowledgeScoringService.ComputeFreshness(ancient, Now, false);
        Assert.True(freshness < 0.01, $"expected near-zero freshness, got {freshness}");
        Assert.True(freshness >= 0.0);
    }

    [Fact]
    public void Freshness_Classic_IsDecayExempt()
        => Assert.Equal(100.0, KnowledgeScoringService.ComputeFreshness(Now.AddYears(-40), Now, true), 6);

    [Fact]
    public void Freshness_FutureTimestamp_ClampsToFull()
        => Assert.Equal(100.0, KnowledgeScoringService.ComputeFreshness(Now.AddYears(1), Now, false), 6);

    // ── Compatibility aggregation ──────────────────────────────────────

    [Fact]
    public void Compatibility_NoEvidence_IsZero()
        => Assert.Equal(0.0, KnowledgeScoringService.ComputeCompatibilityScore(Array.Empty<TransitionEvidenceEntity>(), Now), 6);

    [Fact]
    public void Compatibility_SingleEvidence_IsItsScore()
        => Assert.Equal(77.0, KnowledgeScoringService.ComputeCompatibilityScore(new[] { Evidence(77.0, 0.9, Now) }, Now), 6);

    [Fact]
    public void Compatibility_EqualConfidence_IsMean()
        => Assert.Equal(75.0, KnowledgeScoringService.ComputeCompatibilityScore(
            new[] { Evidence(60.0, 1.0, Now), Evidence(90.0, 1.0, Now) }, Now), 6);

    [Fact]
    public void Compatibility_FreshEvidenceDominatesStale()
    {
        var stale = Evidence(100.0, 1.0, Now.AddYears(-8));
        var fresh = Evidence(0.0, 1.0, Now);
        var score = KnowledgeScoringService.ComputeCompatibilityScore(new[] { stale, fresh }, Now);
        Assert.True(score < 50.0, $"fresh evidence should dominate, got {score}");
    }

    // ── Confidence tiers & normalisation ───────────────────────────────

    [Theory]
    [InlineData(0.0, KnowledgeConfidenceLevel.Unknown)]
    [InlineData(0.29, KnowledgeConfidenceLevel.Unknown)]
    [InlineData(0.3, KnowledgeConfidenceLevel.Experimental)]
    [InlineData(0.99, KnowledgeConfidenceLevel.Experimental)]
    [InlineData(1.0, KnowledgeConfidenceLevel.Likely)]
    [InlineData(1.99, KnowledgeConfidenceLevel.Likely)]
    [InlineData(2.0, KnowledgeConfidenceLevel.Confirmed)]
    [InlineData(5.0, KnowledgeConfidenceLevel.Confirmed)]
    public void ToConfidenceLevel_HonoursThresholds(double points, KnowledgeConfidenceLevel expected)
        => Assert.Equal(expected, KnowledgeScoringService.ToConfidenceLevel(points));

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 0.5)]
    [InlineData(2.0, 1.0)]
    [InlineData(5.0, 1.0)]
    public void ComputeConfidenceScore_NormalisesAndClamps(double points, double expected)
        => Assert.Equal(expected, KnowledgeScoringService.ComputeConfidenceScore(points), 6);

    // ── Full materialization ───────────────────────────────────────────

    [Fact]
    public void Recompute_MaterializesEveryDerivedField()
    {
        var knowledge = new TransitionKnowledgeEntity
        {
            IsClassic = true,
            LastUsedAtUtc = Now.AddYears(-1),
        };
        var evidence = new[]
        {
            Evidence(80.0, 1.0, Now),
            Evidence(100.0, 1.0, Now),
        };

        KnowledgeScoringService.Recompute(knowledge, evidence, Now);

        Assert.Equal(90.0, knowledge.CompatibilityScore, 6);
        Assert.Equal(2, knowledge.EvidenceCount);
        Assert.Equal(KnowledgeConfidenceLevel.Confirmed, knowledge.ConfidenceLevel);
        Assert.Equal(1.0, knowledge.ConfidenceScore, 6);
        Assert.Equal(100.0, knowledge.Freshness, 6); // classic → exempt
        Assert.True(knowledge.Verified);
        Assert.Equal(Now, knowledge.LastCalculatedUtc);
    }
}
