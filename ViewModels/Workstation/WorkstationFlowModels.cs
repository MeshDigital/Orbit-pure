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
/// The two workspaces inside the Workstation page. Enum member names kept as
/// Waveform/Flow (matching the primary surface of each workspace) so existing
/// bindings and persisted sessions stay valid; the UI labels them PREP and SET PLAN.
/// </summary>
public enum WorkstationMode
{
    /// <summary>PREP: decks, waveforms, hot cues, loops, and the stem rack.</summary>
    Waveform,
    /// <summary>SET PLAN: playlist sequence, transition scoring, energy arc.</summary>
    Flow,
    /// <summary>Retired: sets moved to the Sets page. Kept so saved sessions still parse; folds to Waveform.</summary>
    StoryGraph,
    /// <summary>Retired: replaced by the shared transition editor. Kept for session compat; folds to Waveform.</summary>
    TransitionLab,
}

public sealed class WorkstationToolOptionViewModel : ReactiveObject
{
    private bool _isSelected;

    public WorkstationToolOptionViewModel(string shortLabel, string displayName, string description, WorkstationMode? mode, bool isAvailable)
    {
        ShortLabel = shortLabel;
        DisplayName = displayName;
        Description = description;
        Mode = mode;
        IsAvailable = isAvailable;
    }

    public string ShortLabel { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public WorkstationMode? Mode { get; }
    public bool IsAvailable { get; }
    public string StatusText => IsAvailable ? Description : $"{DisplayName} is staged for a later cockpit slice.";

    public bool IsSelected
    {
        get => _isSelected;
        internal set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

public sealed class PlaylistFlowTransitionViewModel
{
    public PlaylistFlowTransitionViewModel(
        string transitionKey,
        string transitionLabel,
        double canvasLeft,
        double canvasWidth,
        double harmonicCompatibilityScore,
        string harmonicCompatibilityLabel,
        double energyCompatibilityScore,
        string energyCompatibilityLabel,
        double combinedCompatibilityScore,
        int sequenceIndex)
    {
        TransitionKey = transitionKey;
        TransitionLabel = transitionLabel;
        CanvasLeft = canvasLeft;
        CanvasWidth = canvasWidth;
        HarmonicCompatibilityScore = harmonicCompatibilityScore;
        HarmonicCompatibilityLabel = harmonicCompatibilityLabel;
        EnergyCompatibilityScore = energyCompatibilityScore;
        EnergyCompatibilityLabel = energyCompatibilityLabel;
        CombinedCompatibilityScore = combinedCompatibilityScore;
        SequenceIndex = sequenceIndex;
        CompatibilityColor = combinedCompatibilityScore >= 80 ? "#66B8E986"
            : combinedCompatibilityScore >= 60 ? "#66FFD58A"
            : combinedCompatibilityScore >= 40 ? "#66FFA94B"
            : "#66FF6B6B";
    }

    public string TransitionKey { get; }
    public string TransitionLabel { get; }
    public double CanvasLeft { get; }
    public double CanvasWidth { get; }
    public double HarmonicCompatibilityScore { get; }
    public string HarmonicCompatibilityLabel { get; }
    public double EnergyCompatibilityScore { get; }
    public string EnergyCompatibilityLabel { get; }
    public double CombinedCompatibilityScore { get; }
    public int SequenceIndex { get; }
    public string CompatibilityColor { get; }
}

public sealed class FlowTransitionOverlayViewModel
{
    public FlowTransitionOverlayViewModel(
        string transitionKey,
        string transitionLabel,
        string compatibilityLabel,
        double startSeconds,
        double endSeconds,
        double canvasLeft,
        double canvasWidth,
        double phraseGuideSeconds,
        double beatGuideSeconds,
        double phraseGuideCanvasLeft,
        double beatGuideCanvasLeft,
        IReadOnlyList<double> phraseSnapCandidatesSeconds,
        bool isPhraseMarkerExplicit,
        double phraseMarkerConfidenceScore,
        string phraseMarkerConfidenceLabel,
        string phraseMarkerTooltip,
        bool phraseAligned,
        bool beatAligned,
        double lengthSeconds,
        bool isSelected,
        bool isLengthSnapped,
        string? appliedPresetId,
        IReadOnlyList<string> suggestedPresetIds,
        double harmonicCompatibilityScore,
        string harmonicCompatibilityLabel,
        double energyCompatibilityScore,
        string energyCompatibilityLabel,
        double combinedCompatibilityScore,
        IReadOnlyList<FlowPhraseRegion> phraseRegions,
        IReadOnlyList<string> warningFlags)
    {
        TransitionKey = transitionKey;
        TransitionLabel = transitionLabel;
        CompatibilityLabel = compatibilityLabel;
        StartSeconds = startSeconds;
        EndSeconds = endSeconds;
        CanvasLeft = canvasLeft;
        CanvasWidth = canvasWidth;
        PhraseGuideSeconds = phraseGuideSeconds;
        BeatGuideSeconds = beatGuideSeconds;
        PhraseGuideCanvasLeft = phraseGuideCanvasLeft;
        BeatGuideCanvasLeft = beatGuideCanvasLeft;
        PhraseSnapCandidatesSeconds = phraseSnapCandidatesSeconds ?? Array.Empty<double>();
        IsPhraseMarkerExplicit = isPhraseMarkerExplicit;
        PhraseMarkerConfidenceScore = phraseMarkerConfidenceScore;
        PhraseMarkerConfidenceLabel = phraseMarkerConfidenceLabel;
        PhraseMarkerTooltip = phraseMarkerTooltip;
        PhraseAligned = phraseAligned;
        BeatAligned = beatAligned;
        LengthSeconds = lengthSeconds;
        IsSelected = isSelected;
        IsLengthSnapped = isLengthSnapped;
        AppliedPresetId = appliedPresetId;
        SuggestedPresetIds = suggestedPresetIds ?? Array.Empty<string>();
        HarmonicCompatibilityScore = harmonicCompatibilityScore;
        HarmonicCompatibilityLabel = harmonicCompatibilityLabel;
        EnergyCompatibilityScore = energyCompatibilityScore;
        EnergyCompatibilityLabel = energyCompatibilityLabel;
        CombinedCompatibilityScore = combinedCompatibilityScore;
        PhraseRegions = phraseRegions ?? Array.Empty<FlowPhraseRegion>();
        ActivePhraseRegion = GetActivePhraseRegion(PhraseRegions);
        var totalSpanSeconds = PhraseRegions.Sum(r => Math.Max(0.0, r.EndSeconds - r.StartSeconds));
        var provenanceLabel = BuildPhraseRegionProvenanceLabel(PhraseRegions);
        PhraseRegionSpanLabel = PhraseRegions.Count == 0
            ? "Region: none"
            : $"{PhraseRegions.Count} region{(PhraseRegions.Count == 1 ? string.Empty : "s")} · {totalSpanSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s · {provenanceLabel}";
        PhraseRegionTooltip = PhraseRegions.Count == 0
            ? "Phrase regions are scaffolded and not yet edited."
            : $"{PhraseRegionSpanLabel} • {ActivePhraseRegion?.Tooltip ?? "scaffolded"}";
        WarningFlags = warningFlags ?? Array.Empty<string>();
    }

    private static FlowPhraseRegion? GetActivePhraseRegion(IReadOnlyList<FlowPhraseRegion> regions)
    {
        if (regions == null || regions.Count == 0)
        {
            return null;
        }

        return regions.FirstOrDefault(r => r.IsExplicit || r.Provenance == FlowPhraseRegionProvenance.ExplicitUser)
            ?? regions[0];
    }

    private static string BuildPhraseRegionProvenanceLabel(IReadOnlyList<FlowPhraseRegion> regions)
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

    public string TransitionKey { get; }
    public string TransitionLabel { get; }
    public string CompatibilityLabel { get; }
    public double StartSeconds { get; }
    public double EndSeconds { get; }
    public double CanvasLeft { get; }
    public double CanvasWidth { get; }
    public double PhraseGuideSeconds { get; }
    public double BeatGuideSeconds { get; }
    public double PhraseGuideCanvasLeft { get; }
    public double BeatGuideCanvasLeft { get; }
    public IReadOnlyList<double> PhraseSnapCandidatesSeconds { get; }
    public bool IsPhraseMarkerExplicit { get; }
    public double PhraseMarkerConfidenceScore { get; }
    public string PhraseMarkerConfidenceLabel { get; }
    public string PhraseMarkerTooltip { get; }
    public double PhraseMarkerOpacity => IsPhraseMarkerExplicit ? 1.0 : 0.58 + (PhraseMarkerConfidenceScore / 100.0) * 0.32;
    public bool PhraseAligned { get; }
    public bool BeatAligned { get; }
    public double LengthSeconds { get; }
    public bool IsSelected { get; }
    public bool IsLengthSnapped { get; }
    public string? AppliedPresetId { get; }
    public bool IsPresetApplied => AppliedPresetId != null;
    public IReadOnlyList<string> SuggestedPresetIds { get; }
    public double HarmonicCompatibilityScore { get; }
    public string HarmonicCompatibilityLabel { get; }
    public double EnergyCompatibilityScore { get; }
    public string EnergyCompatibilityLabel { get; }
    public double CombinedCompatibilityScore { get; }
    public IReadOnlyList<FlowPhraseRegion> PhraseRegions { get; }
    public FlowPhraseRegion? ActivePhraseRegion { get; }
    public string PhraseRegionSpanLabel { get; }
    public string PhraseRegionTooltip { get; }
    public IReadOnlyList<string> WarningFlags { get; }
}

public sealed class FlowPhraseRegion
{
    public FlowPhraseRegion(
        string transitionKey,
        double startSeconds,
        double endSeconds,
        bool isExplicit,
        double confidence,
        IReadOnlyList<string>? sourceCueIds,
        FlowPhraseRegionProvenance provenance)
    {
        TransitionKey = transitionKey ?? string.Empty;
        StartSeconds = Math.Max(0.0, startSeconds);
        EndSeconds = Math.Max(StartSeconds, endSeconds);
        IsExplicit = isExplicit;
        Confidence = Math.Clamp(confidence, 0.0, 1.0);
        SourceCueIds = sourceCueIds ?? Array.Empty<string>();
        Provenance = provenance;
        CanvasLeft = StartSeconds;
        CanvasWidth = Math.Max(0.0, EndSeconds - StartSeconds);
        SpanLabel = NumFormat.Ui($"{StartSeconds:F1}s–{EndSeconds:F1}s");
        Tooltip = NumFormat.Ui($"{SpanLabel} • {Provenance} • {Confidence:P0}");
    }

    public string TransitionKey { get; }
    public double StartSeconds { get; }
    public double EndSeconds { get; }
    public bool IsExplicit { get; }
    public double Confidence { get; }
    public IReadOnlyList<string> SourceCueIds { get; }
    public FlowPhraseRegionProvenance Provenance { get; }
    public double CanvasLeft { get; }
    public double CanvasWidth { get; }
    public string SpanLabel { get; }
    public string Tooltip { get; }
}


public enum FlowTransitionCurveType
{
    EqualPower,
    BassSwap,
    FullSpectrum,
    HardCut,
    Custom
}

public enum FlowFrequencyBandStrategy
{
    Hold,
    Swap,
    Blend,
    Cut,
    Custom
}

public enum FlowPhraseRegionProvenance
{
    ExplicitUser,
    Inferred,
    Mixed
}

public sealed record FlowTransitionPreset(
    string PresetId,
    string DisplayName,
    string Description,
    FlowTransitionCurveType CurveType,
    double DefaultLengthSeconds,
    FlowFrequencyBandStrategy LowBandStrategy,
    FlowFrequencyBandStrategy MidBandStrategy,
    FlowFrequencyBandStrategy HighBandStrategy,
    double MinCompatibilityScore,
    bool RequiresPhraseAlignment,
    double MinSuggestedEnergyDelta,
    double MaxSuggestedEnergyDelta);

public sealed record FlowCompatibilityScore(double Score, string Label);

