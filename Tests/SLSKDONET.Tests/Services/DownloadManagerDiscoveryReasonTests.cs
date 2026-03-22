using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class DownloadManagerDiscoveryReasonTests
{
    [Fact]
    public void ResolveDiscoveryReason_ShouldPreferMatchReason()
    {
        var result = DownloadManager.ResolveDiscoveryReason(
            sourceProvenance: null,
            matchReason: "strong fit • trusted peer • score 92",
            scoreBreakdown: "Blend: Match=80.0, Fit=90.0, Final=92.0");

        Assert.Equal("strong fit • trusted peer • score 92", result);
    }

    [Fact]
    public void ResolveDiscoveryReason_ShouldFallbackToScoreBreakdown()
    {
        var result = DownloadManager.ResolveDiscoveryReason(
            sourceProvenance: null,
            matchReason: null,
            scoreBreakdown: "Blend: Match=72.0, Fit=68.0, Final=70.4");

        Assert.Equal("Blend: Match=72.0, Fit=68.0, Final=70.4", result);
    }

    [Fact]
    public void ResolveDiscoveryReason_ShouldPrefixShieldSanitizedReason()
    {
        var result = DownloadManager.ResolveDiscoveryReason(
            sourceProvenance: "ShieldSanitized",
            matchReason: "strong fit • trusted peer • score 92",
            scoreBreakdown: null);

        Assert.Equal("🛡 Shield sanitized · strong fit • trusted peer • score 92", result);
    }

    [Fact]
    public void ResolveDiscoveryReason_ShouldReturnShieldSanitizedDefault_WhenNoReasonAvailable()
    {
        var result = DownloadManager.ResolveDiscoveryReason(
            sourceProvenance: "ShieldSanitized",
            matchReason: null,
            scoreBreakdown: null);

        Assert.Equal("🛡 Shield sanitized search", result);
    }
}
