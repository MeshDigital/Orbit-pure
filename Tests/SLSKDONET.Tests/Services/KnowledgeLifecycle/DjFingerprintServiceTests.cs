using SLSKDONET.Data.Entities;
using SLSKDONET.Services.KnowledgeLifecycle;
using Xunit;

namespace SLSKDONET.Tests.Services.KnowledgeLifecycle;

public class DjFingerprintServiceTests
{
    [Fact]
    public void ComputeDjFit_NoPreference_IsNeutral()
        => Assert.Equal(0.5, DjFingerprintService.ComputeDjFit(KnowledgeTransitionType.Blend, null, null), 6);

    [Fact]
    public void ComputeDjFit_PreferredType_ScoresHigh()
        => Assert.Equal(0.9, DjFingerprintService.ComputeDjFit(KnowledgeTransitionType.Blend, "{\"Blend\":0.9}", null), 6);

    [Fact]
    public void ComputeDjFit_AvoidedType_IsPenalised()
    {
        var fit = DjFingerprintService.ComputeDjFit(
            KnowledgeTransitionType.DropSwap, null, "[\"DropSwap\"]");
        Assert.Equal(0.5 * DjFingerprintService.AvoidancePenaltyFactor, fit, 6);
    }

    [Fact]
    public void UpdateTransitionPreferences_ReinforcesObserved()
    {
        var fingerprint = new DjFingerprintEntity();
        DjFingerprintService.UpdateTransitionPreferences(
            fingerprint, new[] { KnowledgeTransitionType.Blend, KnowledgeTransitionType.Blend });

        var blend = DjFingerprintService.ComputeDjFit(KnowledgeTransitionType.Blend, fingerprint.PreferredTransitionTypesJson, null);
        // 0.5 -> 0.6 -> 0.68 (two EMA steps of α=0.2 toward 1.0)
        Assert.Equal(0.68, blend, 6);
    }

    [Fact]
    public void ComputeDjFit_MalformedJson_FallsBackToNeutral()
        => Assert.Equal(0.5, DjFingerprintService.ComputeDjFit(KnowledgeTransitionType.Blend, "not-json", "not-json"), 6);
}
