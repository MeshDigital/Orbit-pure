using System.ComponentModel;

namespace SLSKDONET.ViewModels;

public interface IDisplayableTrack : INotifyPropertyChanged
{
    string GlobalId { get; }
    string ArtistName { get; }
    string TrackTitle { get; }
    string AlbumName { get; }
    string? AlbumArtUrl { get; }
    
    // Status & Progress
    string StatusText { get; }
    double Progress { get; } // 0-100
    bool IsIndeterminate { get; } // For searching state
    
    // Technical Stats
    string TechnicalSummary { get; } // "Soulseek • 320kbps • 12MB"
    
    // Metrics
    double IntegrityScore { get; } // 0.0 - 1.0 (or 0-100)
    bool IsSecure { get; } // Validated/Safe
    
    // State Flags for UI triggers
    bool IsFailed { get; }
    bool IsActive { get; }
    bool IsCompleted { get; }
}
