using System;
using System.Collections.Generic;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Models.Flow;

public sealed record SuggestedFlowAffectedTransition(
    int EdgeIndex,
    string FromTrackHash,
    string ToTrackHash,
    TransitionStyle? CurrentStyle,
    TransitionStyle ProposedStyle,
    string ProposedStyleLabel,
    string ProposedStyleReason);

public sealed record SuggestedFlowStyleImpact(
    IReadOnlyDictionary<TransitionStyle, int> CurrentCounts,
    IReadOnlyDictionary<TransitionStyle, int> ProposedCounts,
    IReadOnlyDictionary<TransitionStyle, int> DeltaCounts,
    IReadOnlyList<SuggestedFlowAffectedTransition> AffectedTransitions,
    string SummaryText,
    double? AverageTransitionScore = null,
    TimeSpan? RefreshElapsed = null)
{
    public static SuggestedFlowStyleImpact Empty { get; } = new(
        new Dictionary<TransitionStyle, int>(),
        new Dictionary<TransitionStyle, int>(),
        new Dictionary<TransitionStyle, int>(),
        Array.Empty<SuggestedFlowAffectedTransition>(),
        string.Empty);
}
