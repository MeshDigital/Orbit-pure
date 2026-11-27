using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Views;
using SLSKDONET.Utils;

namespace SLSKDONET.ViewModels.Workstation;


/// <summary>
/// Smart snap, phrase regions, flow transition overlays, harmonic/energy compatibility, and preset scoring (#307).
/// </summary>
public sealed partial class WorkstationViewModel
{
    private void ApplyTimelineViewportToDecks()
    {
        foreach (var deck in Decks)
        {
            deck.UpdateWaveformViewport(TimelineWindowSeconds, TimelineOffsetSeconds);
        }

        this.RaisePropertyChanged(nameof(SnapGuideCanvasLeft));
    }

    public void ApplySmartSnapForDeckDrop(WorkstationDeckViewModel targetDeck)
    {
        if (!IsSnapEnabled || targetDeck == null || !targetDeck.IsLoaded)
        {
            HideSnapGuide();
            return;
        }

        var referenceDeck = Decks
            .Where(d => !ReferenceEquals(d, targetDeck) && d.IsLoaded)
            .OrderByDescending(d => d.IsFocusedDeck)
            .ThenByDescending(d => d.Deck.PositionSeconds)
            .FirstOrDefault();

        if (referenceDeck == null)
        {
            HideSnapGuide();
            return;
        }

        var targetCue = SelectAnchorCueForIncomingTrack(targetDeck);
        var referenceCue = SelectAnchorCueForReferenceTrack(referenceDeck);
        if (targetCue == null || referenceCue == null)
        {
            HideSnapGuide();
            return;
        }

        var desiredStart = QuantizeIfEnabled(targetCue.Timestamp, targetDeck.DisplayBpm);
        var maxSeek = Math.Max(0.0, targetDeck.Deck.DurationSeconds - 0.05);
        var clampedStart = Math.Clamp(desiredStart, 0.0, maxSeek);

        targetDeck.Deck.SeekCommand.Execute(clampedStart).Subscribe();

        SnapGuideTimeSeconds = referenceCue.Timestamp;
        SnapGuideLabel = WorkstationDeckViewModel.BuildTransitionStatus(
            targetDeck.DeckLabel,
            targetDeck.TrackKey,
            targetDeck.DisplayBpm,
            targetDeck.CueEditor.Cues,
            referenceDeck.DeckLabel,
            referenceDeck.TrackKey,
            referenceDeck.DisplayBpm,
            referenceDeck.CueEditor.Cues);
        IsSnapGuideVisible = true;
    }

    private static OrbitCue? SelectAnchorCueForIncomingTrack(WorkstationDeckViewModel deck)
    {
        var cues = deck.CueEditor.Cues;
        if (cues.Count == 0)
        {
            return null;
        }

        return FindFirstByRoles(cues, CueRole.Intro, CueRole.PhraseStart, CueRole.Build, CueRole.Drop)
               ?? cues.OrderBy(c => c.Timestamp).FirstOrDefault();
    }

    private static OrbitCue? SelectAnchorCueForReferenceTrack(WorkstationDeckViewModel deck)
    {
        var cues = deck.CueEditor.Cues;
        if (cues.Count == 0)
        {
            return null;
        }

        var preferred = FindLatestByRoles(cues, CueRole.Outro, CueRole.Breakdown, CueRole.PhraseStart);
        if (preferred != null)
        {
            return preferred;
        }

        var current = deck.Deck.PositionSeconds;
        return cues
            .OrderBy(c => Math.Abs(c.Timestamp - current))
            .FirstOrDefault();
    }

    private static OrbitCue? FindFirstByRoles(IEnumerable<OrbitCue> cues, params CueRole[] roles)
    {
        foreach (var role in roles)
        {
            var match = cues
                .Where(c => c.Role == role)
                .OrderBy(c => c.Timestamp)
                .FirstOrDefault();

            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static OrbitCue? FindLatestByRoles(IEnumerable<OrbitCue> cues, params CueRole[] roles)
    {
        foreach (var role in roles)
        {
            var match = cues
                .Where(c => c.Role == role)
                .OrderByDescending(c => c.Timestamp)
                .FirstOrDefault();

            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static IReadOnlyList<double> BuildPhraseSnapCandidatesSeconds(
        WorkstationDeckViewModel source,
        WorkstationDeckViewModel target,
        double rangeStart,
        double rangeEnd,
        double sourceAnchor,
        double targetAnchor)
    {
        var min = Math.Max(0.0, rangeStart - 16.0);
        var max = rangeEnd + 16.0;

        var phraseRoles = new[]
        {
            CueRole.PhraseStart,
            CueRole.Intro,
            CueRole.Build,
            CueRole.Drop,
            CueRole.Breakdown,
            CueRole.Outro,
        };

        var candidates = source.CueEditor.Cues
            .Concat(target.CueEditor.Cues)
            .Where(c => phraseRoles.Contains(c.Role))
            .Select(c => c.Timestamp)
            .Where(t => t >= min && t <= max)
            .Concat(new[] { sourceAnchor, targetAnchor })
            .Where(t => t >= 0)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        if (candidates.Count == 0)
        {
            candidates.Add(Math.Max(0.0, sourceAnchor));
        }

        return candidates;
    }

    private static IReadOnlyList<FlowPhraseRegion> BuildMergedPhraseRegions(
        string transitionKey,
        WorkstationDeckViewModel source,
        WorkstationDeckViewModel target,
        double rangeStart,
        double rangeEnd,
        double sourceAnchor,
        double targetAnchor,
        IReadOnlyDictionary<string, FlowPhraseRegion>? phraseRegionOverrides)
    {
        var inferred = BuildInferredPhraseRegionsFromCues(transitionKey, source, target, rangeStart, rangeEnd, sourceAnchor, targetAnchor);
        inferred = NormalizePhraseRegions(transitionKey, inferred, rangeStart, rangeEnd);
        inferred = MergeAdjacentOrOverlappingInferredRegions(transitionKey, inferred, rangeStart, rangeEnd);

        FlowPhraseRegion? explicitRegion = null;
        if (phraseRegionOverrides != null && phraseRegionOverrides.TryGetValue(transitionKey, out var overrideRegion))
        {
            explicitRegion = overrideRegion;
        }

        var withExplicit = ApplyExplicitPhraseRegionOverrides(transitionKey, inferred, explicitRegion, rangeStart, rangeEnd);
        return ComputeProvenanceAndConfidence(transitionKey, source, target, withExplicit, rangeStart, rangeEnd);
    }

    private static IReadOnlyList<FlowPhraseRegion> BuildInferredPhraseRegionsFromCues(
        string transitionKey,
        WorkstationDeckViewModel source,
        WorkstationDeckViewModel target,
        double rangeStart,
        double rangeEnd,
        double sourceAnchor,
        double targetAnchor)
    {
        var cues = source.CueEditor.Cues.Concat(target.CueEditor.Cues).ToList();
        var inferred = new List<FlowPhraseRegion>
        {
            // Seed deterministic inferred baseline so explicit edits can shape regions consistently.
            new(transitionKey, rangeStart, rangeEnd, false, 0.55, null, FlowPhraseRegionProvenance.Inferred)
        };

        var cueCandidates = cues
            .Where(c => c.Role is CueRole.PhraseStart or CueRole.Intro or CueRole.Build or CueRole.Drop or CueRole.Breakdown or CueRole.Outro)
            .Select(c => c.Timestamp)
            .Concat(new[] { sourceAnchor, targetAnchor })
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        foreach (var cue in cueCandidates)
        {
            var left = cue - 1.8;
            var right = cue + 1.8;
            inferred.Add(new FlowPhraseRegion(transitionKey, left, right, false, 0.6, null, FlowPhraseRegionProvenance.Inferred));
        }

        return inferred;
    }

    private static IReadOnlyList<FlowPhraseRegion> NormalizePhraseRegions(
        string transitionKey,
        IReadOnlyList<FlowPhraseRegion> regions,
        double rangeStart,
        double rangeEnd)
    {
        var min = Math.Max(0.0, Math.Min(rangeStart, rangeEnd));
        var max = Math.Max(min, Math.Max(rangeStart, rangeEnd));

        return (regions ?? Array.Empty<FlowPhraseRegion>())
            .Select(r =>
            {
                var start = Math.Clamp(r.StartSeconds, min, max);
                var end = Math.Clamp(Math.Max(start, r.EndSeconds), min, max);
                return new FlowPhraseRegion(
                    transitionKey,
                    start,
                    end,
                    r.IsExplicit,
                    r.Confidence,
                    r.SourceCueIds,
                    r.Provenance);
            })
            .Where(r => r.EndSeconds - r.StartSeconds >= 0.05)
            .OrderBy(r => r.StartSeconds)
            .ToList();
    }

    private static IReadOnlyList<FlowPhraseRegion> MergeAdjacentOrOverlappingInferredRegions(
        string transitionKey,
        IReadOnlyList<FlowPhraseRegion> regions,
        double rangeStart,
        double rangeEnd)
    {
        const double adjacencyToleranceSeconds = 0.1;
        var inferred = NormalizePhraseRegions(transitionKey, regions, rangeStart, rangeEnd)
            .Where(r => !r.IsExplicit)
            .OrderBy(r => r.StartSeconds)
            .ToList();

        if (inferred.Count <= 1)
        {
            return inferred;
        }

        var merged = new List<FlowPhraseRegion>();
        var current = inferred[0];
        for (var i = 1; i < inferred.Count; i++)
        {
            var next = inferred[i];
            var shouldMerge = next.StartSeconds <= current.EndSeconds + adjacencyToleranceSeconds;
            if (!shouldMerge)
            {
                merged.Add(current);
                current = next;
                continue;
            }

            var cueIds = current.SourceCueIds.Concat(next.SourceCueIds).Distinct().ToList();
            current = new FlowPhraseRegion(
                transitionKey,
                Math.Min(current.StartSeconds, next.StartSeconds),
                Math.Max(current.EndSeconds, next.EndSeconds),
                false,
                (current.Confidence + next.Confidence) / 2.0,
                cueIds,
                FlowPhraseRegionProvenance.Inferred);
        }

        merged.Add(current);
        return merged;
    }

    private static IReadOnlyList<FlowPhraseRegion> ApplyExplicitPhraseRegionOverrides(
        string transitionKey,
        IReadOnlyList<FlowPhraseRegion> inferredRegions,
        FlowPhraseRegion? explicitRegion,
        double rangeStart,
        double rangeEnd)
    {
        if (explicitRegion == null)
        {
            return inferredRegions;
        }

        var normalizedExplicit = NormalizePhraseRegions(transitionKey, new[]
        {
            new FlowPhraseRegion(
                transitionKey,
                explicitRegion.StartSeconds,
                explicitRegion.EndSeconds,
                true,
                1.0,
                explicitRegion.SourceCueIds,
                FlowPhraseRegionProvenance.ExplicitUser)
        }, rangeStart, rangeEnd).FirstOrDefault();

        if (normalizedExplicit == null)
        {
            return inferredRegions;
        }

        var shaped = new List<FlowPhraseRegion>();
        foreach (var inferred in inferredRegions.Where(r => !r.IsExplicit))
        {
            var overlaps = normalizedExplicit.StartSeconds < inferred.EndSeconds && normalizedExplicit.EndSeconds > inferred.StartSeconds;
            if (!overlaps)
            {
                shaped.Add(inferred);
                continue;
            }

            if (normalizedExplicit.StartSeconds > inferred.StartSeconds + 0.05)
            {
                shaped.Add(new FlowPhraseRegion(
                    transitionKey,
                    inferred.StartSeconds,
                    Math.Min(normalizedExplicit.StartSeconds, inferred.EndSeconds),
                    false,
                    inferred.Confidence,
                    inferred.SourceCueIds,
                    FlowPhraseRegionProvenance.Mixed));
            }

            if (normalizedExplicit.EndSeconds < inferred.EndSeconds - 0.05)
            {
                shaped.Add(new FlowPhraseRegion(
                    transitionKey,
                    Math.Max(normalizedExplicit.EndSeconds, inferred.StartSeconds),
                    inferred.EndSeconds,
                    false,
                    inferred.Confidence,
                    inferred.SourceCueIds,
                    FlowPhraseRegionProvenance.Mixed));
            }
        }

        shaped.Add(normalizedExplicit);
        return NormalizePhraseRegions(transitionKey, shaped, rangeStart, rangeEnd);
    }

    private static IReadOnlyList<FlowPhraseRegion> ComputeProvenanceAndConfidence(
        string transitionKey,
        WorkstationDeckViewModel source,
        WorkstationDeckViewModel target,
        IReadOnlyList<FlowPhraseRegion> regions,
        double rangeStart,
        double rangeEnd)
    {
        var allCues = source.CueEditor.Cues.Concat(target.CueEditor.Cues).ToList();
        var normalized = NormalizePhraseRegions(transitionKey, regions, rangeStart, rangeEnd);

        return normalized
            .Select(r =>
            {
                if (r.IsExplicit)
                {
                    return new FlowPhraseRegion(
                        transitionKey,
                        r.StartSeconds,
                        r.EndSeconds,
                        true,
                        1.0,
                        r.SourceCueIds,
                        FlowPhraseRegionProvenance.ExplicitUser);
                }

                var provenance = r.Provenance == FlowPhraseRegionProvenance.Mixed
                    ? FlowPhraseRegionProvenance.Mixed
                    : FlowPhraseRegionProvenance.Inferred;
                var confidence = ComputeInferredRegionConfidence(r.StartSeconds, r.EndSeconds, allCues, provenance);
                var cueIds = allCues
                    .Where(c => c.Timestamp >= r.StartSeconds && c.Timestamp <= r.EndSeconds)
                    .OrderBy(c => c.Timestamp)
                    .Take(4)
                    .Select(c => $"{c.Role}@{c.Timestamp.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}")
                    .ToList();

                return new FlowPhraseRegion(
                    transitionKey,
                    r.StartSeconds,
                    r.EndSeconds,
                    false,
                    confidence,
                    cueIds,
                    provenance);
            })
            .OrderBy(r => r.StartSeconds)
            .ToList();
    }

    private static double ComputeInferredRegionConfidence(
        double startSeconds,
        double endSeconds,
        IReadOnlyList<OrbitCue> cues,
        FlowPhraseRegionProvenance provenance)
    {
        var cuesInSpan = (cues ?? Array.Empty<OrbitCue>())
            .Where(c => c.Timestamp >= startSeconds && c.Timestamp <= endSeconds)
            .ToList();

        if (cuesInSpan.Count == 0)
        {
            return provenance == FlowPhraseRegionProvenance.Mixed ? 0.55 : 0.45;
        }

        var average = cuesInSpan.Average(c => Math.Clamp(c.Confidence, 0.0, 1.0));
        var adjusted = provenance == FlowPhraseRegionProvenance.Mixed ? average + 0.05 : average;
        return Math.Clamp(adjusted, 0.35, 0.95);
    }

    private double QuantizeIfEnabled(double seconds, double bpm)
    {
        if (!IsQuantizeEnabled || bpm <= 0)
        {
            return seconds;
        }

        var beatSeconds = 60.0 / bpm;
        if (beatSeconds <= 0)
        {
            return seconds;
        }

        return Math.Round(seconds / beatSeconds) * beatSeconds;
    }

    private void RefreshDeckTransitionGuidance()
    {
        foreach (var deck in Decks)
        {
            var referenceDeck = Decks
                .Where(d => !ReferenceEquals(d, deck) && d.IsLoaded)
                .OrderByDescending(d => d.IsFocusedDeck)
                .FirstOrDefault();

            if (referenceDeck == null || !deck.IsLoaded)
            {
                deck.UpdateTransitionStatus("Load another deck for live transition guidance");
                deck.UpdateHarmonicGuidance(
                    WorkstationDeckViewModel.BuildHarmonicSuggestionText(deck.TrackKey, null, deck.Deck.SemitoneShift));
                continue;
            }

            deck.UpdateTransitionStatus(
                WorkstationDeckViewModel.BuildTransitionStatus(
                    deck.DeckLabel,
                    deck.TrackKey,
                    deck.DisplayBpm,
                    deck.CueEditor.Cues,
                    referenceDeck.DeckLabel,
                    referenceDeck.TrackKey,
                    referenceDeck.DisplayBpm,
                    referenceDeck.CueEditor.Cues));
            deck.UpdateHarmonicGuidance(
                WorkstationDeckViewModel.BuildHarmonicSuggestionText(
                    deck.TrackKey,
                    referenceDeck.TrackKey,
                    deck.Deck.SemitoneShift));
        }

        RaiseHeaderProperties();
        this.RaisePropertyChanged(nameof(FlowTransitions));
        this.RaisePropertyChanged(nameof(HasFlowTransitions));
        this.RaisePropertyChanged(nameof(IsFlowOverlayVisible));
        this.RaisePropertyChanged(nameof(FlowOverlayHint));
    }

    public static IReadOnlyList<FlowTransitionOverlayViewModel> BuildFlowTransitions(
        IEnumerable<WorkstationDeckViewModel> decks,
        double timelineOffsetSeconds,
        double timelineWindowSeconds)
    {
        return BuildFlowTransitions(decks, timelineOffsetSeconds, timelineWindowSeconds, selectedTransitionKey: null, lengthOverrides: null, presetOverrides: null, phraseMarkerOverrides: null, phraseMarkerExplicitKeys: null, phraseRegionOverrides: null);
    }

    public static IReadOnlyList<FlowTransitionOverlayViewModel> BuildFlowTransitions(
        IEnumerable<WorkstationDeckViewModel> decks,
        double timelineOffsetSeconds,
        double timelineWindowSeconds,
        string? selectedTransitionKey,
        IReadOnlyDictionary<string, double>? lengthOverrides,
        IReadOnlyDictionary<string, string>? presetOverrides = null,
        IReadOnlyDictionary<string, double>? phraseMarkerOverrides = null,
        IReadOnlyCollection<string>? phraseMarkerExplicitKeys = null,
        IReadOnlyDictionary<string, FlowPhraseRegion>? phraseRegionOverrides = null)
    {
        const double timelineCanvasWidth = 700.0;
        var overlays = new List<FlowTransitionOverlayViewModel>();

        if (timelineWindowSeconds <= 0)
        {
            return overlays;
        }

        var loadedDecks = (decks ?? Enumerable.Empty<WorkstationDeckViewModel>())
            .Where(deck => deck.IsLoaded)
            .OrderBy(deck => deck.DeckLabel)
            .ToList();

        if (loadedDecks.Count < 2)
        {
            return overlays;
        }

        var presetCatalog = BuildFlowTransitionPresetCatalog();

        for (var i = 0; i < loadedDecks.Count - 1; i++)
        {
            var source = loadedDecks[i];
            var target = loadedDecks[i + 1];

            var sourceCue = SelectAnchorCueForReferenceTrack(source);
            var targetCue = SelectAnchorCueForIncomingTrack(target);

            var transitionKey = BuildTransitionKey(source, target);
            var sourceAnchor = sourceCue?.Timestamp ?? source.Deck.PositionSeconds;
            if (phraseMarkerOverrides != null && phraseMarkerOverrides.TryGetValue(transitionKey, out var phraseMarkerOverride))
            {
                sourceAnchor = Math.Max(0.0, phraseMarkerOverride);
            }

            var targetAnchor = targetCue?.Timestamp ?? target.Deck.PositionSeconds;
            var rangeStart = Math.Max(0.0, Math.Min(sourceAnchor, targetAnchor) - 2.0);
            var baselineEnd = Math.Max(rangeStart + 6.0, Math.Max(sourceAnchor, targetAnchor) + 2.0);
            var baselineLength = Math.Max(0.1, baselineEnd - rangeStart);
            var transitionLength = baselineLength;
            var overriddenLength = 0d;
            var hasOverride = lengthOverrides != null && lengthOverrides.TryGetValue(transitionKey, out overriddenLength);
            if (hasOverride)
            {
                transitionLength = Math.Max(0.1, overriddenLength);
            }

            var rangeEnd = rangeStart + transitionLength;

            var left = ToCanvasX(rangeStart, timelineOffsetSeconds, timelineWindowSeconds, timelineCanvasWidth);
            var right = ToCanvasX(rangeEnd, timelineOffsetSeconds, timelineWindowSeconds, timelineCanvasWidth);
            var width = Math.Max(26.0, right - left);

            var phraseGuideLeft = ToCanvasX(sourceAnchor, timelineOffsetSeconds, timelineWindowSeconds, timelineCanvasWidth);
            var beatGuideLeft = ToCanvasX(targetAnchor, timelineOffsetSeconds, timelineWindowSeconds, timelineCanvasWidth);
            var phraseSnapCandidates = BuildPhraseSnapCandidatesSeconds(source, target, rangeStart, rangeEnd, sourceAnchor, targetAnchor);
            var isPhraseMarkerExplicit = phraseMarkerExplicitKeys?.Contains(transitionKey) == true;
            var phraseRegions = BuildMergedPhraseRegions(
                transitionKey,
                source,
                target,
                rangeStart,
                rangeEnd,
                sourceAnchor,
                targetAnchor,
                phraseRegionOverrides);
            var beatDiff = Math.Abs(source.DisplayBpm - target.DisplayBpm);
            var beatAligned = source.DisplayBpm > 0 && target.DisplayBpm > 0 && beatDiff <= 1.0;
            var phraseAligned = sourceCue != null && targetCue != null;
            var sourceEnergy = source.Energy ?? EstimateNormalizedEnergyFromBpm(source.DisplayBpm);
            var targetEnergy = target.Energy ?? EstimateNormalizedEnergyFromBpm(target.DisplayBpm);
            var harmonicScore = ComputeFlowHarmonicCompatibility(source.TrackKey, target.TrackKey, semitoneShift: 0);
            var energyScore = ComputeFlowEnergyCompatibility(sourceEnergy, targetEnergy, transitionLength);
            var combinedScore = ComputeCombinedFlowCompatibilityScore(harmonicScore.Score, energyScore.Score);
            var suggestedPresets = RankFlowPresetSuggestions(
                presetCatalog,
                harmonicScore,
                energyScore,
                phraseAligned,
                Math.Abs(sourceEnergy - targetEnergy),
                topN: 3);
            var warningFlags = BuildFlowWarningFlags(harmonicScore, energyScore, phraseAligned);
            var phraseMarkerConfidenceScore = ComputePhraseMarkerConfidenceScore(sourceCue, targetCue, phraseAligned, isPhraseMarkerExplicit);
            var phraseMarkerConfidenceLabel = BuildPhraseMarkerConfidenceLabel(phraseMarkerConfidenceScore, isPhraseMarkerExplicit);
            var phraseMarkerTooltip = BuildPhraseMarkerTooltip(phraseMarkerConfidenceLabel, isPhraseMarkerExplicit, phraseSnapCandidates.Count);

            var resolvedPresetId = presetOverrides != null && presetOverrides.TryGetValue(transitionKey, out var pid) ? pid : null;

            overlays.Add(new FlowTransitionOverlayViewModel(
                transitionKey,
                BuildTransitionLabel(source, target),
                BuildFlowCompatibilityLabel(source.TrackKey, target.TrackKey, beatDiff),
                rangeStart,
                rangeEnd,
                left,
                width,
                sourceAnchor,
                targetAnchor,
                phraseGuideLeft,
                beatGuideLeft,
                phraseSnapCandidates,
                isPhraseMarkerExplicit,
                phraseMarkerConfidenceScore,
                phraseMarkerConfidenceLabel,
                phraseMarkerTooltip,
                phraseAligned,
                beatAligned,
                transitionLength,
                string.Equals(transitionKey, selectedTransitionKey, StringComparison.Ordinal),
                hasOverride,
                resolvedPresetId,
                suggestedPresets,
                harmonicScore.Score,
                harmonicScore.Label,
                energyScore.Score,
                energyScore.Label,
                combinedScore,
                phraseRegions,
                warningFlags));
        }

        return overlays;
    }

    private static string BuildTransitionLabel(WorkstationDeckViewModel source, WorkstationDeckViewModel target)
    {
        var sourceLabel = string.IsNullOrWhiteSpace(source.TrackTitle) ? source.DeckLabel : source.TrackTitle;
        var targetLabel = string.IsNullOrWhiteSpace(target.TrackTitle) ? target.DeckLabel : target.TrackTitle;
        return $"{sourceLabel} → {targetLabel}";
    }

    private static string BuildTransitionKey(WorkstationDeckViewModel source, WorkstationDeckViewModel target)
    {
        var sourceId = string.IsNullOrWhiteSpace(source.TrackHash) ? source.DeckLabel : source.TrackHash;
        var targetId = string.IsNullOrWhiteSpace(target.TrackHash) ? target.DeckLabel : target.TrackHash;
        return $"{sourceId}->{targetId}";
    }

    private static string BuildFlowCompatibilityLabel(string? sourceKey, string? targetKey, double bpmDifference)
    {
        var keyDistance = CamelotDistance(sourceKey, targetKey);
        var harmonicState = keyDistance switch
        {
            <= 1d => "harmonic lock",
            <= 2d => "safe blend",
            <= 4d => "stretch blend",
            _ => "risky blend"
        };

        var bpmText = bpmDifference <= 0.05
            ? "±0.0 BPM"
            : $"±{bpmDifference.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} BPM";
        return $"{harmonicState} • {bpmText}";
    }

    private string ResolvePresetName(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return "Custom";
        }

        var preset = _flowPresetCatalog.FirstOrDefault(p => string.Equals(p.PresetId, presetId, StringComparison.OrdinalIgnoreCase));
        return preset?.DisplayName ?? "Custom";
    }

    private string ResolveCurveLabel(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return "Curve: default";
        }

        var preset = _flowPresetCatalog.FirstOrDefault(p => string.Equals(p.PresetId, presetId, StringComparison.OrdinalIgnoreCase));
        return preset == null ? "Curve: default" : $"Curve: {CurveTypeDisplayName(preset.CurveType)}";
    }

    private string ResolveBandStrategyLabel(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            return "Bands: default";
        }

        var preset = _flowPresetCatalog.FirstOrDefault(p => string.Equals(p.PresetId, presetId, StringComparison.OrdinalIgnoreCase));
        if (preset == null)
        {
            return "Bands: default";
        }

        return $"Bands: Low={BandStrategyDisplayName(preset.LowBandStrategy)} Mid={BandStrategyDisplayName(preset.MidBandStrategy)} High={BandStrategyDisplayName(preset.HighBandStrategy)}";
    }

    private static string CurveTypeDisplayName(FlowTransitionCurveType curveType) => curveType switch
    {
        FlowTransitionCurveType.EqualPower  => "Equal Power",
        FlowTransitionCurveType.BassSwap    => "Bass Swap",
        FlowTransitionCurveType.FullSpectrum => "Full Spectrum",
        FlowTransitionCurveType.HardCut     => "Hard Cut",
        FlowTransitionCurveType.Custom      => "Custom",
        _                                   => "Default"
    };

    private static string BandStrategyDisplayName(FlowFrequencyBandStrategy strategy) => strategy switch
    {
        FlowFrequencyBandStrategy.Blend  => "Blend",
        FlowFrequencyBandStrategy.Swap   => "Swap",
        FlowFrequencyBandStrategy.Hold   => "Hold",
        FlowFrequencyBandStrategy.Cut    => "Cut",
        FlowFrequencyBandStrategy.Custom => "Custom",
        _                               => "Default"
    };

    private string BuildSuggestedPresetLabel(IReadOnlyList<string> suggestedPresetIds)
    {
        if (suggestedPresetIds == null || suggestedPresetIds.Count == 0)
        {
            return "No strong preset suggestions yet";
        }

        var names = suggestedPresetIds
            .Take(3)
            .Select(ResolvePresetName)
            .ToList();
        return string.Join(" • ", names);
    }

    private static string BuildWarningLabel(IReadOnlyList<string> warningFlags)
    {
        if (warningFlags == null || warningFlags.Count == 0)
        {
            return "Warnings: none";
        }

        return $"Warnings: {string.Join(" • ", warningFlags)}";
    }

    private static double ComputePhraseMarkerConfidenceScore(OrbitCue? sourceCue, OrbitCue? targetCue, bool phraseAligned, bool isExplicit)
    {
        var sourceConfidence = sourceCue?.Confidence ?? 0.55;
        var targetConfidence = targetCue?.Confidence ?? 0.55;
        var baseConfidence = (sourceConfidence + targetConfidence) / 2.0;

        if (phraseAligned)
        {
            baseConfidence += 0.1;
        }

        if (isExplicit)
        {
            baseConfidence += 0.1;
        }

        return Math.Clamp(Math.Round(baseConfidence * 100.0, 1), 0.0, 100.0);
    }

    private static string BuildPhraseMarkerConfidenceLabel(double confidenceScore, bool isExplicit)
    {
        var state = isExplicit ? "explicit" : "inferred";
        return $"{state} {confidenceScore.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}%";
    }

    private static string BuildPhraseMarkerTooltip(string confidenceLabel, bool isExplicit, int candidateCount)
    {
        var mode = isExplicit ? "Explicit phrase marker" : "Inferred phrase marker";
        var action = isExplicit ? "Double-click to remove" : "Double-click to pin";
        return $"{mode} · {confidenceLabel} · {candidateCount} candidate{(candidateCount == 1 ? string.Empty : "s")} · {action}";
    }

    private static double EstimateNormalizedEnergyFromBpm(double bpm)
    {
        if (bpm <= 0)
        {
            return 0.5;
        }

        return Math.Clamp((bpm - 80.0) / 80.0, 0.0, 1.0);
    }

    public static IReadOnlyList<FlowTransitionPreset> BuildFlowTransitionPresetCatalog()
    {
        return new List<FlowTransitionPreset>
        {
            new(
                PresetId: "crossfade",
                DisplayName: "Crossfade",
                Description: "Balanced overlap and gain curve.",
                CurveType: FlowTransitionCurveType.EqualPower,
                DefaultLengthSeconds: 8.0,
                LowBandStrategy: FlowFrequencyBandStrategy.Blend,
                MidBandStrategy: FlowFrequencyBandStrategy.Blend,
                HighBandStrategy: FlowFrequencyBandStrategy.Blend,
                MinCompatibilityScore: 20.0,
                RequiresPhraseAlignment: false,
                MinSuggestedEnergyDelta: 0.00,
                MaxSuggestedEnergyDelta: 0.40),
            new(
                PresetId: "bass-swap",
                DisplayName: "Bass Swap",
                Description: "Stage low-band handoff while mids/highs overlap.",
                CurveType: FlowTransitionCurveType.BassSwap,
                DefaultLengthSeconds: 10.0,
                LowBandStrategy: FlowFrequencyBandStrategy.Swap,
                MidBandStrategy: FlowFrequencyBandStrategy.Blend,
                HighBandStrategy: FlowFrequencyBandStrategy.Blend,
                MinCompatibilityScore: 55.0,
                RequiresPhraseAlignment: true,
                MinSuggestedEnergyDelta: 0.05,
                MaxSuggestedEnergyDelta: 0.35),
            new(
                PresetId: "full",
                DisplayName: "Full",
                Description: "Full-spectrum overlap for energetic lifts.",
                CurveType: FlowTransitionCurveType.FullSpectrum,
                DefaultLengthSeconds: 12.0,
                LowBandStrategy: FlowFrequencyBandStrategy.Blend,
                MidBandStrategy: FlowFrequencyBandStrategy.Blend,
                HighBandStrategy: FlowFrequencyBandStrategy.Blend,
                MinCompatibilityScore: 70.0,
                RequiresPhraseAlignment: true,
                MinSuggestedEnergyDelta: 0.00,
                MaxSuggestedEnergyDelta: 0.22),
            new(
                PresetId: "none",
                DisplayName: "None",
                Description: "Hard handoff with minimal overlap.",
                CurveType: FlowTransitionCurveType.HardCut,
                DefaultLengthSeconds: 3.0,
                LowBandStrategy: FlowFrequencyBandStrategy.Cut,
                MidBandStrategy: FlowFrequencyBandStrategy.Cut,
                HighBandStrategy: FlowFrequencyBandStrategy.Cut,
                MinCompatibilityScore: 0.0,
                RequiresPhraseAlignment: false,
                MinSuggestedEnergyDelta: 0.00,
                MaxSuggestedEnergyDelta: 1.00),
            new(
                PresetId: "custom",
                DisplayName: "Custom",
                Description: "User-defined transition behavior.",
                CurveType: FlowTransitionCurveType.Custom,
                DefaultLengthSeconds: 8.0,
                LowBandStrategy: FlowFrequencyBandStrategy.Custom,
                MidBandStrategy: FlowFrequencyBandStrategy.Custom,
                HighBandStrategy: FlowFrequencyBandStrategy.Custom,
                MinCompatibilityScore: 0.0,
                RequiresPhraseAlignment: false,
                MinSuggestedEnergyDelta: 0.00,
                MaxSuggestedEnergyDelta: 1.00)
        };
    }

    /// <summary>
    /// Delegates to the shared, tested harmony formula (<see cref="Similarity.TrackMatchScorer.ComputeHarmony"/>)
    /// instead of an independently hand-rolled Camelot-distance bucket — this Flow overlay previously
    /// computed its own harmony score in parallel with the documented library-wide compatibility system
    /// (see DOCS/TRACK_COMPATIBILITY_SCORING.md), diverging from it for no real reason.
    /// Label thresholds are DJ-convention: same/adjacent key (Camelot distance 0-1) is a "lock",
    /// which the shared formula's 1-(distance/6) scale places at ~83-100, not the >=90 the old
    /// independent bucket formula used — retuned here rather than carried over unchanged.
    /// </summary>
    public static FlowCompatibilityScore ComputeFlowHarmonicCompatibility(string? sourceCamelotKey, string? targetCamelotKey, int semitoneShift)
    {
        var axis = Services.Playlist.TrackPairCompatibilityScorer.ComputeHarmonicCompatibility(sourceCamelotKey, targetCamelotKey, semitoneShift);
        return new FlowCompatibilityScore(axis.Score, axis.Label);
    }

    public static FlowCompatibilityScore ComputeFlowEnergyCompatibility(double? sourceEnergy, double? targetEnergy, double transitionLengthSeconds)
    {
        var axis = Services.Playlist.TrackPairCompatibilityScorer.ComputeEnergyCompatibility(sourceEnergy, targetEnergy, transitionLengthSeconds);
        return new FlowCompatibilityScore(axis.Score, axis.Label);
    }

    public static double ComputeCombinedFlowCompatibilityScore(double harmonicScore, double energyScore)
    {
        return Math.Round(Math.Clamp((harmonicScore * 0.6) + (energyScore * 0.4), 0.0, 100.0), 1);
    }

    public static IReadOnlyList<string> RankFlowPresetSuggestions(
        IReadOnlyList<FlowTransitionPreset> presets,
        FlowCompatibilityScore harmonicScore,
        FlowCompatibilityScore energyScore,
        bool phraseAligned,
        double energyDelta,
        int topN)
    {
        if (presets == null || presets.Count == 0 || topN <= 0)
        {
            return Array.Empty<string>();
        }

        var blendScore = ComputeCombinedFlowCompatibilityScore(harmonicScore.Score, energyScore.Score);
        var ranked = presets
            .Select(preset => new
            {
                preset.PresetId,
                Score = ScorePresetCandidate(preset, harmonicScore, energyScore, phraseAligned, energyDelta, blendScore)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.PresetId, StringComparer.Ordinal)
            .Take(topN)
            .Select(x => x.PresetId)
            .ToList();

        return ranked;
    }

    private static double ScorePresetCandidate(
        FlowTransitionPreset preset,
        FlowCompatibilityScore harmonicScore,
        FlowCompatibilityScore energyScore,
        bool phraseAligned,
        double energyDelta,
        double blendScore)
    {
        if (blendScore < preset.MinCompatibilityScore)
        {
            return -100d;
        }

        if (preset.RequiresPhraseAlignment && !phraseAligned)
        {
            return -100d;
        }

        var score = 50d + blendScore;

        if (energyDelta < preset.MinSuggestedEnergyDelta || energyDelta > preset.MaxSuggestedEnergyDelta)
        {
            score -= 12d;
        }

        if (harmonicScore.Label == "lock" && (preset.PresetId == "bass-swap" || preset.PresetId == "full"))
        {
            score += 12d;
        }

        if (harmonicScore.Label == "risky" && preset.PresetId == "full")
        {
            score -= 35d;
        }

        if (energyScore.Label == "mismatch")
        {
            if (preset.PresetId is "crossfade" or "bass-swap")
            {
                score += 14d;
            }

            if (preset.PresetId == "crossfade")
            {
                score += 6d;
            }

            if (preset.PresetId == "none")
            {
                score -= 8d;
            }

            if (preset.PresetId == "full")
            {
                score -= 20d;
            }
        }

        if (energyScore.Label == "smooth" && preset.PresetId is "none" or "crossfade")
        {
            score += 8d;
        }

        // Keep "Custom" available for manual choice, but avoid auto-prioritizing it
        // over curated presets in suggestion chips.
        if (preset.PresetId == "custom")
        {
            score -= 25d;
        }

        return score;
    }

    private static IReadOnlyList<string> BuildFlowWarningFlags(
        FlowCompatibilityScore harmonicScore,
        FlowCompatibilityScore energyScore,
        bool phraseAligned)
    {
        var warnings = new List<string>();

        if (harmonicScore.Label == "risky")
        {
            warnings.Add("Harmonic mismatch risk");
        }

        if (energyScore.Label == "mismatch")
        {
            warnings.Add("Energy jump is aggressive");
        }

        if (!phraseAligned)
        {
            warnings.Add("Phrase anchors missing");
        }

        return warnings;
    }

    private static double ToCanvasX(double seconds, double timelineOffsetSeconds, double timelineWindowSeconds, double timelineCanvasWidth)
    {
        if (timelineWindowSeconds <= 0)
        {
            return 0;
        }

        var ratio = (seconds - timelineOffsetSeconds) / timelineWindowSeconds;
        return Math.Clamp(ratio, 0.0, 1.0) * timelineCanvasWidth;
    }

    public void PreviewFlowTransitionLengthFromCanvasDelta(string transitionKey, double initialLengthSeconds, double deltaCanvasPixels)
    {
        if (string.IsNullOrWhiteSpace(transitionKey))
        {
            return;
        }

        var nextLength = ComputeTransitionLengthFromCanvasDelta(transitionKey, initialLengthSeconds, deltaCanvasPixels);
        SetFlowTransitionLengthOverride(transitionKey, nextLength);
    }

    public void CommitFlowTransitionLengthFromCanvasDelta(string transitionKey, double initialLengthSeconds, double deltaCanvasPixels)
    {
        if (string.IsNullOrWhiteSpace(transitionKey))
        {
            return;
        }

        var nextLength = ComputeTransitionLengthFromCanvasDelta(transitionKey, initialLengthSeconds, deltaCanvasPixels);
        if (Math.Abs(nextLength - initialLengthSeconds) < 0.05)
        {
            SetFlowTransitionLengthOverride(transitionKey, initialLengthSeconds);
            return;
        }

        SetFlowTransitionLengthOverride(transitionKey, nextLength);
        _undoService.Push(new FlowTransitionLengthOperation(this, transitionKey, initialLengthSeconds, nextLength));
        RaiseHeaderProperties();
    }

    public void PreviewFlowPhraseMarkerFromCanvasDelta(string transitionKey, double initialPhraseSeconds, double deltaCanvasPixels)
    {
        if (string.IsNullOrWhiteSpace(transitionKey))
        {
            return;
        }

        var nextPhraseSeconds = ComputeFlowPhraseMarkerSecondsFromCanvasDelta(transitionKey, initialPhraseSeconds, deltaCanvasPixels);
        SetFlowTransitionPhraseMarkerState(transitionKey, nextPhraseSeconds, isExplicit: true);
    }

    public void CommitFlowPhraseMarkerFromCanvasDelta(string transitionKey, double initialPhraseSeconds, double deltaCanvasPixels)
    {
        if (string.IsNullOrWhiteSpace(transitionKey))
        {
            return;
        }

        var nextPhraseSeconds = ComputeFlowPhraseMarkerSecondsFromCanvasDelta(transitionKey, initialPhraseSeconds, deltaCanvasPixels);
        if (Math.Abs(nextPhraseSeconds - initialPhraseSeconds) < 0.03)
        {
            SetFlowTransitionPhraseMarkerState(transitionKey, initialPhraseSeconds, isExplicit: true);
            return;
        }

        SetFlowTransitionPhraseMarkerState(transitionKey, nextPhraseSeconds, isExplicit: true);
        _undoService.Push(new FlowTransitionPhraseMarkerOperation(this, transitionKey, initialPhraseSeconds, false, nextPhraseSeconds, true));
        RaiseHeaderProperties();
    }

    public void ToggleFlowPhraseMarkerEditState(string transitionKey)
    {
        if (string.IsNullOrWhiteSpace(transitionKey) || !TryGetFlowTransition(transitionKey, out var overlay) || overlay == null)
        {
            return;
        }

        var fromPhraseSeconds = overlay.PhraseGuideSeconds;
        var fromExplicit = overlay.IsPhraseMarkerExplicit;
        var toExplicit = !fromExplicit;
        var toPhraseSeconds = toExplicit ? overlay.PhraseGuideSeconds : overlay.PhraseGuideSeconds;

        SetFlowTransitionPhraseMarkerState(transitionKey, toPhraseSeconds, toExplicit);
        _undoService.Push(new FlowTransitionPhraseMarkerOperation(this, transitionKey, fromPhraseSeconds, fromExplicit, toPhraseSeconds, toExplicit));
        RaiseHeaderProperties();
    }

    private double ComputeTransitionLengthFromCanvasDelta(string transitionKey, double initialLengthSeconds, double deltaCanvasPixels)
    {
        var deltaSeconds = (deltaCanvasPixels / 700.0) * Math.Max(1.0, TimelineWindowSeconds);
        var candidateLength = Math.Max(2.0, initialLengthSeconds + deltaSeconds);
        if (!TryGetFlowTransition(transitionKey, out var overlay) || overlay == null)
        {
            return candidateLength;
        }

        var endTime = overlay.StartSeconds + candidateLength;
        var snapThresholdSeconds = Math.Max(0.18, (Math.Max(1.0, TimelineWindowSeconds) / 700.0) * 9.0);
        var snappedEnd = ComputePhraseAwareSnapEndSeconds(
            endTime,
            overlay.BeatGuideSeconds,
            overlay.PhraseSnapCandidatesSeconds,
            snapThresholdSeconds,
            overlay.PhraseRegions);

        var snappedLength = Math.Max(2.0, snappedEnd - overlay.StartSeconds);
        return Math.Clamp(snappedLength, 2.0, Math.Max(8.0, TimelineWindowSeconds * 0.95));
    }

    private double ComputeFlowPhraseMarkerSecondsFromCanvasDelta(string transitionKey, double initialPhraseSeconds, double deltaCanvasPixels)
    {
        var deltaSeconds = (deltaCanvasPixels / 700.0) * Math.Max(1.0, TimelineWindowSeconds);
        var candidate = Math.Max(0.0, initialPhraseSeconds + deltaSeconds);
        if (!TryGetFlowTransition(transitionKey, out var overlay) || overlay == null)
        {
            return candidate;
        }

        var min = Math.Max(0.0, overlay.StartSeconds - 24.0);
        var max = overlay.EndSeconds + 24.0;
        return Math.Clamp(candidate, min, max);
    }

    public static double ComputePhraseAwareSnapEndSeconds(
        double desiredEndSeconds,
        double beatGuideSeconds,
        IReadOnlyList<double>? phraseSnapCandidatesSeconds,
        double snapThresholdSeconds,
        IReadOnlyList<FlowPhraseRegion>? phraseRegions = null)
    {
        if (snapThresholdSeconds <= 0)
        {
            return desiredEndSeconds;
        }

        if (phraseRegions != null && phraseRegions.Count > 0)
        {
            var explicitBoundaries = phraseRegions
                .Where(r => r.IsExplicit || r.Provenance == FlowPhraseRegionProvenance.ExplicitUser)
                .SelectMany(r => new[] { r.StartSeconds, r.EndSeconds })
                .Distinct()
                .ToList();
            var mixedBoundaries = phraseRegions
                .Where(r => r.Provenance == FlowPhraseRegionProvenance.Mixed)
                .SelectMany(r => new[] { r.StartSeconds, r.EndSeconds })
                .Distinct()
                .ToList();
            var inferredBoundaries = phraseRegions
                .Where(r => !r.IsExplicit && r.Provenance == FlowPhraseRegionProvenance.Inferred)
                .SelectMany(r => new[] { r.StartSeconds, r.EndSeconds })
                .Distinct()
                .ToList();

            var prioritizedBoundaryGroups = new[] { explicitBoundaries, mixedBoundaries, inferredBoundaries };
            foreach (var group in prioritizedBoundaryGroups)
            {
                if (group.Count == 0)
                {
                    continue;
                }

                var boundaryMatch = FindClosestCandidateWithinThreshold(desiredEndSeconds, group, snapThresholdSeconds);
                if (boundaryMatch.HasValue)
                {
                    return boundaryMatch.Value;
                }
            }
        }

        if (phraseSnapCandidatesSeconds != null && phraseSnapCandidatesSeconds.Count > 0)
        {
            var closestPhrase = FindClosestCandidateWithinThreshold(desiredEndSeconds, phraseSnapCandidatesSeconds, snapThresholdSeconds);
            if (closestPhrase.HasValue)
            {
                return closestPhrase.Value;
            }
        }

        return Math.Abs(desiredEndSeconds - beatGuideSeconds) <= snapThresholdSeconds
            ? beatGuideSeconds
            : desiredEndSeconds;
    }

    private static double? FindClosestCandidateWithinThreshold(double desiredSeconds, IEnumerable<double> candidates, double thresholdSeconds)
    {
        double? closest = null;
        var closestDistance = double.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = Math.Abs(desiredSeconds - candidate);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closest = candidate;
            }
        }

        return closest.HasValue && closestDistance <= thresholdSeconds
            ? closest
            : null;
    }

    private static string BuildFlowPhraseRegionProvenanceLabel(IReadOnlyList<FlowPhraseRegion>? regions)
    {
        if (regions == null || regions.Count == 0)
        {
            return "None";
        }

        if (regions.All(r => r.Provenance == FlowPhraseRegionProvenance.ExplicitUser))
        {
            return "Manual";
        }

        if (regions.All(r => r.Provenance == FlowPhraseRegionProvenance.Inferred))
        {
            return "Suggested";
        }

        return "Hybrid";
    }

    private bool TryGetFlowTransition(string transitionKey, out FlowTransitionOverlayViewModel? overlay)
    {
        overlay = FlowTransitions.FirstOrDefault(t => string.Equals(t.TransitionKey, transitionKey, StringComparison.Ordinal));
        return overlay != null;
    }

    private void SetFlowTransitionLengthOverride(string transitionKey, double lengthSeconds)
    {
        var defaultLength = GetDefaultFlowTransitionLength(transitionKey);
        if (defaultLength.HasValue && Math.Abs(lengthSeconds - defaultLength.Value) < 0.05)
        {
            _flowTransitionLengthOverrides.Remove(transitionKey);
        }
        else
        {
            _flowTransitionLengthOverrides[transitionKey] = Math.Max(2.0, lengthSeconds);
        }

        RaiseFlowSelectionProperties();
    }

    internal void SetFlowTransitionPhraseMarkerState(string transitionKey, double phraseSeconds, bool isExplicit)
    {
        var baseline = BuildFlowTransitions(Decks, TimelineOffsetSeconds, TimelineWindowSeconds, selectedTransitionKey: null, lengthOverrides: null, presetOverrides: null, phraseMarkerOverrides: null, phraseMarkerExplicitKeys: null)
            .FirstOrDefault(t => string.Equals(t.TransitionKey, transitionKey, StringComparison.Ordinal));
        if (baseline != null && Math.Abs(phraseSeconds - baseline.PhraseGuideSeconds) < 0.03 && !isExplicit)
        {
            _flowTransitionPhraseMarkerOverrides.Remove(transitionKey);
            _flowTransitionPhraseMarkerExplicitKeys.Remove(transitionKey);
        }
        else
        {
            _flowTransitionPhraseMarkerOverrides[transitionKey] = Math.Max(0.0, phraseSeconds);
            if (isExplicit)
            {
                _flowTransitionPhraseMarkerExplicitKeys.Add(transitionKey);
            }
            else
            {
                _flowTransitionPhraseMarkerExplicitKeys.Remove(transitionKey);
            }
        }

        RaiseFlowSelectionProperties();
    }

    internal void ClearFlowTransitionPhraseMarkerOverride(string transitionKey)
    {
        _flowTransitionPhraseMarkerOverrides.Remove(transitionKey);
        _flowTransitionPhraseMarkerExplicitKeys.Remove(transitionKey);
        RaiseFlowSelectionProperties();
    }

    internal void SetFlowTransitionPresetOverride(string transitionKey, string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
        {
            _flowTransitionPresetOverrides.Remove(transitionKey);
        }
        else
        {
            _flowTransitionPresetOverrides[transitionKey] = presetId;
        }

        RaiseFlowSelectionProperties();
    }

    internal void CreateFlowPhraseRegionForTransition(string transitionKey)
    {
        if (string.IsNullOrWhiteSpace(transitionKey) || !TryGetFlowTransition(transitionKey, out var overlay) || overlay == null)
        {
            return;
        }

        _flowPhraseRegionOverrides.TryGetValue(transitionKey, out var previousRegion);
        var defaultSpanSeconds = (60.0 / Math.Max(1.0, overlay.PhraseGuideSeconds > 0 ? 1.0 : 1.0)) * 4.0 * (overlay.PhraseGuideSeconds > 0 ? 1.0 : 1.0);
        // Default span: 4 bars at 120 BPM ≈ 8 s; clamp to transition window
        var spanSeconds = Math.Clamp(8.0, 4.0, Math.Max(8.0, overlay.EndSeconds - overlay.StartSeconds));
        var startSec = Math.Max(overlay.StartSeconds, overlay.PhraseGuideSeconds - spanSeconds / 2.0);
        var endSec = Math.Min(overlay.EndSeconds, startSec + spanSeconds);
        var newRegion = new FlowPhraseRegion(
            transitionKey,
            startSec,
            endSec,
            true,
            1.0,
            null,
            FlowPhraseRegionProvenance.ExplicitUser);
        _undoService.Push(new FlowPhraseRegionOperation(this, transitionKey, previousRegion, newRegion));
        SetFlowPhraseRegionOverride(transitionKey, newRegion);
        RaiseHeaderProperties();
    }

    internal void RemoveFlowPhraseRegionForTransition(string transitionKey)
    {
        if (string.IsNullOrWhiteSpace(transitionKey))
        {
            return;
        }

        _flowPhraseRegionOverrides.TryGetValue(transitionKey, out var previousRegion);
        if (previousRegion == null)
        {
            return;
        }

        _undoService.Push(new FlowPhraseRegionOperation(this, transitionKey, previousRegion, null));
        ClearFlowPhraseRegionOverride(transitionKey);
        RaiseHeaderProperties();
    }

    internal void PreviewFlowPhraseRegionBoundary(string transitionKey, bool isStartHandle, double initialBoundarySeconds, double deltaCanvasPixels)
    {
        if (string.IsNullOrWhiteSpace(transitionKey) || !_flowPhraseRegionOverrides.TryGetValue(transitionKey, out var existing))
        {
            return;
        }

        var deltaSeconds = (deltaCanvasPixels / 700.0) * Math.Max(1.0, TimelineWindowSeconds);
        var newBoundary = Math.Max(0.0, initialBoundarySeconds + deltaSeconds);
        var updated = isStartHandle
            ? new FlowPhraseRegion(transitionKey, newBoundary, existing.EndSeconds, existing.IsExplicit, existing.Confidence, existing.SourceCueIds, existing.Provenance)
            : new FlowPhraseRegion(transitionKey, existing.StartSeconds, newBoundary, existing.IsExplicit, existing.Confidence, existing.SourceCueIds, existing.Provenance);
        SetFlowPhraseRegionOverride(transitionKey, updated);
    }

    internal void CommitFlowPhraseRegionBoundary(string transitionKey, bool isStartHandle, double initialBoundarySeconds, double deltaCanvasPixels)
    {
        if (string.IsNullOrWhiteSpace(transitionKey) || !_flowPhraseRegionOverrides.TryGetValue(transitionKey, out var existing))
        {
            return;
        }

        var deltaSeconds = (deltaCanvasPixels / 700.0) * Math.Max(1.0, TimelineWindowSeconds);
        var newBoundary = Math.Max(0.0, initialBoundarySeconds + deltaSeconds);
        if (Math.Abs(newBoundary - initialBoundarySeconds) < 0.03)
        {
            return;
        }

        // Take snapshot before mutation for undo
        var priorRegion = isStartHandle
            ? new FlowPhraseRegion(transitionKey, initialBoundarySeconds, existing.EndSeconds, existing.IsExplicit, existing.Confidence, existing.SourceCueIds, existing.Provenance)
            : new FlowPhraseRegion(transitionKey, existing.StartSeconds, initialBoundarySeconds, existing.IsExplicit, existing.Confidence, existing.SourceCueIds, existing.Provenance);
        var updated = isStartHandle
            ? new FlowPhraseRegion(transitionKey, newBoundary, existing.EndSeconds, existing.IsExplicit, existing.Confidence, existing.SourceCueIds, existing.Provenance)
            : new FlowPhraseRegion(transitionKey, existing.StartSeconds, newBoundary, existing.IsExplicit, existing.Confidence, existing.SourceCueIds, existing.Provenance);
        _undoService.Push(new FlowPhraseRegionOperation(this, transitionKey, priorRegion, updated));
        SetFlowPhraseRegionOverride(transitionKey, updated);
        RaiseHeaderProperties();
    }

    internal void SetFlowPhraseRegionOverride(string transitionKey, FlowPhraseRegion? region)
    {
        if (string.IsNullOrWhiteSpace(transitionKey))
        {
            return;
        }

        if (region == null)
        {
            _flowPhraseRegionOverrides.Remove(transitionKey);
        }
        else
        {
            _flowPhraseRegionOverrides[transitionKey] = region;
        }

        RaiseFlowSelectionProperties();
    }

    internal void ClearFlowPhraseRegionOverride(string transitionKey)
    {
        _flowPhraseRegionOverrides.Remove(transitionKey);
        RaiseFlowSelectionProperties();
    }

    internal void ClearFlowTransitionLengthOverride(string transitionKey)
    {
        _flowTransitionLengthOverrides.Remove(transitionKey);
        RaiseFlowSelectionProperties();
    }

    private double? GetDefaultFlowTransitionLength(string transitionKey)
    {
        var baseline = BuildFlowTransitions(Decks, TimelineOffsetSeconds, TimelineWindowSeconds, selectedTransitionKey: null, lengthOverrides: null, presetOverrides: null, phraseMarkerOverrides: null, phraseMarkerExplicitKeys: null)
            .FirstOrDefault(t => string.Equals(t.TransitionKey, transitionKey, StringComparison.Ordinal));
        return baseline?.LengthSeconds;
    }

    private sealed class FlowTransitionLengthOperation : IUndoableOperation
    {
        private readonly WorkstationViewModel _owner;
        private readonly string _transitionKey;
        private readonly double _fromLength;
        private readonly double _toLength;

        public FlowTransitionLengthOperation(WorkstationViewModel owner, string transitionKey, double fromLength, double toLength)
        {
            _owner = owner;
            _transitionKey = transitionKey;
            _fromLength = fromLength;
            _toLength = toLength;
        }

        public string Description => "Resize flow transition";

        public void Execute()
        {
            _owner.SetFlowTransitionLengthOverride(_transitionKey, _toLength);
        }

        public void Undo()
        {
            _owner.SetFlowTransitionLengthOverride(_transitionKey, _fromLength);
        }
    }

    private sealed class FlowTransitionPhraseMarkerOperation : IUndoableOperation
    {
        private readonly WorkstationViewModel _owner;
        private readonly string _transitionKey;
        private readonly double _fromPhraseSeconds;
        private readonly bool _fromExplicit;
        private readonly double _toPhraseSeconds;
        private readonly bool _toExplicit;

        public FlowTransitionPhraseMarkerOperation(WorkstationViewModel owner, string transitionKey, double fromPhraseSeconds, bool fromExplicit, double toPhraseSeconds, bool toExplicit)
        {
            _owner = owner;
            _transitionKey = transitionKey;
            _fromPhraseSeconds = fromPhraseSeconds;
            _fromExplicit = fromExplicit;
            _toPhraseSeconds = toPhraseSeconds;
            _toExplicit = toExplicit;
        }

        public string Description => "Edit phrase marker";

        public void Execute()
        {
            _owner.SetFlowTransitionPhraseMarkerState(_transitionKey, _toPhraseSeconds, _toExplicit);
        }

        public void Undo()
        {
            _owner.SetFlowTransitionPhraseMarkerState(_transitionKey, _fromPhraseSeconds, _fromExplicit);
        }
    }

    private sealed class FlowPhraseRegionOperation : IUndoableOperation
    {
        private readonly WorkstationViewModel _owner;
        private readonly string _transitionKey;
        private readonly FlowPhraseRegion? _fromRegion;
        private readonly FlowPhraseRegion? _toRegion;

        public FlowPhraseRegionOperation(WorkstationViewModel owner, string transitionKey, FlowPhraseRegion? fromRegion, FlowPhraseRegion? toRegion)
        {
            _owner = owner;
            _transitionKey = transitionKey;
            _fromRegion = fromRegion;
            _toRegion = toRegion;
        }

        public string Description => "Edit phrase region";

        public void Execute()
        {
            _owner.SetFlowPhraseRegionOverride(_transitionKey, _toRegion);
        }

        public void Undo()
        {
            _owner.SetFlowPhraseRegionOverride(_transitionKey, _fromRegion);
        }
    }

    private sealed class FlowTransitionPresetOperation : IUndoableOperation
    {
        private readonly WorkstationViewModel _owner;
        private readonly string _transitionKey;
        private readonly string? _fromPresetId;
        private readonly string? _toPresetId;
        private readonly double? _fromLengthSeconds;
        private readonly double? _toLengthSeconds;

        public FlowTransitionPresetOperation(WorkstationViewModel owner, string transitionKey, string? fromPresetId, string? toPresetId, double? fromLengthSeconds = null, double? toLengthSeconds = null)
        {
            _owner = owner;
            _transitionKey = transitionKey;
            _fromPresetId = fromPresetId;
            _toPresetId = toPresetId;
            _fromLengthSeconds = fromLengthSeconds;
            _toLengthSeconds = toLengthSeconds;
        }

        public string Description => "Apply flow transition preset";

        public void Execute()
        {
            _owner.SetFlowTransitionPresetOverride(_transitionKey, _toPresetId);
            if (_toLengthSeconds.HasValue)
                _owner.SetFlowTransitionLengthOverride(_transitionKey, _toLengthSeconds.Value);
        }

        public void Undo()
        {
            _owner.SetFlowTransitionPresetOverride(_transitionKey, _fromPresetId);
            if (_fromLengthSeconds.HasValue)
                _owner.SetFlowTransitionLengthOverride(_transitionKey, _fromLengthSeconds.Value);
            else if (_toLengthSeconds.HasValue)
                _owner.ClearFlowTransitionLengthOverride(_transitionKey);
        }
    }

    private static double CamelotDistance(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return 4d;

        var parsedA = ParseCamelot(a);
        var parsedB = ParseCamelot(b);
        if (parsedA == null || parsedB == null)
            return 4d;

        var (numberA, letterA) = parsedA.Value;
        var (numberB, letterB) = parsedB.Value;

        var circleDiff = Math.Abs(numberA - numberB);
        circleDiff = Math.Min(circleDiff, 12 - circleDiff);
        var letterPenalty = letterA == letterB ? 0d : 1d;
        return circleDiff + letterPenalty;
    }

    private static (int number, char letter)? ParseCamelot(string value)
    {
        var trimmed = value.Trim().ToUpperInvariant();
        if (trimmed.Length < 2)
            return null;

        var letter = trimmed[^1];
        if (letter != 'A' && letter != 'B')
            return null;

        if (!int.TryParse(trimmed[..^1], out var number))
            return null;

        if (number is < 1 or > 12)
            return null;

        return (number, letter);
    }

}
