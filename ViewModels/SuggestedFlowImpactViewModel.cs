using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ReactiveUI;
using SLSKDONET.Models.Flow;

namespace SLSKDONET.ViewModels;

public sealed class SuggestedFlowImpactRowViewModel
{
    public string StyleLabel { get; }
    public int CurrentCount { get; }
    public int ProposedCount { get; }
    public int DeltaCount { get; }
    public string DeltaDisplay { get; }

    public SuggestedFlowImpactRowViewModel(string styleLabel, int currentCount, int proposedCount, int deltaCount)
    {
        StyleLabel = styleLabel;
        CurrentCount = currentCount;
        ProposedCount = proposedCount;
        DeltaCount = deltaCount;
        DeltaDisplay = deltaCount > 0 ? $"+{deltaCount}" : deltaCount.ToString();
    }
}

public sealed class SuggestedFlowAffectedTransitionViewModel
{
    public string EdgeLabel { get; }
    public string StyleChangeLabel { get; }
    public string Reason { get; }

    public SuggestedFlowAffectedTransitionViewModel(SuggestedFlowAffectedTransition transition)
    {
        EdgeLabel = $"Edge {transition.EdgeIndex + 1}: {transition.FromTrackHash} -> {transition.ToTrackHash}";
        var fromLabel = transition.CurrentStyle is null
            ? "Unclassified"
            : FlowBuilderViewModel.GetTransitionStyleDisplayName(transition.CurrentStyle.Value);
        StyleChangeLabel = $"{fromLabel} -> {transition.ProposedStyleLabel}";
        Reason = transition.ProposedStyleReason;
    }
}

public sealed class SuggestedFlowImpactViewModel : ReactiveObject
{
    public string SummaryText { get; }
    public string AverageTransitionScoreDisplay { get; }
    public ObservableCollection<SuggestedFlowImpactRowViewModel> Rows { get; }
    public ObservableCollection<SuggestedFlowAffectedTransitionViewModel> AffectedTransitions { get; }

    public SuggestedFlowImpactViewModel(SuggestedFlowStyleImpact impact)
    {
        SummaryText = impact.SummaryText;
        AverageTransitionScoreDisplay = impact.AverageTransitionScore.HasValue
            ? $"Avg flow {(impact.AverageTransitionScore.Value * 100):F0}%"
            : string.Empty;

        Rows = new ObservableCollection<SuggestedFlowImpactRowViewModel>(BuildRows(impact));
        AffectedTransitions = new ObservableCollection<SuggestedFlowAffectedTransitionViewModel>(
            impact.AffectedTransitions.Select(transition => new SuggestedFlowAffectedTransitionViewModel(transition)));
    }

    private static IEnumerable<SuggestedFlowImpactRowViewModel> BuildRows(SuggestedFlowStyleImpact impact)
    {
        foreach (var style in FlowBuilderViewModel.GetOrderedTransitionStyles())
        {
            var current = impact.CurrentCounts.TryGetValue(style, out var currentValue) ? currentValue : 0;
            var proposed = impact.ProposedCounts.TryGetValue(style, out var proposedValue) ? proposedValue : 0;
            var delta = impact.DeltaCounts.TryGetValue(style, out var deltaValue) ? deltaValue : proposed - current;
            if (current == 0 && proposed == 0 && delta == 0)
                continue;

            yield return new SuggestedFlowImpactRowViewModel(
                FlowBuilderViewModel.GetTransitionStyleDisplayName(style),
                current,
                proposed,
                delta);
        }
    }
}
