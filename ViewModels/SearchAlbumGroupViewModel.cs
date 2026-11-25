using System;
using System.Collections.Generic;
using System.Linq;
using ReactiveUI;

namespace SLSKDONET.ViewModels;

/// <summary>One track inside an album group: the best-ranked match plus collapsed alternate peers/files (#427).</summary>
public sealed class SearchAlbumTrackGroupViewModel : ReactiveObject
{
    private bool _isAlternatesExpanded;

    public SearchAlbumTrackGroupViewModel(
        AnalyzedSearchResultViewModel best,
        IReadOnlyList<AnalyzedSearchResultViewModel> alternates)
    {
        Best = best;
        Alternates = alternates;
    }

    public AnalyzedSearchResultViewModel Best { get; }
    public IReadOnlyList<AnalyzedSearchResultViewModel> Alternates { get; }
    public int AlternateCount => Alternates.Count;
    public bool HasAlternates => Alternates.Count > 0;
    public string AlternatesSummary => AlternateCount == 1 ? "+1 alternate" : $"+{AlternateCount} alternates";

    public bool IsAlternatesExpanded
    {
        get => _isAlternatesExpanded;
        set => this.RaiseAndSetIfChanged(ref _isAlternatesExpanded, value);
    }
}

/// <summary>An album-mode search group collapsing duplicate peers/files per track (#427).</summary>
public sealed class SearchAlbumGroupViewModel : ReactiveObject
{
    private bool _isExpanded;

    public SearchAlbumGroupViewModel(
        string albumTitle,
        string artistDisplay,
        IReadOnlyList<SearchAlbumTrackGroupViewModel> tracks,
        int totalFilesCount,
        int peerCount)
    {
        AlbumTitle = albumTitle;
        ArtistDisplay = artistDisplay;
        Tracks = tracks;
        BestTracks = tracks.Select(t => t.Best).ToList();
        TotalFilesCount = totalFilesCount;
        PeerCount = peerCount;
        BestRank = tracks.Count == 0 ? 0 : tracks.Max(t => t.Best.RawResult.CurrentRank);
    }

    public string AlbumTitle { get; }
    public string ArtistDisplay { get; }
    public IReadOnlyList<SearchAlbumTrackGroupViewModel> Tracks { get; }
    public IReadOnlyList<AnalyzedSearchResultViewModel> BestTracks { get; }
    public int TrackCount => Tracks.Count;
    public int TotalFilesCount { get; }
    public int PeerCount { get; }
    public double BestRank { get; }

    public string SummaryText
    {
        get
        {
            var t = TrackCount == 1 ? "1 track" : $"{TrackCount} tracks";
            var f = TotalFilesCount == 1 ? "1 file" : $"{TotalFilesCount} files";
            var p = PeerCount == 1 ? "1 peer" : $"{PeerCount} peers";
            return $"{t} · {f} · {p}";
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }
}
