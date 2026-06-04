using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services.Similarity;

/// <summary>
/// Pure A10.7 classifier that labels a transition without changing any A10 score.
/// Deterministic, stateless, and safe to call from UI-facing pairwise surfaces.
/// </summary>
public sealed class TransitionStyleClassifier
{
    public TransitionStyleResult Classify(
        TrackFingerprint left,
        TrackFingerprint right,
        TrackSimilarityResult similarity,
        IReadOnlyList<SectionFeatureVector>? leftSections = null,
        IReadOnlyList<SectionFeatureVector>? rightSections = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(similarity);

        var energyDelta = right.Energy.GlobalEnergy - left.Energy.GlobalEnergy;
        var absoluteEnergyDelta = Math.Abs(energyDelta);
        var harmonic = similarity.VectorScores.Harmonic;
        var overall = similarity.FinalSimilarity;
        var transitionAlignment = ResolveTransitionAlignment(leftSections, rightSections);

        var outgoingDrop = GetBestSection(leftSections, PhraseType.Drop);
        var outgoingBreakdown = GetBestSection(leftSections, PhraseType.Breakdown);
        var outgoingOutro = GetBestSection(leftSections, PhraseType.Outro);
        var incomingIntro = GetBestSection(rightSections, PhraseType.Intro);
        var incomingDrop = GetBestSection(rightSections, PhraseType.Drop);

        var incomingDropImpact = incomingDrop is not null && incomingIntro is not null
            ? incomingDrop.EnergyLevel >= incomingIntro.EnergyLevel + 0.16f
            : incomingDrop is not null;
        var outgoingBreakdownReset = outgoingBreakdown is not null && incomingIntro is not null
            ? outgoingBreakdown.EnergyLevel <= left.Energy.GlobalEnergy - 0.08f && incomingIntro.EnergyLevel <= 0.45f
            : false;
        var sectionMismatch = transitionAlignment <= 0.60;

        if (harmonic <= 0.35 && overall <= 0.45 && absoluteEnergyDelta >= 0.22)
        {
            return Build(
                TransitionStyle.RiskyClash,
                "Risky Clash",
                "Low harmonic fit, weak similarity, and a sharp energy jump make this transition fragile.");
        }

        if (outgoingBreakdownReset && energyDelta <= 0.05 && harmonic >= 0.55 && overall <= 0.68)
        {
            return Build(
                TransitionStyle.BreakdownReset,
                "Breakdown Reset",
                "The mix eases into a lower-energy entry after a breakdown, creating a deliberate reset.");
        }

        if ((outgoingDrop is not null || outgoingOutro is not null) && incomingDropImpact && energyDelta >= 0.16 && similarity.SegmentScores.Drop >= 0.70)
        {
            return Build(
                TransitionStyle.DropSwap,
                "Drop Swap",
                "The outgoing track hands off with impact and the incoming track answers with a strong drop.");
        }

        if (overall >= 0.78 && harmonic >= 0.72 && absoluteEnergyDelta <= 0.12)
        {
            return Build(
                TransitionStyle.SmoothBlend,
                "Smooth Blend",
                "High similarity, strong harmonic fit, and a stable energy profile support a seamless handoff.");
        }

        if (energyDelta >= 0.14 && harmonic >= 0.55 && overall >= 0.52)
        {
            return Build(
                TransitionStyle.EnergyLift,
                "Energy Lift",
                "The incoming track lifts energy while staying harmonically steady enough to feel intentional.");
        }

        if (harmonic >= 0.40 && harmonic < 0.70 && overall >= 0.50 && absoluteEnergyDelta <= 0.12 && sectionMismatch)
        {
            return Build(
                TransitionStyle.TensionBridge,
                "Tension Bridge",
                "Moderate fit with a sectional mismatch creates controlled tension rather than a clean melt.");
        }

        if (overall >= 0.65 && harmonic >= 0.58)
        {
            return Build(
                TransitionStyle.SmoothBlend,
                "Smooth Blend",
                "The pair stays close enough across the core A10 signals to read as a stable blend.");
        }

        if (energyDelta >= 0.12 && harmonic >= 0.48)
        {
            return Build(
                TransitionStyle.EnergyLift,
                "Energy Lift",
                "The transition primarily works by pushing the set upward in energy rather than melting invisibly.");
        }

        if (harmonic < 0.45 || overall < 0.48)
        {
            return Build(
                TransitionStyle.RiskyClash,
                "Risky Clash",
                "The pair lacks enough shared fit to be trusted as a stable transition without intervention.");
        }

        return Build(
            TransitionStyle.TensionBridge,
            "Tension Bridge",
            "Moderate fit with a sectional mismatch creates controlled tension rather than a clean melt.");
    }

    private static TransitionStyleResult Build(TransitionStyle style, string label, string reason)
        => new()
        {
            Style = style,
            Label = label,
            Reason = reason,
        };

    private static SectionFeatureVector? GetBestSection(IReadOnlyList<SectionFeatureVector>? sections, PhraseType type)
        => sections?
            .Where(section => section.SectionType == type)
            .OrderByDescending(section => section.Confidence)
            .FirstOrDefault();

    private static double ResolveTransitionAlignment(
        IReadOnlyList<SectionFeatureVector>? leftSections,
        IReadOnlyList<SectionFeatureVector>? rightSections)
    {
        var outro = GetBestSection(leftSections, PhraseType.Outro);
        var intro = GetBestSection(rightSections, PhraseType.Intro);
        if (outro is null || intro is null)
            return 0.5;

        return outro.TransitionScore(intro);
    }
}
