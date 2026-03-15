using ReactiveUI;
using SLSKDONET.Models;
using System.Linq;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Enhanced ViewModel wrapper for playlist cards in the library with forensic health metrics.
/// Provides UI-specific properties including the Health Ring calculation.
/// </summary>
public class LibraryPlaylistCardViewModel : ReactiveObject
{
    private readonly PlaylistJob _playlist;

    public Guid Id => _playlist.Id;
    public string Name => _playlist.SourceTitle;
    public string? CoverImageUrl => _playlist.DisplayArtUrl;
    public int TrackCount => _playlist.TotalTracks;

    public string TrackCountText => $"{TrackCount} track{(TrackCount != 1 ? "s" : "")}";

    /// <summary>
    /// Health Ring: Ratio of verified lossless tracks (0.0 to 1.0)
    /// Calculated as (Verified + Gold) / TotalTracks
    /// </summary>
    public double LosslessRatio
    {
        get
        {
            if (_playlist.PlaylistTracks == null || _playlist.TotalTracks == 0)
                return 0.0;

            var verifiedCount = _playlist.PlaylistTracks.Count(t =>
                t.Integrity == SLSKDONET.Data.IntegrityLevel.Verified ||
                t.Integrity == SLSKDONET.Data.IntegrityLevel.Gold);

            return (double)verifiedCount / _playlist.TotalTracks;
        }
    }

    /// <summary>
    /// Health Ring color based on lossless ratio
    /// </summary>
    public string HealthRingColor => LosslessRatio switch
    {
        >= 0.9 => "#00FF88", // Green - Excellent
        >= 0.7 => "#FFCC00", // Yellow - Good
        >= 0.5 => "#FF9D00", // Orange - Fair
        _ => "#FF4444"       // Red - Poor
    };

    /// <summary>
    /// Health status text for tooltip
    /// </summary>
    public string HealthStatusText
    {
        get
        {
            if (_playlist.PlaylistTracks == null)
                return "No tracks analyzed";

            var verified = _playlist.PlaylistTracks.Count(t =>
                t.Integrity == SLSKDONET.Data.IntegrityLevel.Verified ||
                t.Integrity == SLSKDONET.Data.IntegrityLevel.Gold);

            var suspicious = _playlist.PlaylistTracks.Count(t =>
                t.Integrity == SLSKDONET.Data.IntegrityLevel.Suspicious);

            return $"{verified} verified lossless, {suspicious} suspicious";
        }
    }

    private bool _isHovered;
    public bool IsHovered
    {
        get => _isHovered;
        set => this.RaiseAndSetIfChanged(ref _isHovered, value);
    }

    public LibraryPlaylistCardViewModel(PlaylistJob playlist)
    {
        _playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
    }

    // Explicit access to the underlying model if needed for commands
    public PlaylistJob Model => _playlist;
}