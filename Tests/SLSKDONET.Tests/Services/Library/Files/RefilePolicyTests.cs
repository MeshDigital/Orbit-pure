using System;
using SLSKDONET.Services.Library.Files;
using Xunit;

namespace SLSKDONET.Tests.Services.Library.Files;

/// <summary>LF3 (#345): the re-file-after-analysis policy.</summary>
public class RefilePolicyTests
{
    [Theory]
    [InlineData("{artist} - {title} - {key}")]
    [InlineData("{artist} - {bpm} BPM")]
    [InlineData("{bpm_bracket}/{artist} - {title}")]
    public void TemplateUsesAnalysisTokens_TrueForKeyBpmBracket(string format)
    {
        Assert.True(RefilePolicy.TemplateUsesAnalysisTokens(format));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{artist} - {title}")]
    [InlineData("{artist}/{album}/{title}")]
    public void TemplateUsesAnalysisTokens_FalseWhenNotAnalysisDependent(string? format)
    {
        Assert.False(RefilePolicy.TemplateUsesAnalysisTokens(format));
    }

    [Fact]
    public void ShouldRefileAfterAnalysis_TrueWhenUnlockedAndAnalysisDependent()
    {
        Assert.True(RefilePolicy.ShouldRefileAfterAnalysis("{artist} - {key}", pathLockedAt: null));
    }

    [Fact]
    public void ShouldRefileAfterAnalysis_FalseWhenLocked()
    {
        Assert.False(RefilePolicy.ShouldRefileAfterAnalysis("{artist} - {key}", pathLockedAt: DateTime.UtcNow));
    }

    [Fact]
    public void ShouldRefileAfterAnalysis_FalseWhenTemplateIsStatic()
    {
        Assert.False(RefilePolicy.ShouldRefileAfterAnalysis("{artist} - {title}", pathLockedAt: null));
    }
}
