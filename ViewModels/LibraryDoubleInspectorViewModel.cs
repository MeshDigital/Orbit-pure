using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.Similarity;

namespace SLSKDONET.ViewModels;

/// <summary>
/// The panel bound to this VM (<c>DoubleInspectorPanel.axaml</c>) is only ever re-shown by a fresh
/// <see cref="SLSKDONET.Events.OpenInspectorEvent"/> when selection settles at exactly 2 tracks —
/// going from a valid pair to a 1-, 3+-, or 0-track selection does NOT re-fire that event, so
/// without live change notifications the panel kept showing the last real pair's transition data
/// as if it still applied. Implements <see cref="INotifyPropertyChanged"/> (this class previously
/// had none at all, despite the view binding directly to its properties) so
/// <see cref="ClearPairwiseContext"/>/<see cref="SetPairwiseContext"/>/selection-driven property
/// changes actually reach the already-open panel instead of only taking effect on the next
/// re-navigation.
/// </summary>
public sealed class LibraryDoubleInspectorViewModel : INotifyPropertyChanged
{
    private readonly LibraryViewModel _library;
    private readonly ILogger _logger;
    private readonly TrackSimilarityService? _trackSimilarityService;
    private readonly TransitionStyleClassifier? _transitionStyleClassifier;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Raises change notification for every property derived from the current selection
    /// (TrackA/TrackB and everything computed from them) — called whenever selection settles,
    /// since none of those are backed by a field this class controls the setter of.</summary>
    private void RaiseSelectionDerivedPropertiesChanged()
    {
        RaisePropertyChanged(nameof(TrackA));
        RaisePropertyChanged(nameof(TrackB));
        RaisePropertyChanged(nameof(IsPairAnalyzable));
        RaisePropertyChanged(nameof(HeaderTitle));
        RaisePropertyChanged(nameof(KeyCompatibilitySummary));
        RaisePropertyChanged(nameof(BpmDifferenceSummary));
        RaisePropertyChanged(nameof(EnergyAlignmentSummary));
    }

    public LibraryDoubleInspectorViewModel(
        LibraryViewModel library,
        ILogger logger,
        TrackSimilarityService? trackSimilarityService = null,
        TransitionStyleClassifier? transitionStyleClassifier = null)
    {
        _library = library;
        _logger = logger;
        _trackSimilarityService = trackSimilarityService;
        _transitionStyleClassifier = transitionStyleClassifier;
    }

    internal LibraryViewModel Library => _library;

    public PlaylistTrackViewModel? TrackA => _library.Tracks.SelectedTracks.ElementAtOrDefault(0);

    public PlaylistTrackViewModel? TrackB => _library.Tracks.SelectedTracks.ElementAtOrDefault(1);

    public bool IsPairAnalyzable =>
        TrackA?.HasAnalysisData == true &&
        TrackB?.HasAnalysisData == true;

    public bool IsPairScoreLoading { get; private set; }

    public bool HasPairContext { get; private set; }

    public double TransitionScore { get; private set; }

    public double HarmonicScore { get; private set; }

    public double BeatScore { get; private set; }

    public double DropScore { get; private set; }

    public string ReasonTags { get; private set; } = string.Empty;

    public string TransitionStyleLabel { get; private set; } = string.Empty;

    public string TransitionStyleReason { get; private set; } = string.Empty;

    public string HeaderTitle
    {
        get
        {
            var trackA = TrackA;
            var trackB = TrackB;
            if (trackA is null || trackB is null)
                return "Track A -> Track B";

            return $"{trackA.TrackTitle} -> {trackB.TrackTitle}";
        }
    }

    public string KeyCompatibilitySummary =>
        LibraryViewModel.BuildCamelotCompatibilityLabel(TrackA?.CamelotDisplay, TrackB?.CamelotDisplay);

    public ICommand AnalyzeSelectedPairCommand => _library.AnalyzeSelectedPairCommand;

    public ICommand FavoriteSelectedPairAsDoubleCommand => _library.FavoriteSelectedPairAsDoubleCommand;

    public ICommand OpenSelectedPairInWorkstationCommand => _library.OpenSelectedPairInWorkstationCommand;

    public string BpmDifferenceSummary
    {
        get
        {
            var trackA = TrackA;
            var trackB = TrackB;
            if (trackA is null || trackB is null || !trackA.HasBpm || !trackB.HasBpm)
                return "BPM gap: Analyze both tracks";

            var delta = Math.Abs(trackA.BPM - trackB.BPM);
            var verdict = delta switch
            {
                <= 2.0 => "tight",
                <= 6.0 => "blendable",
                <= 10.0 => "tempo shift needed",
                _ => "wide gap"
            };

            return $"BPM delta: {delta:0.0} ({verdict})";
        }
    }

    public string EnergyAlignmentSummary
    {
        get
        {
            var trackA = TrackA;
            var trackB = TrackB;
            if (trackA is null || trackB is null)
                return "Energy alignment: Select two tracks";

            var delta = Math.Abs(trackA.Energy - trackB.Energy);
            var verdict = delta switch
            {
                <= 0.08 => "aligned",
                <= 0.2 => "workable",
                <= 0.35 => "noticeable lift",
                _ => "high contrast"
            };

            return $"Energy delta: {delta:0.00} ({verdict})";
        }
    }

    public void ClearPairwiseContext()
    {
        TransitionScore = 0;
        HarmonicScore = 0;
        BeatScore = 0;
        DropScore = 0;
        ReasonTags = string.Empty;
        TransitionStyleLabel = string.Empty;
        TransitionStyleReason = string.Empty;
        HasPairContext = false;

        RaisePropertyChanged(nameof(TransitionScore));
        RaisePropertyChanged(nameof(HarmonicScore));
        RaisePropertyChanged(nameof(BeatScore));
        RaisePropertyChanged(nameof(DropScore));
        RaisePropertyChanged(nameof(ReasonTags));
        RaisePropertyChanged(nameof(TransitionStyleLabel));
        RaisePropertyChanged(nameof(TransitionStyleReason));
        RaisePropertyChanged(nameof(HasPairContext));
    }

    public async Task HandleSelectionChangedAsync(IReadOnlyList<PlaylistTrackViewModel> selectedTracks)
    {
        RaiseSelectionDerivedPropertiesChanged();

        if (selectedTracks.Count == 2)
        {
            await TryAttachPairwiseContextAsync(selectedTracks[0], selectedTracks[1]).ConfigureAwait(false);
            return;
        }

        IsPairScoreLoading = false;
        RaisePropertyChanged(nameof(IsPairScoreLoading));
        ClearPairwiseContext();
    }

    private void SetPairwiseContext(
        double transitionScore,
        double harmonicScore,
        double beatScore,
        double dropScore,
        string reasonTags,
        string? transitionStyleLabel = null,
        string? transitionStyleReason = null)
    {
        TransitionScore = transitionScore;
        HarmonicScore = harmonicScore;
        BeatScore = beatScore;
        DropScore = dropScore;
        ReasonTags = reasonTags;
        TransitionStyleLabel = transitionStyleLabel ?? string.Empty;
        TransitionStyleReason = transitionStyleReason ?? string.Empty;
        HasPairContext = true;

        RaisePropertyChanged(nameof(TransitionScore));
        RaisePropertyChanged(nameof(HarmonicScore));
        RaisePropertyChanged(nameof(BeatScore));
        RaisePropertyChanged(nameof(DropScore));
        RaisePropertyChanged(nameof(ReasonTags));
        RaisePropertyChanged(nameof(TransitionStyleLabel));
        RaisePropertyChanged(nameof(TransitionStyleReason));
        RaisePropertyChanged(nameof(HasPairContext));
    }

    private async Task TryAttachPairwiseContextAsync(PlaylistTrackViewModel trackA, PlaylistTrackViewModel trackB)
    {
        try
        {
            IsPairScoreLoading = true;
            RaisePropertyChanged(nameof(IsPairScoreLoading));
            ClearPairwiseContext();

            if (string.IsNullOrWhiteSpace(trackA.GlobalId) || string.IsNullOrWhiteSpace(trackB.GlobalId))
                return;

            var similarity = _trackSimilarityService;
            if (similarity is null)
                return;

            var snapshot = await similarity.BuildSnapshotAsync(
                trackA.GlobalId,
                trackB.GlobalId,
                TrackSimilarityProfile.BlendSafe).ConfigureAwait(false);

            if (snapshot is null)
                return;

            if (_library.Tracks.SelectedTracks.Count != 2 ||
                !ReferenceEquals(_library.Tracks.SelectedTracks.ElementAtOrDefault(0), trackA) ||
                !ReferenceEquals(_library.Tracks.SelectedTracks.ElementAtOrDefault(1), trackB))
            {
                return;
            }

            var transitionStyleLabel = string.Empty;
            var transitionStyleReason = string.Empty;

            var classifier = _transitionStyleClassifier;
            if (classifier is not null)
            {
                var style = classifier.Classify(
                    snapshot.Left,
                    snapshot.Right,
                    snapshot.Result,
                    snapshot.LeftSections,
                    snapshot.RightSections);

                transitionStyleLabel = style.Label;
                transitionStyleReason = style.Reason;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SetPairwiseContext(
                    snapshot.Result.FinalSimilarity,
                    snapshot.Result.VectorScores.Harmonic,
                    snapshot.Result.VectorScores.Rhythm,
                    snapshot.Result.SegmentScores.Drop,
                    string.Join(" • ", snapshot.Result.ReasonTags.Take(3)),
                    transitionStyleLabel,
                    transitionStyleReason);
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to compute double inspector A10 context for selected pair");
        }
        finally
        {
            // Mirrors the SetPairwiseContext call above: the preceding awaits use
            // ConfigureAwait(false), so this can resume on a background thread — mutate/raise on
            // the UI thread like every other property change in this class.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsPairScoreLoading = false;
                RaisePropertyChanged(nameof(IsPairScoreLoading));
            });
        }
    }
}
