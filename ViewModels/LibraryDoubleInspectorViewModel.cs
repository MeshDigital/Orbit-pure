using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.Similarity;

namespace SLSKDONET.ViewModels;

public sealed class LibraryDoubleInspectorViewModel
{
    private readonly LibraryViewModel _library;
    private readonly ILogger _logger;
    private readonly TrackSimilarityService? _trackSimilarityService;
    private readonly TransitionStyleClassifier? _transitionStyleClassifier;

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
    }

    public async Task HandleSelectionChangedAsync(IReadOnlyList<PlaylistTrackViewModel> selectedTracks)
    {
        if (selectedTracks.Count == 2)
        {
            await TryAttachPairwiseContextAsync(selectedTracks[0], selectedTracks[1]).ConfigureAwait(false);
            return;
        }

        IsPairScoreLoading = false;
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
    }

    private async Task TryAttachPairwiseContextAsync(PlaylistTrackViewModel trackA, PlaylistTrackViewModel trackB)
    {
        try
        {
            IsPairScoreLoading = true;
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
            IsPairScoreLoading = false;
        }
    }
}
