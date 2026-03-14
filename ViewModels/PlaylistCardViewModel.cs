using ReactiveUI;
using SLSKDONET.Models;
using System;

namespace SLSKDONET.ViewModels;

/// <summary>
/// ViewModel wrapper for playlist cards on the Mission Control dashboard.
/// Provides UI-specific properties and formatting.
/// </summary>
public class PlaylistCardViewModel : ReactiveObject
{
    private readonly PlaylistJob _playlist;

    public Guid Id => _playlist.Id;
    public string Name => _playlist.SourceTitle;
    public string? CoverImageUrl => _playlist.AlbumArtUrl;
    public int TrackCount => _playlist.TotalTracks;
    
    // Calculated match percentage from database context usually, 
    // but we'll allow it to be passed or updated.
    private double _matchPercentage;
    public double MatchPercentage
    {
        get => _matchPercentage;
        set => this.RaiseAndSetIfChanged(ref _matchPercentage, value);
    }

    public string TrackCountText => $"{TrackCount} track{(TrackCount != 1 ? "s" : "")}";
    public string MatchText => $"{MatchPercentage:0}% Match";

    private bool _isHovered;
    public bool IsHovered
    {
        get => _isHovered;
        set => this.RaiseAndSetIfChanged(ref _isHovered, value);
    }

    public PlaylistCardViewModel(PlaylistJob playlist)
    {
        _playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
        
        // Initial match percentage calculation (if not already set in model)
        // Note: Models don't have MatchPercentage yet, we'll calculate it in DashboardService
        _matchPercentage = _playlist.TotalTracks > 0 
            ? (double)_playlist.SuccessfulCount / _playlist.TotalTracks * 100 
            : 0;
    }

    // Explicit access to the underlying model if needed for commands
    public PlaylistJob Model => _playlist;
}
