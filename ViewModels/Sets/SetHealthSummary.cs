using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.KnowledgeLifecycle;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels.Sets;

/// <summary>One Set Health number, or "—" with what's missing.</summary>
public sealed record HealthMetric(string Label, double? Value, string Missing)
{
    public bool HasValue => Value is not null;
    public double MeterValue => Value ?? 0;
    public string ValueText => Value is double v ? v.ToString("0", CultureInfo.InvariantCulture) : "—";
    /// <summary>Tooltip: the value's meaning, or what to do to get one.</summary>
    public string Hint => Value is null ? Missing : $"{Label}: {ValueText} / 100";
}

/// <summary>Something worth fixing in the set; <see cref="SlotId"/> jumps to the slot when known.</summary>
public sealed record HealthIssue(string Text, Guid? SlotId);

/// <summary>
/// Set Health for the Sets page strip (rebuild plan P2.7). Uses <see cref="SetHealthService"/>'s
/// formulas but only on real inputs: no neutral 50s for missing analysis, roles or energy targets.
/// A metric without inputs is null (shown as "—") with a hint saying what's missing.
/// </summary>
public sealed class SetHealthSummary
{
    public HealthMetric Overall { get; }
    public HealthMetric Transitions { get; }
    public HealthMetric PeakPlacement { get; }
    public HealthMetric Recovery { get; }
    public IReadOnlyList<HealthMetric> Metrics => new[] { Overall, Transitions, PeakPlacement, Recovery };
    public IReadOnlyList<HealthIssue> Issues { get; }
    public bool HasIssues => Issues.Count > 0;
    public string IssuesText => CountFormat.Of(Issues.Count, "issue");

    /// <summary>Energy target per slot (null where unset), for the arc.</summary>
    public IReadOnlyList<double?> EnergyTargets { get; }
    /// <summary>Analysed track energy per slot (null where unanalysed), for the arc.</summary>
    public IReadOnlyList<double?> TrackEnergies { get; }

    public static SetHealthSummary Empty { get; } = new(Array.Empty<SlotCardViewModel>(), Array.Empty<TransitionChipViewModel>());

    public SetHealthSummary(IReadOnlyList<SlotCardViewModel> slots, IReadOnlyList<TransitionChipViewModel> transitions)
    {
        static bool Analysed(SlotCardViewModel s) => s.Slot.Bpm is > 0 && s.Slot.CamelotKey is not null;

        var issues = new List<HealthIssue>();

        // Transitions: only pairs where both sides are analysed carry a real score.
        var scored = transitions.Where(t => Analysed(t.From) && Analysed(t.To)).ToList();
        Transitions = new HealthMetric("Transitions",
            scored.Count == 0 ? null : SetHealthService.TransitionQuality(scored.Select(t => t.Score).ToList()),
            slots.Count < 2 ? "Add at least two tracks" : "Analyse the tracks to score their transitions");

        var roles = slots.Select(s => s.Slot.Role).ToList();
        var hasPeak = roles.Contains(SetSlotRole.Peak);
        PeakPlacement = new HealthMetric("Peak placement",
            hasPeak ? SetHealthService.PeakPlacement(roles) : null,
            "Mark at least one slot as Peak");
        Recovery = new HealthMetric("Recovery",
            hasPeak && roles.Count >= 2 ? SetHealthService.CrowdRecovery(roles) : null,
            "Mark Peak slots, then follow them with a Reset or Atmosphere slot");

        var parts = new[] { (Transitions.Value, 0.6), (PeakPlacement.Value, 0.2), (Recovery.Value, 0.2) }
            .Where(p => p.Value is not null)
            .ToList();
        Overall = new HealthMetric("Overall",
            parts.Count == 0 ? null : parts.Sum(p => p.Value!.Value * p.Item2) / parts.Sum(p => p.Item2),
            "Needs analysed tracks or slot roles");

        // Issues, in lane order.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            if (!slot.Slot.IsInLibrary)
                issues.Add(new HealthIssue($"{slot.Number} {slot.Title}: file is not in the library", slot.SlotId));
            else if (!Analysed(slot))
                issues.Add(new HealthIssue($"{slot.Number} {slot.Title}: not analysed (no BPM/key)", slot.SlotId));
            if (!seen.Add(slot.Slot.TrackUniqueHash))
                issues.Add(new HealthIssue($"{slot.Number} {slot.Title}: already earlier in the set", slot.SlotId));
        }
        foreach (var transition in scored.Where(t => t.HasWarnings))
            issues.Add(new HealthIssue(
                $"{transition.From.Number} → {transition.To.Number}: {string.Join(", ", transition.Warnings)}",
                transition.To.SlotId));
        Issues = issues;

        EnergyTargets = slots.Select(s => s.Slot.EnergyTarget).ToList();
        TrackEnergies = slots.Select(s => s.Slot.Energy).ToList();
    }
}
