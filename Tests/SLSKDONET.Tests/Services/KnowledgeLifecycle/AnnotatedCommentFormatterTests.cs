using SLSKDONET.Data.Entities;
using SLSKDONET.Services.KnowledgeLifecycle;
using Xunit;

namespace SLSKDONET.Tests.Services.KnowledgeLifecycle;

public class AnnotatedCommentFormatterTests
{
    [Fact]
    public void FormatComment_WithHealth_ProducesDenseFormat()
    {
        var health = new SetHealthReport(OverallScore: 88, NarrativeScore: 85, TechnicalScore: 90, PerformanceRiskScore: 12);

        var comment = AnnotatedCommentFormatter.FormatComment(health, KnowledgeConfidenceLevel.Confirmed, freshness: 95.0);

        Assert.Equal("[N:85 T:90 R:12] C:High F:0.95", comment);
    }

    [Fact]
    public void FormatComment_WithoutHealth_OmitsHealthPart()
    {
        var comment = AnnotatedCommentFormatter.FormatComment(null, KnowledgeConfidenceLevel.Likely, freshness: 80.0);

        Assert.Equal("C:Med F:0.80", comment);
    }

    [Theory]
    [InlineData(KnowledgeConfidenceLevel.Confirmed, "High")]
    [InlineData(KnowledgeConfidenceLevel.Likely, "Med")]
    [InlineData(KnowledgeConfidenceLevel.Experimental, "Low")]
    [InlineData(KnowledgeConfidenceLevel.Unknown, "Unk")]
    public void ConfidenceTier_Maps(KnowledgeConfidenceLevel level, string expected)
        => Assert.Equal(expected, AnnotatedCommentFormatter.ConfidenceTier(level));
}
