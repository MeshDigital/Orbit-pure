using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Audio.Separation;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Workstation;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class WorkstationDeckViewModelTests
{
    [Fact]
    public void ResolvePhraseJumpTarget_PrefersMatchingCueRole()
    {
        var cues = new List<OrbitCue>
        {
            new() { Timestamp = 12.5, Name = "Build 1", Role = CueRole.Build },
            new() { Timestamp = 44.0, Name = "Drop 1", Role = CueRole.Drop }
        };

        var target = WorkstationDeckViewModel.ResolvePhraseJumpTarget(cues, CueRole.Drop, 120, 0.55);

        Assert.Equal(44.0, target, 3);
    }

    [Fact]
    public void ResolvePhraseJumpTarget_UsesFallbackRatioWhenCueMissing()
    {
        var target = WorkstationDeckViewModel.ResolvePhraseJumpTarget(new List<OrbitCue>(), CueRole.Outro, 200, 0.82);

        Assert.Equal(164.0, target, 3);
    }

    [Fact]
    public void BuildSuggestedHotCues_PrioritizesCoreSections()
    {
        var cues = new List<OrbitCue>
        {
            new() { Timestamp = 4.0, Name = "Intro", Role = CueRole.Intro, Color = "#00FFFF", Confidence = 0.9 },
            new() { Timestamp = 31.5, Name = "Build", Role = CueRole.Build, Color = "#FFFF00", Confidence = 0.92 },
            new() { Timestamp = 48.0, Name = "Drop", Role = CueRole.Drop, Color = "#FF0000", Confidence = 0.97 },
            new() { Timestamp = 92.0, Name = "Breakdown", Role = CueRole.Breakdown, Color = "#800080", Confidence = 0.8 },
            new() { Timestamp = 180.0, Name = "Outro", Role = CueRole.Outro, Color = "#00FFFF", Confidence = 0.88 }
        };

        var hotCues = WorkstationDeckViewModel.BuildSuggestedHotCues(cues).ToList();

        Assert.Equal(5, hotCues.Count);
        Assert.Equal("Intro", hotCues[0].Label);
        Assert.Equal("Build", hotCues[1].Label);
        Assert.Equal("Drop", hotCues[2].Label);
        Assert.Equal("Breakdown", hotCues[3].Label);
        Assert.Equal("Outro", hotCues[4].Label);
    }

    [Fact]
    public void BuildSuggestedHotCues_DeduplicatesNearbyMarkers()
    {
        var cues = new List<OrbitCue>
        {
            new() { Timestamp = 48.0, Name = "Drop A", Role = CueRole.Drop, Confidence = 0.8 },
            new() { Timestamp = 49.2, Name = "Drop B", Role = CueRole.Drop, Confidence = 0.95 },
            new() { Timestamp = 120.0, Name = "Outro", Role = CueRole.Outro, Confidence = 0.8 }
        };

        var hotCues = WorkstationDeckViewModel.BuildSuggestedHotCues(cues).ToList();

        Assert.Single(hotCues.Where(h => h.Label.Contains("Drop")));
        Assert.Contains(hotCues, h => h.Label == "Outro");
    }

    [Theory]
    [InlineData("Intro", "IN")]
    [InlineData("Build 1", "BLD")]
    [InlineData("Drop", "DRP")]
    [InlineData("Outro", "OUT")]
    [InlineData("", "1")]
    public void BuildHotCuePadText_UsesFriendlyShortLabels(string label, string expected)
    {
        HotCue? cue = string.IsNullOrWhiteSpace(label)
            ? null
            : new HotCue { Slot = 0, Label = label, PositionSeconds = 12.5, Color = "#FFFFFF" };

        var text = WorkstationDeckViewModel.BuildHotCuePadText(cue, 0);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void BuildCuePrepSummary_ReportsPreparedPadCount()
    {
        var summary = WorkstationDeckViewModel.BuildCuePrepSummary(new HotCue?[]
        {
            new HotCue { Slot = 0, Label = "Intro", PositionSeconds = 0 },
            new HotCue { Slot = 1, Label = "Drop", PositionSeconds = 48 },
            null,
            null
        });

        Assert.Contains("2 prep pads", summary);
        Assert.Contains("Intro", summary);
        Assert.Contains("Drop", summary);
    }

    [Fact]
    public void BuildMixReadinessText_ReportsReadyWhenCoreCuesExist()
    {
        var text = WorkstationDeckViewModel.BuildMixReadinessText(
            "8A",
            124.0,
            new List<OrbitCue>
            {
                new() { Timestamp = 0, Role = CueRole.Intro, Name = "Intro" },
                new() { Timestamp = 48, Role = CueRole.Drop, Name = "Drop" },
                new() { Timestamp = 180, Role = CueRole.Outro, Name = "Outro" }
            });

        Assert.Contains("Mix-ready", text);
        Assert.Contains("8A", text);
        Assert.Contains("124.0 BPM", text);
    }

    [Fact]
    public void BuildSectionJumpSummary_ReturnsFallbackWhenNoCuesExist()
    {
        var text = WorkstationDeckViewModel.BuildSectionJumpSummary(Array.Empty<OrbitCue>());

        Assert.Contains("guided", text);
    }

    [Fact]
    public void BuildSectionJumpSummary_ListsPreparedAnchors()
    {
        var text = WorkstationDeckViewModel.BuildSectionJumpSummary(
            new List<OrbitCue>
            {
                new() { Timestamp = 0, Role = CueRole.Intro, Name = "Intro" },
                new() { Timestamp = 24, Role = CueRole.Build, Name = "Build" },
                new() { Timestamp = 48, Role = CueRole.Drop, Name = "Drop" },
                new() { Timestamp = 180, Role = CueRole.Outro, Name = "Outro" }
            });

        Assert.Contains("Intro", text);
        Assert.Contains("Build", text);
        Assert.Contains("Drop", text);
        Assert.Contains("Outro", text);
    }

    [Fact]
    public void BuildTransitionStatus_ReportsSmoothBlendForCompatibleKeys()
    {
        var text = WorkstationDeckViewModel.BuildTransitionStatus(
            "A",
            "8A",
            124.0,
            new List<OrbitCue> { new() { Role = CueRole.Outro, Timestamp = 180, Name = "Outro" } },
            "B",
            "9A",
            124.8,
            new List<OrbitCue> { new() { Role = CueRole.Intro, Timestamp = 0, Name = "Intro" } });

        Assert.Contains("Deck B", text);
        Assert.Contains("Smooth blend", text);
        Assert.Contains("+0.8 BPM", text);
    }

    [Fact]
    public void BuildPerformanceStatusSummary_ReportsLiveDeckState()
    {
        var text = WorkstationDeckViewModel.BuildPerformanceStatusSummary("B", true, true, true, true, 2.5);

        Assert.Contains("Deck B", text);
        Assert.Contains("Live", text);
        Assert.Contains("loop on", text);
        Assert.Contains("key lock", text);
        Assert.Contains("+2.5%", text);
    }

    [Fact]
    public void BuildWaveformDetailSummary_ReportsSemanticWindowWhenZoomedIn()
    {
        var text = WorkstationDeckViewModel.BuildWaveformDetailSummary(true, 4.0, 0.35);

        Assert.Contains("Semantic detail", text);
        Assert.Contains("4.0×", text);
        Assert.Contains("35%", text);
    }

    [Fact]
    public void BuildPerformanceStatusSummary_ReportsCueState_WhenDeckIsNotLoaded()
    {
        var text = WorkstationDeckViewModel.BuildPerformanceStatusSummary("A", false, false, false, false, 0);

        Assert.Contains("Load a track", text);
    }

    [Fact]
    public void BuildDeckStatusSummary_ReportsLoadedDecksAndFocus()
    {
        var text = WorkstationViewModel.BuildDeckStatusSummary(2, 4, "B", 124.5);

        Assert.Contains("2/4 decks live", text);
        Assert.Contains("Focus B", text);
        Assert.Contains("124.5 BPM", text);
    }

    [Fact]
    public void BuildToolbarHint_ReportsModeAndGuides()
    {
        var text = WorkstationViewModel.BuildToolbarHint(WorkstationMode.Waveform, true, true);

        Assert.Contains("Waveform", text);
        Assert.Contains("Snap on", text);
        Assert.Contains("Quantize on", text);
    }

    [Fact]
    public void BuildDeckFocusSummary_ReportsFocusedAndOpenTargets()
    {
        var text = WorkstationViewModel.BuildDeckFocusSummary(new[] { "A", "B", "C" }, new[] { "A", "B" }, "B");

        Assert.Contains("A live", text);
        Assert.Contains("B focused", text);
        Assert.Contains("C open", text);
    }

    [Fact]
    public void BuildFlowWindowSummary_ReportsViewportRange()
    {
        var text = WorkstationViewModel.BuildFlowWindowSummary(30, 60);

        Assert.Contains("0:30", text);
        Assert.Contains("1:30", text);
        Assert.Contains("60s", text);
    }

    [Fact]
    public void BuildPlaylistFlowSummary_ReportsPlaylistReadinessAndMode()
    {
        var text = WorkstationViewModel.BuildPlaylistFlowSummary("Peak Set", 12, 2, WorkstationMode.Flow);

        Assert.Contains("Peak Set", text);
        Assert.Contains("12 flow-ready tracks", text);
        Assert.Contains("2 live decks", text);
        Assert.Contains("flow active", text);
    }

    [Fact]
    public void BuildAutomationModeSummary_ReportsSyncAndViewportState()
    {
        var text = WorkstationViewModel.BuildAutomationModeSummary(
            snapEnabled: true,
            quantizeEnabled: false,
            metronomeEnabled: true,
            flowWindowSummary: "Viewport 0:30 → 1:30 • 60s window",
            focusedDeckLabel: "B");

        Assert.Contains("Deck B focused", text);
        Assert.Contains("Snap on", text);
        Assert.Contains("Quantize off", text);
        Assert.Contains("Metronome on", text);
        Assert.Contains("Viewport 0:30", text);
    }

    [Fact]
    public void BuildSamplesModeSummary_ReportsFallbackWhenPlaylistOrDeckMissing()
    {
        var text = WorkstationViewModel.BuildSamplesModeSummary(
            playlistTitle: null,
            readyTrackCount: 0,
            focusedDeckLabel: null);

        Assert.Contains("No playlist selected", text);
        Assert.Contains("0 ready sample sources", text);
        Assert.Contains("No focused deck", text);
    }

    [Fact]
    public void BuildAnalysisQueueSummary_ReportsPausedPrepState()
    {
        var text = WorkstationViewModel.BuildAnalysisQueueSummary(2, 18, "track-hash", true, "Stealth", 1);

        Assert.Contains("Analysis paused", text);
        Assert.Contains("2 queued", text);
        Assert.Contains("18 prepped", text);
        Assert.Contains("Stealth", text);
    }

    [Fact]
    public void BuildTransportStatusSummary_ReportsLiveMixState()
    {
        var text = WorkstationViewModel.BuildTransportStatusSummary(true, 2, "B", true);

        Assert.Contains("Live", text);
        Assert.Contains("2 decks rolling", text);
        Assert.Contains("Focus B", text);
        Assert.Contains("loop armed", text);
    }

    [Fact]
    public void BuildFocusedDeckActionSummary_ReportsPreparedDeckActions()
    {
        var text = WorkstationViewModel.BuildFocusedDeckActionSummary("B", true, true, true);

        Assert.Contains("Deck B", text);
        Assert.Contains("jump cues", text);
        Assert.Contains("stems", text);
    }

    [Fact]
    public void BuildMixCoachSummary_CombinesHarmonicAndTransitionGuidance()
    {
        var text = WorkstationViewModel.BuildMixCoachSummary("B", "Harmonic lock • blend approved", "Smooth blend into Deck A");

        Assert.Contains("Deck B", text);
        Assert.Contains("Harmonic", text);
        Assert.Contains("Smooth blend", text);
    }

    [Fact]
    public void BuildFlowTransitions_MarksSelectedTransition()
    {
        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: "alpha->beta",
            lengthOverrides: null);

        var selected = Assert.Single(overlays);
        Assert.True(selected.IsSelected);
        Assert.Equal("alpha->beta", selected.TransitionKey);
    }

    [Fact]
    public void BuildFlowTransitions_DoesNotSelectWhenKeyDiffers()
    {
        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: "missing->key",
            lengthOverrides: null);

        var selected = Assert.Single(overlays);
        Assert.False(selected.IsSelected);
    }

    [Fact]
    public void BuildFlowTransitions_UsesLengthOverridesAndMarksSnapped()
    {
        var overrides = new Dictionary<string, double>
        {
            ["alpha->beta"] = 18.5
        };

        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: "alpha->beta",
            lengthOverrides: overrides);

        var selected = Assert.Single(overlays);
        Assert.True(selected.IsLengthSnapped);
        Assert.Equal(18.5, selected.LengthSeconds, 1);
        Assert.True(selected.CanvasWidth > 26);
        Assert.NotEmpty(selected.SuggestedPresetIds);
        Assert.True(selected.HarmonicCompatibilityScore > 0);
        Assert.True(selected.EnergyCompatibilityScore > 0);
        Assert.True(selected.CombinedCompatibilityScore > 0);
        Assert.NotEmpty(selected.PhraseSnapCandidatesSeconds);
    }

    [Fact]
    public void BuildFlowTransitions_UsesPhraseMarkerOverrides()
    {
        var phraseOverrides = new Dictionary<string, double>
        {
            ["alpha->beta"] = 42.0
        };

        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: "alpha->beta",
            lengthOverrides: null,
            presetOverrides: null,
            phraseMarkerOverrides: phraseOverrides);

        var selected = Assert.Single(overlays);
        Assert.Equal(42.0, selected.PhraseGuideSeconds, 2);
        Assert.Contains(42.0, selected.PhraseSnapCandidatesSeconds);
    }

    [Fact]
    public void BuildFlowTransitions_MarksExplicitPhraseMarkerState()
    {
        var explicitKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "alpha->beta"
        };

        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: "alpha->beta",
            lengthOverrides: null,
            presetOverrides: null,
            phraseMarkerOverrides: null,
            phraseMarkerExplicitKeys: explicitKeys);

        var selected = Assert.Single(overlays);
        Assert.True(selected.IsPhraseMarkerExplicit);
        Assert.StartsWith("explicit", selected.PhraseMarkerConfidenceLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1.0, selected.PhraseMarkerOpacity, precision: 2);
        Assert.Contains("remove", selected.PhraseMarkerTooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFlowTransitions_InferredRegionsMergeWhenOverlapping()
    {
        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: null,
            lengthOverrides: null);

        var selected = Assert.Single(overlays);
        var region = Assert.Single(selected.PhraseRegions);
        Assert.Equal(FlowPhraseRegionProvenance.Inferred, region.Provenance);
        Assert.False(region.IsExplicit);
    }

    [Fact]
    public void BuildFlowTransitions_ExplicitRegionOverridesInferredOverlap()
    {
        const string transitionKey = "alpha->beta";
        var explicitRegion = new FlowPhraseRegion(transitionKey, 2.0, 4.0, isExplicit: true, confidence: 1.0, sourceCueIds: null, FlowPhraseRegionProvenance.ExplicitUser);
        var regionOverrides = new Dictionary<string, FlowPhraseRegion>(StringComparer.Ordinal) { [transitionKey] = explicitRegion };

        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: null,
            lengthOverrides: null,
            phraseRegionOverrides: regionOverrides);

        var selected = Assert.Single(overlays);
        var explicitResolved = selected.PhraseRegions.FirstOrDefault(r => r.IsExplicit);
        Assert.NotNull(explicitResolved);
        Assert.Equal(2.0, explicitResolved!.StartSeconds, 2);
        Assert.Equal(4.0, explicitResolved.EndSeconds, 2);
    }

    [Fact]
    public void BuildFlowTransitions_InferredRegionsShrinkAroundExplicit()
    {
        const string transitionKey = "alpha->beta";
        var explicitRegion = new FlowPhraseRegion(transitionKey, 2.0, 4.0, isExplicit: true, confidence: 1.0, sourceCueIds: null, FlowPhraseRegionProvenance.ExplicitUser);
        var regionOverrides = new Dictionary<string, FlowPhraseRegion>(StringComparer.Ordinal) { [transitionKey] = explicitRegion };

        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: null,
            lengthOverrides: null,
            phraseRegionOverrides: regionOverrides);

        var selected = Assert.Single(overlays);
        Assert.True(selected.PhraseRegions.Count >= 3);
        Assert.Contains(selected.PhraseRegions, r => !r.IsExplicit && r.EndSeconds <= 2.05);
        Assert.Contains(selected.PhraseRegions, r => !r.IsExplicit && r.StartSeconds >= 3.95);
    }

    [Fact]
    public void BuildFlowTransitions_ProvenanceBecomesMixedWhenBothContribute()
    {
        const string transitionKey = "alpha->beta";
        var explicitRegion = new FlowPhraseRegion(transitionKey, 2.0, 4.0, isExplicit: true, confidence: 1.0, sourceCueIds: null, FlowPhraseRegionProvenance.ExplicitUser);
        var regionOverrides = new Dictionary<string, FlowPhraseRegion>(StringComparer.Ordinal) { [transitionKey] = explicitRegion };

        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: null,
            lengthOverrides: null,
            phraseRegionOverrides: regionOverrides);

        var selected = Assert.Single(overlays);
        Assert.Contains(selected.PhraseRegions, r => r.Provenance == FlowPhraseRegionProvenance.Mixed);
        Assert.Contains(selected.PhraseRegions, r => r.Provenance == FlowPhraseRegionProvenance.ExplicitUser);
    }

    [Fact]
    public void BuildFlowTransitions_RecomputesInferredConfidenceAfterMerge()
    {
        const string transitionKey = "alpha->beta";
        var explicitRegion = new FlowPhraseRegion(transitionKey, 2.0, 4.0, isExplicit: true, confidence: 1.0, sourceCueIds: null, FlowPhraseRegionProvenance.ExplicitUser);
        var regionOverrides = new Dictionary<string, FlowPhraseRegion>(StringComparer.Ordinal) { [transitionKey] = explicitRegion };

        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: null,
            lengthOverrides: null,
            phraseRegionOverrides: regionOverrides);

        var selected = Assert.Single(overlays);
        Assert.All(selected.PhraseRegions.Where(r => !r.IsExplicit), r => Assert.InRange(r.Confidence, 0.0, 1.0));
        Assert.All(selected.PhraseRegions.Where(r => r.IsExplicit), r => Assert.Equal(1.0, r.Confidence, 2));
    }

    [Fact]
    public void ComputePhraseAwareSnapEndSeconds_UsesRegionBoundaries()
    {
        var regions = new[]
        {
            new FlowPhraseRegion("alpha->beta", 31.5, 32.0, false, 0.6, null, FlowPhraseRegionProvenance.Inferred)
        };
        var snapped = WorkstationViewModel.ComputePhraseAwareSnapEndSeconds(
            desiredEndSeconds: 32.08,
            beatGuideSeconds: 33.0,
            phraseSnapCandidatesSeconds: new[] { 16.0, 48.0 },
            snapThresholdSeconds: 0.15,
            phraseRegions: regions);

        Assert.Equal(32.0, snapped, precision: 2);
    }

    [Fact]
    public void ComputePhraseAwareSnapEndSeconds_PrefersExplicitRegionBoundaryOverInferredAtEqualDistance()
    {
        var regions = new[]
        {
            new FlowPhraseRegion("alpha->beta", 31.0, 31.2, true, 1.0, null, FlowPhraseRegionProvenance.ExplicitUser),
            new FlowPhraseRegion("alpha->beta", 32.8, 33.0, false, 0.6, null, FlowPhraseRegionProvenance.Inferred)
        };

        var snapped = WorkstationViewModel.ComputePhraseAwareSnapEndSeconds(
            desiredEndSeconds: 32.0,
            beatGuideSeconds: 40.0,
            phraseSnapCandidatesSeconds: new[] { 31.9 },
            snapThresholdSeconds: 1.1,
            phraseRegions: regions);

        Assert.Equal(31.2, snapped, precision: 2);
    }

    [Fact]
    public void FlowInspectorPhraseRegionSpanLabel_ReflectsMergedCountSpanAndProvenance()
    {
        const string transitionKey = "alpha->beta";
        var explicitRegion = new FlowPhraseRegion(transitionKey, 2.0, 4.0, isExplicit: true, confidence: 1.0, sourceCueIds: null, FlowPhraseRegionProvenance.ExplicitUser);
        var regionOverrides = new Dictionary<string, FlowPhraseRegion>(StringComparer.Ordinal) { [transitionKey] = explicitRegion };

        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: null,
            lengthOverrides: null,
            phraseRegionOverrides: regionOverrides);

        var selected = Assert.Single(overlays);
        Assert.Contains("region", selected.PhraseRegionSpanLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hybrid", selected.PhraseRegionSpanLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlowPhraseRegionOperation_UndoRedoRestoresRegionState()
    {
        // Test undo/redo mechanics with a local dict and a simple operation
        const string transitionKey = "alpha->beta";
        var store = new Dictionary<string, FlowPhraseRegion?>(StringComparer.Ordinal);
        var before = (FlowPhraseRegion?)null;
        var after = new FlowPhraseRegion(transitionKey, 10.0, 18.0, isExplicit: true, confidence: 1.0, sourceCueIds: null, FlowPhraseRegionProvenance.ExplicitUser);

        var undoService = new UndoService();
        undoService.Push(new DelegateUndoOperation(
            execute: () => store[transitionKey] = after,
            undo: () => store[transitionKey] = before,
            description: "Set phrase region"));

        // After push, operation is already NOT executed — execute means redo
        // Initial state: nothing yet in store
        Assert.False(store.ContainsKey(transitionKey));

        // Simulate manual set (as the VM does: SetFlowPhraseRegionOverride before push)
        store[transitionKey] = after;
        Assert.NotNull(store[transitionKey]);

        // Undo should revert to before (null)
        undoService.Undo();
        Assert.Null(store[transitionKey]);

        // Redo should reapply
        undoService.Redo();
        Assert.NotNull(store[transitionKey]);
        Assert.Equal(10.0, store[transitionKey]!.StartSeconds, 2);
    }

    [Fact]
    public void ComputePhraseAwareSnapEndSeconds_PrefersNearestPhraseCandidate()
    {
        var snapped = WorkstationViewModel.ComputePhraseAwareSnapEndSeconds(
            desiredEndSeconds: 24.12,
            beatGuideSeconds: 24.18,
            phraseSnapCandidatesSeconds: new[] { 16.0, 24.05, 32.0 },
            snapThresholdSeconds: 0.15);

        Assert.Equal(24.05, snapped, precision: 2);
    }

    [Fact]
    public void ComputePhraseAwareSnapEndSeconds_FallsBackToBeatWhenNoPhraseCandidateWithinThreshold()
    {
        var snapped = WorkstationViewModel.ComputePhraseAwareSnapEndSeconds(
            desiredEndSeconds: 24.12,
            beatGuideSeconds: 24.20,
            phraseSnapCandidatesSeconds: new[] { 16.0, 32.0 },
            snapThresholdSeconds: 0.10);

        Assert.Equal(24.20, snapped, precision: 2);
    }

    [Fact]
    public void ComputeFlowHarmonicCompatibility_MapsAdjacentCamelotToLock()
    {
        var score = WorkstationViewModel.ComputeFlowHarmonicCompatibility("8A", "9A", semitoneShift: 0);

        Assert.Equal("lock", score.Label);
        Assert.True(score.Score >= 90);
    }

    [Fact]
    public void ComputeFlowHarmonicCompatibility_MapsDistantCamelotToRisky()
    {
        var score = WorkstationViewModel.ComputeFlowHarmonicCompatibility("1A", "8B", semitoneShift: 0);

        Assert.Equal("risky", score.Label);
        Assert.True(score.Score < 45);
    }

    [Fact]
    public void ComputeFlowEnergyCompatibility_MapsSmallDeltaToSmooth()
    {
        var score = WorkstationViewModel.ComputeFlowEnergyCompatibility(0.55, 0.60, transitionLengthSeconds: 8.0);

        Assert.Equal("smooth", score.Label);
        Assert.True(score.Score >= 90);
    }

    [Fact]
    public void ComputeFlowEnergyCompatibility_MapsLargeDeltaToMismatch()
    {
        var score = WorkstationViewModel.ComputeFlowEnergyCompatibility(0.10, 0.80, transitionLengthSeconds: 6.0);

        Assert.Equal("mismatch", score.Label);
        Assert.True(score.Score <= 30);
    }

    [Fact]
    public void RankFlowPresetSuggestions_PrefersCrossfadeOverFullForRiskyMismatch()
    {
        var catalog = WorkstationViewModel.BuildFlowTransitionPresetCatalog();
        var harmonic = new FlowCompatibilityScore(34, "risky");
        var energy = new FlowCompatibilityScore(26, "mismatch");

        var ranked = WorkstationViewModel.RankFlowPresetSuggestions(
            catalog,
            harmonic,
            energy,
            phraseAligned: false,
            energyDelta: 0.55,
            topN: 3);

        Assert.NotEmpty(ranked);
        Assert.Equal("crossfade", ranked[0]);
        Assert.DoesNotContain("full", ranked);
    }

    [Fact]
    public void BuildFlowTransitions_IsPresetApplied_FalseWhenNoOverride()
    {
        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: null,
            lengthOverrides: null,
            presetOverrides: null);

        var overlay = Assert.Single(overlays);
        Assert.False(overlay.IsPresetApplied);
        Assert.Null(overlay.AppliedPresetId);
    }

    [Fact]
    public void BuildFlowTransitions_IsPresetApplied_TrueWhenOverrideSet()
    {
        var presetOverrides = new Dictionary<string, string>
        {
            ["alpha->beta"] = "crossfade"
        };

        var overlays = WorkstationViewModel.BuildFlowTransitions(
            new[]
            {
                CreateLoadedDeck("A", "alpha", "Alpha", 124.0, "8A"),
                CreateLoadedDeck("B", "beta", "Beta", 124.4, "9A")
            },
            timelineOffsetSeconds: 0,
            timelineWindowSeconds: 60,
            selectedTransitionKey: null,
            lengthOverrides: null,
            presetOverrides: presetOverrides);

        var overlay = Assert.Single(overlays);
        Assert.True(overlay.IsPresetApplied);
        Assert.Equal("crossfade", overlay.AppliedPresetId);
    }

    [Fact]
    public void BuildFlowTransitionPresetCatalog_CrossfadeDefaultLength_IsEightSeconds()
    {
        var catalog = WorkstationViewModel.BuildFlowTransitionPresetCatalog();
        var crossfade = catalog.FirstOrDefault(p => p.PresetId == "crossfade");

        Assert.NotNull(crossfade);
        Assert.Equal(8.0, crossfade.DefaultLengthSeconds, precision: 1);
    }

    [Fact]
    public void BuildFlowTransitionPresetCatalog_BassSwapDefaultLength_IsTenSeconds()
    {
        var catalog = WorkstationViewModel.BuildFlowTransitionPresetCatalog();
        var bassSwap = catalog.FirstOrDefault(p => p.PresetId == "bass-swap");

        Assert.NotNull(bassSwap);
        Assert.Equal(10.0, bassSwap.DefaultLengthSeconds, precision: 1);
    }

    [Fact]
    public void BuildHarmonicSuggestionText_ReportsLockForCompatibleKeys()
    {
        var text = WorkstationDeckViewModel.BuildHarmonicSuggestionText("8A", "9A", 0);

        Assert.Contains("Harmonic", text);
        Assert.Contains("blend", text);
    }

    [Fact]
    public void BuildHarmonicSuggestionText_IncludesShiftWhenDeckIsTransposed()
    {
        var text = WorkstationDeckViewModel.BuildHarmonicSuggestionText("8A", null, 2);

        Assert.Contains("+2 st", text);
    }

    [Fact]
    public void IsTrackReadyForWorkstation_RequiresLocalFileAndPrepData()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var missingPrep = new PlaylistTrack
            {
                Status = TrackStatus.Downloaded,
                ResolvedFilePath = tempFile,
                Artist = "Artist",
                Title = "Unprepared"
            };

            var ready = new PlaylistTrack
            {
                Status = TrackStatus.Downloaded,
                ResolvedFilePath = tempFile,
                Artist = "Artist",
                Title = "Prepared",
                TrackUniqueHash = "track-hash",
                BPM = 128,
                WaveformData = new byte[] { 1, 2, 3 },
                CuePointsJson = "[{\"Timestamp\":0.0,\"Name\":\"Intro\"}]"
            };

            Assert.False(WorkstationDeckViewModel.IsTrackReadyForWorkstation(missingPrep));
            Assert.True(WorkstationDeckViewModel.IsTrackReadyForWorkstation(ready));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task LoadPlaylistTrackCommand_DoesNotThrow_WhenFileIsMissing()
    {
        var separatorInner = new Mock<IStemSeparator>();
        separatorInner.SetupGet(x => x.Name).Returns("stub");
        separatorInner.SetupGet(x => x.IsAvailable).Returns(false);
        separatorInner.SetupGet(x => x.ModelTag).Returns("stub-model");

        var separator = new CachedStemSeparator(
            separatorInner.Object,
            new StemCacheService(new NullLogger<StemCacheService>()),
            new NullLogger<CachedStemSeparator>());

        var vm = new WorkstationDeckViewModel(
            "A",
            new DeckSlotViewModel("A", new DeckEngine()),
            separator,
            Mock.Of<ICuePointService>(),
            null!);

        var track = new PlaylistTrack
        {
            Status = TrackStatus.Downloaded,
            Artist = "Missing Artist",
            Title = "Missing Track",
            ResolvedFilePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp3"),
            BPM = 124,
            WaveformData = new byte[] { 1 }
        };

        var exception = await Record.ExceptionAsync(async () =>
            await vm.LoadPlaylistTrackCommand.Execute(track).FirstAsync());

        Assert.Null(exception);
        Assert.False(vm.IsLoaded);
        Assert.False(string.IsNullOrWhiteSpace(vm.TrackLoadError));
    }

    private static WorkstationDeckViewModel CreateLoadedDeck(string label, string trackHash, string trackTitle, double bpm, string key)
    {
        var separatorInner = new Mock<IStemSeparator>();
        separatorInner.SetupGet(x => x.Name).Returns("stub");
        separatorInner.SetupGet(x => x.IsAvailable).Returns(false);
        separatorInner.SetupGet(x => x.ModelTag).Returns("stub-model");

        var separator = new CachedStemSeparator(
            separatorInner.Object,
            new StemCacheService(new NullLogger<StemCacheService>()),
            new NullLogger<CachedStemSeparator>());

        var deckEngine = new DeckEngine();
        var deckSlot = new DeckSlotViewModel(label, deckEngine)
        {
            TrackBpm = bpm
        };

        var deck = new WorkstationDeckViewModel(
            label,
            deckSlot,
            separator,
            Mock.Of<ICuePointService>(),
            null!);

        deck.TrackTitle = trackTitle;
        deck.TrackArtist = "Artist";
        deck.TrackKey = key;
        deck.DisplayBpm = bpm;

        var loadedField = typeof(WorkstationDeckViewModel).GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
        loadedField?.SetValue(deck, true);
        var hashField = typeof(WorkstationDeckViewModel).GetField("_trackHash", BindingFlags.Instance | BindingFlags.NonPublic);
        hashField?.SetValue(deck, trackHash);

        return deck;
    }

    [Fact]
    public void BuildPlaylistFlowTransitions_ReturnsOnePerConsecutivePair()
    {
        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), TrackNumber = 1, Title = "Alpha", MusicalKey = "8A", BPM = 128, Energy = 0.7, CanonicalDuration = 200_000 },
            new() { Id = Guid.NewGuid(), TrackNumber = 2, Title = "Beta",  MusicalKey = "9A", BPM = 130, Energy = 0.72, CanonicalDuration = 200_000 },
            new() { Id = Guid.NewGuid(), TrackNumber = 3, Title = "Gamma", MusicalKey = "1B", BPM = 140, Energy = 0.5,  CanonicalDuration = 200_000 },
        };

        var result = WorkstationViewModel.BuildPlaylistFlowTransitions(tracks, 0, 900);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, t => t.TransitionLabel.StartsWith("Alpha"));
        Assert.Contains(result, t => t.TransitionLabel.StartsWith("Beta"));
    }

    [Fact]
    public void BuildPlaylistFlowTransitions_CompatibilityColor_GreenForLock()
    {
        var tracks = new List<PlaylistTrack>
        {
            new() { Id = Guid.NewGuid(), TrackNumber = 1, Title = "A", MusicalKey = "8A", BPM = 128, Energy = 0.65, CanonicalDuration = 200_000 },
            new() { Id = Guid.NewGuid(), TrackNumber = 2, Title = "B", MusicalKey = "9A", BPM = 128, Energy = 0.65, CanonicalDuration = 200_000 },
        };

        var result = WorkstationViewModel.BuildPlaylistFlowTransitions(tracks, 0, 900);

        Assert.Single(result);
        // Adjacent Camelot keys (8A→9A) and matched energy → combined ≥ 80 → green color
        Assert.Equal("#66B8E986", result[0].CompatibilityColor);
    }
}

file sealed class DelegateUndoOperation : IUndoableOperation
{
    private readonly Action _execute;
    private readonly Action _undo;

    public DelegateUndoOperation(Action execute, Action undo, string description = "test op")
    {
        _execute = execute;
        _undo = undo;
        Description = description;
    }

    public string Description { get; }
    public void Execute() => _execute();
    public void Undo() => _undo();
}
