using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Similarity;

namespace SLSKDONET.ViewModels;

public sealed class LibraryTrackInspectorViewModel
{
    private readonly LibraryViewModel _library;
    private readonly ILogger _logger;
    private readonly SimilarityIndex? _similarityIndex;

    public LibraryTrackInspectorViewModel(
        LibraryViewModel library,
        ILogger logger,
        SimilarityIndex? similarityIndex = null)
    {
        _library = library;
        _logger = logger;
        _similarityIndex = similarityIndex;
    }

    public string? TrackExplainabilitySummary { get; private set; }

    public IReadOnlyList<string> TrackExplainabilityReasons { get; private set; } = Array.Empty<string>();

    public bool IsTrackExplainabilityVisible =>
        !string.IsNullOrWhiteSpace(TrackExplainabilitySummary) &&
        TrackExplainabilityReasons.Count > 0;

    public ObservableCollection<PlaylistTrackViewModel> SimilarTracksPreview { get; } = new();

    public bool HasSimilarTracksPreview => SimilarTracksPreview.Count > 0;

    public void ClearEnhancements()
    {
        TrackExplainabilitySummary = null;
        TrackExplainabilityReasons = Array.Empty<string>();
        SimilarTracksPreview.Clear();
    }

    public async Task TryAttachEnhancementsAsync(PlaylistTrackViewModel selected)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(ClearEnhancements);

            // Ensure the analysis/technical data is fully loaded from database
            await selected.LoadAnalysisDataAsync();

            if (!selected.HasAnalysisData || string.IsNullOrWhiteSpace(selected.GlobalId))
                return;

            var index = _similarityIndex;

            var explainabilitySummary = BuildTrackExplainabilitySummary(selected);
            var explainabilityReasons = ParseReasonTags(selected.InspectorA10ReasonTags);

            var previewTracks = new List<PlaylistTrackViewModel>();
            if (index is not null)
            {
                var similar = await index.GetSimilarTracksAsync(selected.GlobalId, topN: 5).ConfigureAwait(false);

                var trackPool = _library.Tracks.FilteredTracks
                    .Concat(_library.Tracks.CurrentProjectTracks)
                    .GroupBy(track => track.GlobalId)
                    .ToDictionary(group => group.Key, group => group.First());

                foreach (var hit in similar)
                {
                    if (string.Equals(hit.TrackHash, selected.GlobalId, StringComparison.Ordinal))
                        continue;

                    if (trackPool.TryGetValue(hit.TrackHash, out var vm))
                        previewTracks.Add(vm);

                    if (previewTracks.Count >= 5)
                        break;
                }
            }

            if (_library.Tracks.SelectedTracks.Count != 1 || !ReferenceEquals(_library.Tracks.SelectedTracks.FirstOrDefault(), selected))
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                TrackExplainabilitySummary = explainabilitySummary;
                TrackExplainabilityReasons = explainabilityReasons;

                SimilarTracksPreview.Clear();
                foreach (var vm in previewTracks)
                    SimilarTracksPreview.Add(vm);
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to populate track inspector explainability and similar preview for {TrackHash}", selected.GlobalId);
            await Dispatcher.UIThread.InvokeAsync(ClearEnhancements);
        }
    }

    private static string? BuildTrackExplainabilitySummary(PlaylistTrackViewModel selected)
    {
        if (selected.HasInspectorA10PairwiseContext && !string.IsNullOrWhiteSpace(selected.InspectorA10PairContextLabel))
        {
            return $"{selected.InspectorA10PairContextLabel} shows blend-safe context around this track.";
        }

        if (selected.HasBpm && !string.IsNullOrWhiteSpace(selected.CamelotDisplay) && !string.IsNullOrWhiteSpace(selected.EnergyRating))
        {
            return $"Tempo {selected.BpmDisplay}, key {selected.CamelotDisplay}, and energy {selected.EnergyRating} make this track transition-ready.";
        }

        return null;
    }

    private static IReadOnlyList<string> ParseReasonTags(string? reasonTags)
    {
        if (string.IsNullOrWhiteSpace(reasonTags))
            return Array.Empty<string>();

        return reasonTags
            .Split('•', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Take(5)
            .ToArray();
    }
}