using SLSKDONET.Data.Entities;
using SLSKDONET.Services.KnowledgeLifecycle;
using Xunit;

namespace SLSKDONET.Tests.Services.KnowledgeLifecycle;

public class SetHealthServiceTests
{
    private static EventProfileEntity EventWithCurve(string curve)
        => new() { TargetEnergyCurveJson = curve };

    [Fact]
    public void TransitionQuality_Empty_IsZero()
        => Assert.Equal(0.0, SetHealthService.TransitionQuality(System.Array.Empty<double>()), 6);

    [Fact]
    public void TransitionQuality_IsMean()
        => Assert.Equal(85.0, SetHealthService.TransitionQuality(new[] { 80.0, 90.0 }), 6);

    [Fact]
    public void EnergyFlow_NoProfile_IsNeutral()
        => Assert.Equal(50.0, SetHealthService.EnergyFlow(new[] { 30.0, 60.0, 90.0 }, null), 6);

    [Fact]
    public void EnergyFlow_MatchesCurve_ScoresHigh()
    {
        // Curve and targets identical → MSE 0 → 100.
        var profile = EventWithCurve("[30,60,90]");
        var score = SetHealthService.EnergyFlow(new[] { 30.0, 60.0, 90.0 }, profile);
        Assert.Equal(100.0, score, 6);
    }

    [Fact]
    public void EnergyFlow_Mismatch_ScoresLow()
    {
        var profile = EventWithCurve("[90,90,90]");
        var score = SetHealthService.EnergyFlow(new[] { 0.0, 0.0, 0.0 }, profile);
        Assert.True(score < 20.0, $"expected < 20, got {score}");
    }

    [Theory]
    [InlineData(SetSlotRole.Opening, SetSlotRole.Builder, SetSlotRole.Peak, SetSlotRole.Reset, SetSlotRole.Closer, 100.0)]
    [InlineData(SetSlotRole.Peak, SetSlotRole.Builder, SetSlotRole.Closer, SetSlotRole.Reset, SetSlotRole.Opening, 0.0)]
    public void PeakPlacement_RewardsMiddleWindow(
        SetSlotRole a, SetSlotRole b, SetSlotRole c, SetSlotRole d, SetSlotRole e, double expected)
        => Assert.Equal(expected, SetHealthService.PeakPlacement(new[] { a, b, c, d, e }), 6);

    [Fact]
    public void PeakPlacement_NoPeaks_IsNeutral()
        => Assert.Equal(50.0, SetHealthService.PeakPlacement(new[] { SetSlotRole.Opening, SetSlotRole.Closer }), 6);

    [Fact]
    public void CrowdRecovery_PeakFollowedByReset_ScoresHigh()
        => Assert.Equal(100.0, SetHealthService.CrowdRecovery(new[]
            { SetSlotRole.Opening, SetSlotRole.Peak, SetSlotRole.Reset, SetSlotRole.Closer }), 6);

    [Fact]
    public void CrowdRecovery_PeakWithoutRecovery_ScoresZero()
        => Assert.Equal(0.0, SetHealthService.CrowdRecovery(new[]
            { SetSlotRole.Peak, SetSlotRole.Builder, SetSlotRole.Peak }), 6);

    [Fact]
    public void Compute_BlendsAndComputesRisk()
    {
        var report = SetHealthService.Compute(
            new[] { 80.0, 90.0 },
            new[] { 30.0, 60.0, 90.0 },
            new[] { SetSlotRole.Opening, SetSlotRole.Peak, SetSlotRole.Reset },
            EventWithCurve("[30,60,90]"),
            preparationCompleteness: 0.5);

        Assert.Equal(50.0, report.PerformanceRiskScore, 6);
        Assert.InRange(report.TechnicalScore, 0.0, 100.0);
        Assert.InRange(report.NarrativeScore, 0.0, 100.0);
        Assert.InRange(report.OverallScore, 0.0, 100.0);
    }
}
