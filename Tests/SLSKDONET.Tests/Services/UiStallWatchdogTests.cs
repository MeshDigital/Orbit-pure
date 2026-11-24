using Xunit;
using SLSKDONET.Services;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// #336: a late watchdog tick after sleep/resume must be classified as a resume gap, not a stall,
/// so real stalls stay easy to spot in logs.
/// </summary>
public class UiStallWatchdogTests
{
    [Theory]
    [InlineData(-100)]
    [InlineData(0)]
    [InlineData(UiStallWatchdog.ReportThresholdMs - 1)]
    public void Classify_BelowThreshold_IsBelowThreshold(long blockedMs)
    {
        Assert.Equal(UiStallWatchdog.GapKind.BelowThreshold, UiStallWatchdog.Classify(blockedMs));
    }

    [Theory]
    [InlineData(UiStallWatchdog.ReportThresholdMs)]
    [InlineData(1_000)]
    [InlineData(UiStallWatchdog.ResumeGapThresholdMs - 1)]
    public void Classify_ModerateGap_IsStall(long blockedMs)
    {
        Assert.Equal(UiStallWatchdog.GapKind.Stall, UiStallWatchdog.Classify(blockedMs));
    }

    [Theory]
    [InlineData(UiStallWatchdog.ResumeGapThresholdMs)]
    [InlineData(36_135_212)] // the ~10 h sleep/resume reported in #336
    public void Classify_LargeGap_IsResumeGap(long blockedMs)
    {
        Assert.Equal(UiStallWatchdog.GapKind.ResumeGap, UiStallWatchdog.Classify(blockedMs));
    }
}
