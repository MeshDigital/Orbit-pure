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
/// Toolbar, deck, flow, transport, and mix-coach status summaries and property notifications (#307).
/// </summary>
public sealed partial class WorkstationViewModel
{
    public static string BuildDeckStatusSummary(int loadedDecks, int totalDecks, string? focusedDeckLabel, double masterBpm)
    {
        var focus = string.IsNullOrWhiteSpace(focusedDeckLabel) ? "Focus —" : $"Focus {focusedDeckLabel}";
        var bpmText = masterBpm > 0 ? $"{masterBpm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} BPM" : "Master BPM pending";
        return $"{loadedDecks}/{Math.Max(1, totalDecks)} decks live • {focus} • {bpmText}";
    }

    public static IReadOnlyList<PlaylistFlowTransitionViewModel> BuildPlaylistFlowTransitions(
        IEnumerable<PlaylistTrack> tracks,
        double timelineOffsetSeconds,
        double timelineWindowSeconds)
    {
        const double canvasWidth = 700.0;
        const double defaultDurationSeconds = 180.0;
        const double transitionWindowSeconds = 8.0;

        var result = new List<PlaylistFlowTransitionViewModel>();

        if (timelineWindowSeconds <= 0)
        {
            return result;
        }

        var ordered = (tracks ?? Enumerable.Empty<PlaylistTrack>())
            .OrderBy(t => t.TrackNumber)
            .ThenBy(t => t.SortOrder)
            .ToList();

        if (ordered.Count < 2)
        {
            return result;
        }

        // Build cumulative start positions from durations
        var positions = new double[ordered.Count];
        positions[0] = 0;
        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var dur = prev.CanonicalDuration.HasValue
                ? Math.Max(30.0, prev.CanonicalDuration.Value / 1000.0)
                : defaultDurationSeconds;
            positions[i] = positions[i - 1] + dur;
        }

        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var source = ordered[i];
            var target = ordered[i + 1];

            var transitionPoint = positions[i + 1];
            var blockStart = transitionPoint - transitionWindowSeconds;
            var blockEnd = transitionPoint + transitionWindowSeconds * 0.5;

            var left = ToCanvasX(blockStart, timelineOffsetSeconds, timelineWindowSeconds, canvasWidth);
            var right = ToCanvasX(blockEnd, timelineOffsetSeconds, timelineWindowSeconds, canvasWidth);
            var blockWidth = Math.Max(4.0, right - left);

            var sourceEnergy = source.Energy ?? EstimateNormalizedEnergyFromBpm(source.BPM ?? 0);
            var targetEnergy = target.Energy ?? EstimateNormalizedEnergyFromBpm(target.BPM ?? 0);
            var harmonicScore = ComputeFlowHarmonicCompatibility(source.MusicalKey, target.MusicalKey, semitoneShift: 0);
            var energyScore = ComputeFlowEnergyCompatibility(sourceEnergy, targetEnergy, transitionWindowSeconds);
            var combined = ComputeCombinedFlowCompatibilityScore(harmonicScore.Score, energyScore.Score);

            var sourceTitle = !string.IsNullOrWhiteSpace(source.Title) ? source.Title : $"Track {i + 1}";
            var targetTitle = !string.IsNullOrWhiteSpace(target.Title) ? target.Title : $"Track {i + 2}";
            var key = $"pl:{source.Id}->{target.Id}";

            result.Add(new PlaylistFlowTransitionViewModel(
                key,
                $"{sourceTitle} → {targetTitle}",
                left,
                blockWidth,
                harmonicScore.Score,
                harmonicScore.Label,
                energyScore.Score,
                energyScore.Label,
                combined,
                i));
        }

        return result;
    }

    public static string BuildDeckFocusSummary(IEnumerable<string> deckLabels, IEnumerable<string> liveDeckLabels, string? focusedDeckLabel)
    {
        var live = new HashSet<string>(liveDeckLabels ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var ordered = (deckLabels ?? Enumerable.Empty<string>()).Take(4).ToList();
        if (ordered.Count == 0)
        {
            return "Deck targets pending • add or load a deck to start routing";
        }

        var labels = ordered.Select(label =>
        {
            var state = live.Contains(label)
                ? (string.Equals(label, focusedDeckLabel, StringComparison.OrdinalIgnoreCase) ? "focused" : "live")
                : "open";
            return $"{label} {state}";
        });

        return $"Deck targets • {string.Join(" • ", labels)}";
    }

    public static string BuildPlaylistFlowSummary(string? playlistTitle, int readyTrackCount, int liveDeckCount, WorkstationMode activeMode)
    {
        var title = string.IsNullOrWhiteSpace(playlistTitle) ? "No playlist selected" : playlistTitle;
        var mode = activeMode switch
        {
            WorkstationMode.Flow => "set plan active",
            _ => "prep active"
        };

        return $"{title} • {Math.Max(0, readyTrackCount)} flow-ready track{(readyTrackCount == 1 ? string.Empty : "s")} • {Math.Max(0, liveDeckCount)} live deck{(liveDeckCount == 1 ? string.Empty : "s")} • {mode}";
    }

    public static string BuildAnalysisQueueSummary(int queuedCount, int processedCount, string? currentTrackHash, bool isPaused, string? performanceMode, int maxConcurrency)
    {
        var mode = string.IsNullOrWhiteSpace(performanceMode) ? "Standard" : performanceMode;
        var concurrency = maxConcurrency > 0 ? $"{maxConcurrency} lane{(maxConcurrency == 1 ? string.Empty : "s")}" : "auto lanes";

        if (queuedCount <= 0 && string.IsNullOrWhiteSpace(currentTrackHash))
        {
            return $"Analysis idle • {processedCount} prepped • {mode} • {concurrency}";
        }

        if (isPaused)
        {
            return $"Analysis paused • {queuedCount} queued • {processedCount} prepped • {mode}";
        }

        return $"Analysis rolling • {queuedCount} queued • {processedCount} prepped • {mode} • {concurrency}";
    }

    public static string BuildToolbarHint(WorkstationMode activeMode, bool snapEnabled, bool quantizeEnabled)
    {
        var modeLabel = activeMode switch
        {
            WorkstationMode.Flow => "Set Plan",
            _ => "Prep"
        };

        return $"{modeLabel} mode • Snap {(snapEnabled ? "on" : "off")} • Quantize {(quantizeEnabled ? "on" : "off")} • F1 shortcuts";
    }

    public static string BuildActiveToolSummary(string? toolName, string? statusText)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return "Cockpit tools • select a workstation tool to change inspector and overlay context";
        }

        return $"Cockpit tool • {toolName} • {statusText}";
    }

    public static string BuildFlowWindowSummary(double timelineOffsetSeconds, double timelineWindowSeconds)
    {
        var start = FormatTick(timelineOffsetSeconds);
        var end = FormatTick(timelineOffsetSeconds + Math.Max(0, timelineWindowSeconds));
        return $"Viewport {start} → {end} • {timelineWindowSeconds:F0}s window";
    }

    public static string BuildTransportStatusSummary(bool isPlaying, int loadedDecks, string? focusedDeckLabel, bool loopArmed)
    {
        var state = isPlaying ? "Live transport" : "Transport cued";
        var deckText = $"{Math.Max(0, loadedDecks)} deck{(loadedDecks == 1 ? string.Empty : "s")} {(isPlaying ? "rolling" : "ready")}";
        var focus = string.IsNullOrWhiteSpace(focusedDeckLabel) ? "Focus —" : $"Focus {focusedDeckLabel}";
        var loop = loopArmed ? "loop armed" : "loop open";
        return $"{state} • {deckText} • {focus} • {loop}";
    }

    public static string BuildGlobalTransportSummary(bool isPlaying, int loadedDecks)
    {
        var state = isPlaying ? "Live transport" : "Transport cued";
        var deckText = $"{Math.Max(0, loadedDecks)} deck{(loadedDecks == 1 ? string.Empty : "s")} {(isPlaying ? "rolling" : "ready")}";
        return $"{state} • {deckText}";
    }

    public static string BuildFocusedDeckActionSummary(string? focusedDeckLabel, bool isLoaded, bool hasJumpCues, bool stemsReady)
    {
        if (string.IsNullOrWhiteSpace(focusedDeckLabel) || !isLoaded)
            return "Focus a live deck to sync, jump cues, and shape stems";

        var cueText = hasJumpCues ? "jump cues ready" : "prep cues next";
        var stemText = stemsReady ? "stems live" : "separate stems next";
        return $"Deck {focusedDeckLabel} • {cueText} • {stemText}";
    }

    public static string BuildMixCoachSummary(string? focusedDeckLabel, string? harmonicHint, string? transitionHint)
    {
        if (string.IsNullOrWhiteSpace(focusedDeckLabel))
            return "Mix coach • focus a loaded deck for harmonic and transition guidance";

        var harmonic = string.IsNullOrWhiteSpace(harmonicHint) ? "harmonic guidance pending" : harmonicHint;
        var transition = string.IsNullOrWhiteSpace(transitionHint) ? "transition guidance pending" : transitionHint;
        return $"Mix coach • Deck {focusedDeckLabel} • {harmonic} • {transition}";
    }

    private void RaiseHeaderProperties()
    {
        RefreshExportInspector();
        this.RaisePropertyChanged(nameof(DeckStatusSummary));
        this.RaisePropertyChanged(nameof(DeckFocusSummary));
        this.RaisePropertyChanged(nameof(ActivePlaylistFlowSummary));
        this.RaisePropertyChanged(nameof(HasLoadedDecks));
        this.RaisePropertyChanged(nameof(AnalysisQueueSummary));
        this.RaisePropertyChanged(nameof(ToolbarHint));
        this.RaisePropertyChanged(nameof(FlowWindowSummary));
        this.RaisePropertyChanged(nameof(FlowTransitions));
        this.RaisePropertyChanged(nameof(HasFlowTransitions));
        this.RaisePropertyChanged(nameof(IsFlowOverlayVisible));
        this.RaisePropertyChanged(nameof(FlowOverlayHint));
        this.RaisePropertyChanged(nameof(FlowPlaylistTransitions));
        this.RaisePropertyChanged(nameof(HasFlowPlaylistTransitions));
        this.RaisePropertyChanged(nameof(IsFlowPlaylistOverlayVisible));
        RaiseFlowSelectionProperties();
        this.RaisePropertyChanged(nameof(GlobalTransportSummary));
        this.RaisePropertyChanged(nameof(TransportStatusSummary));
        this.RaisePropertyChanged(nameof(FocusedDeckActionSummary));
        this.RaisePropertyChanged(nameof(MixCoachSummary));
        this.RaisePropertyChanged(nameof(ActiveToolOption));
        this.RaisePropertyChanged(nameof(ActiveToolSummary));
        RaiseLaneActionProperties();
    }

    private void RaiseLaneActionProperties()
    {
        this.RaisePropertyChanged(nameof(HasActivePlaylist));
        this.RaisePropertyChanged(nameof(ReadyPlaylistTrackCount));
        this.RaisePropertyChanged(nameof(PlaylistAttentionTrackCount));
        this.RaisePropertyChanged(nameof(HasPlaylistAttention));
        this.RaisePropertyChanged(nameof(IsPlaylistReadyForExport));
        this.RaisePropertyChanged(nameof(AnalysisWorkflowStatus));
        this.RaisePropertyChanged(nameof(PreparationWorkflowStatus));
        this.RaisePropertyChanged(nameof(ExportWorkflowStatus));
        this.RaisePropertyChanged(nameof(HasReadyPlaylistTracks));
        this.RaisePropertyChanged(nameof(HasLoadedDecks));
        this.RaisePropertyChanged(nameof(ShouldShowSelectPlaylistCta));
        this.RaisePropertyChanged(nameof(ShouldShowAcquireCtas));
        this.RaisePropertyChanged(nameof(ShouldShowDownloadTracksCta));
        this.RaisePropertyChanged(nameof(ShouldShowImportLocalFilesCta));
        this.RaisePropertyChanged(nameof(ShouldShowReadyTrackCtas));
        this.RaisePropertyChanged(nameof(FlowCtaStateSummary));
        this.RaisePropertyChanged(nameof(TimelineEmptyCanvasSummary));
        this.RaisePropertyChanged(nameof(FlowLaneReadinessSummary));
        this.RaisePropertyChanged(nameof(FlowTransitions));
        this.RaisePropertyChanged(nameof(HasFlowTransitions));
        this.RaisePropertyChanged(nameof(IsFlowOverlayVisible));
        this.RaisePropertyChanged(nameof(FlowOverlayHint));
        this.RaisePropertyChanged(nameof(FlowPlaylistTransitions));
        this.RaisePropertyChanged(nameof(HasFlowPlaylistTransitions));
        this.RaisePropertyChanged(nameof(IsFlowPlaylistOverlayVisible));
        RaiseFlowSelectionProperties();
        this.RaisePropertyChanged(nameof(SelectPlaylistCtaHint));
        this.RaisePropertyChanged(nameof(DownloadTracksCtaHint));
        this.RaisePropertyChanged(nameof(ImportLocalFilesCtaHint));
        this.RaisePropertyChanged(nameof(CanAnalyzePlaylistFromLane));
        this.RaisePropertyChanged(nameof(CanOpenTrackOverlayFromLane));
        this.RaisePropertyChanged(nameof(CanUseFocusedDeckLaneActions));
        this.RaisePropertyChanged(nameof(CanUseExportLaneActions));
        this.RaisePropertyChanged(nameof(FlowLaneAnalyzeHint));
        this.RaisePropertyChanged(nameof(FlowLaneOverlayHint));
        this.RaisePropertyChanged(nameof(StemsLaneActionHint));
        this.RaisePropertyChanged(nameof(ExportLaneActionHint));
    }

    private void RefreshExportInspector()
    {
        ExportPanel.SetDecks(Decks);
    }

    private void SynchronizeToolSelection()
    {
        foreach (var option in ToolOptions)
        {
            option.IsSelected = option.Mode == ActiveMode;
        }

        this.RaisePropertyChanged(nameof(ActiveToolOption));
        this.RaisePropertyChanged(nameof(ActiveToolSummary));
    }

    private void HideSnapGuide()
    {
        IsSnapGuideVisible = false;
        SnapGuideLabel = string.Empty;
    }

    private static string FormatTick(double seconds)
    {
        var s = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)s.TotalMinutes}:{s.Seconds:00}";
    }

    private void RaiseTimelineTickLabels()
    {
        this.RaisePropertyChanged(nameof(TimelineTick0));
        this.RaisePropertyChanged(nameof(TimelineTick1));
        this.RaisePropertyChanged(nameof(TimelineTick2));
        this.RaisePropertyChanged(nameof(TimelineTick3));
        this.RaisePropertyChanged(nameof(TimelineTick4));
        this.RaisePropertyChanged(nameof(TimelineTick5));
        this.RaisePropertyChanged(nameof(FlowTransitions));
        this.RaisePropertyChanged(nameof(HasFlowTransitions));
        this.RaisePropertyChanged(nameof(IsFlowOverlayVisible));
        this.RaisePropertyChanged(nameof(FlowOverlayHint));
        RaiseFlowSelectionProperties();
    }

    private void RaiseFlowSelectionProperties()
    {
        this.RaisePropertyChanged(nameof(FlowTransitions));
        this.RaisePropertyChanged(nameof(SelectedFlowTransition));
        this.RaisePropertyChanged(nameof(HasSelectedFlowTransition));
        this.RaisePropertyChanged(nameof(FlowInspectorTransitionLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorCompatibilityLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorPhraseAlignment));
        this.RaisePropertyChanged(nameof(FlowInspectorBeatAlignment));
        this.RaisePropertyChanged(nameof(FlowInspectorLengthLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorSnapLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorPhraseMarkerLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorPhraseRegionCount));
        this.RaisePropertyChanged(nameof(FlowInspectorPhraseRegionSpanSeconds));
        this.RaisePropertyChanged(nameof(FlowInspectorPhraseRegionProvenanceLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorPhraseRegionSpanLabel));
        this.RaisePropertyChanged(nameof(FlowTransitionPresetCatalog));
        this.RaisePropertyChanged(nameof(FlowInspectorPresetLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorSuggestedPresetsLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorHarmonicScoreLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorEnergyScoreLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorCombinedScoreLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorWarningLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorTopSuggestedPresetId));
        this.RaisePropertyChanged(nameof(FlowInspectorCurveLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorBandStrategyLabel));
        this.RaisePropertyChanged(nameof(FlowInspectorHasWarnings));
        this.RaisePropertyChanged(nameof(FlowInspectorScoreRow));
        this.RaisePropertyChanged(nameof(FlowInspectorAlignmentRow));
        this.RaisePropertyChanged(nameof(FlowInspectorCurveDetailRow));
    }

}
