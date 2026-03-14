using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SLSKDONET.Models;

/// <summary>
/// Represents a playlist/source import job (e.g., from Spotify, CSV).
/// This is the playlist header/metadata in the relational library structure.
/// Foreign Keys: One-to-Many relationship with PlaylistTrack.
/// </summary>
public class PlaylistJob : INotifyPropertyChanged
{
    private int _successfulCount;
    private int _failedCount;
    private int _missingCount;

    /// <summary>
    /// Unique identifier for this job (Primary Key).
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Name of the source playlist/list (e.g., "Chill Vibes 2025").
    /// </summary>
    public string SourceTitle { get; set; } = "Untitled Playlist";

    // UI Compatibility Aliases
    public string Name => SourceTitle;
    public string AlbumTitle => SourceTitle;
    public int TrackCount => TotalTracks;
    public string Artist => string.IsNullOrEmpty(SourceUrl) ? "Various Artists" : (SourceType == "Spotify" ? "Spotify Playlist" : "Local Import");
    
    public double TotalSizeMb => PlaylistTracks.Sum(t => (t.Bitrate ?? 0) * (t.CanonicalDuration ?? 0) / 8.0) / 1024.0 / 1024.0;
    
    public string QualitySummary => SuccessfulCount > 0 ? $"{SuccessfulCount} tracks downloaded" : "Pending Analysis";
    public int? MatchConfidence => TotalTracks > 0 ? (int)((double)SuccessfulCount / TotalTracks * 100) : null;

    /// <summary>
    /// Type of source (e.g., "Spotify", "CSV", "YouTube").
    /// </summary>
    public string SourceType { get; set; } = "Unknown";

    /// <summary>
    /// The folder where tracks from this job will be downloaded.
    /// </summary>
    public string DestinationFolder { get; set; } = "";

    /// <summary>
    /// URL for the playlist/album cover art.
    /// </summary>
    public string? AlbumArtUrl { get; set; }

    /// <summary>
    /// Resolves the art to display, falling back to the first track's art if the playlist art is missing.
    /// </summary>
    public string? DisplayArtUrl => !string.IsNullOrEmpty(AlbumArtUrl) 
        ? AlbumArtUrl 
        : PlaylistTracks?.FirstOrDefault(t => !string.IsNullOrEmpty(t.AlbumArtUrl))?.AlbumArtUrl;

    /// <summary>
    /// Returns an emoji icon based on the source type.
    /// </summary>
    public string SourceIcon => SourceType switch
    {
        "Spotify" => "🎧",
        "YouTube" => "🎬",
        "CSV" => "📄",
        "Local" => "📁",
        "User" => "👤",
        _ => "🎵"
    };

    /// <summary>
    /// Source URL for duplicate detection (e.g. Spotify URI/URL).
    /// </summary>
    public string? SourceUrl { get; set; }

    /// <summary>
    /// The complete, original list of tracks fetched from the source.
    /// This list is never modified; it represents the full source.
    /// </summary>
    public ObservableCollection<Track> OriginalTracks { get; set; } = new();

    /// <summary>
    /// Related PlaylistTrack entries (Foreign Key relationship).
    /// This is populated when loading from the database.
    /// In-memory during import, persisted to the relational index.
    /// </summary>
    public List<PlaylistTrack> PlaylistTracks { get; set; } = new();

    /// <summary>
    /// When the job was created/imported.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Total number of tracks in this job.
    /// Default logic falls back to collection counts if not explicitly set.
    /// </summary>
    private int _totalTracksOverride;
    public int TotalTracks 
    {
        get 
        {
            if (_totalTracksOverride > 0) return _totalTracksOverride;
            if (PlaylistTracks?.Count > 0) return PlaylistTracks.Count;
            return OriginalTracks?.Count ?? 0;
        }
        set 
        {
             SetProperty(ref _totalTracksOverride, value);
             OnPropertyChanged(nameof(ProgressPercentage));
        }
    }

    /// <summary>
    /// Number of tracks successfully downloaded (status = Downloaded).
    /// </summary>
    public int SuccessfulCount
    {
        get => _successfulCount;
        set { SetProperty(ref _successfulCount, value); }
    }

    /// <summary>
    /// Number of tracks that failed to download (status = Failed).
    /// </summary>
    public int FailedCount
    {
        get => _failedCount;
        set { SetProperty(ref _failedCount, value); }
    }

    /// <summary>
    /// Number of tracks yet to be downloaded (status = Missing).
    /// </summary>
    public int MissingCount
    {
        get => _missingCount;
        set { SetProperty(ref _missingCount, value); }
    }

    private int _activeDownloadsCount;
    /// <summary>
    /// Number of tracks currently being downloaded (Downloading, Searching, or Queued state).
    /// </summary>
    public int ActiveDownloadsCount
    {
        get => _activeDownloadsCount;
        set 
        { 
            if (SetProperty(ref _activeDownloadsCount, value))
            {
                OnPropertyChanged(nameof(HasActiveDownloads));
            }
        }
    }

    /// <summary>
    /// Helper for XAML bindings to toggle visibility of download indicators.
    /// </summary>
    public bool HasActiveDownloads => ActiveDownloadsCount > 0;

    private string? _currentDownloadingTrack;
    /// <summary>
    /// Display name of the track currently being downloaded (e.g., "Artist - Title").
    /// </summary>
    public string? CurrentDownloadingTrack
    {
        get => _currentDownloadingTrack;
        set { SetProperty(ref _currentDownloadingTrack, value); }
    }

    /// <summary>
    /// True if user manually paused downloads (don't auto-resume on crash recovery).
    /// </summary>
    public bool IsUserPaused { get; set; } = false;
    
    /// <summary>
    /// When the first track in this job started downloading.
    /// </summary>
    public DateTime? DateStarted { get; set; }
    
    /// <summary>
    /// Last time the download orchestrator touched this job.
    /// </summary>
    public DateTime DateUpdated { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    // Phase 20: Smart Playlists 2.0
    public bool IsSmartPlaylist { get; set; } = false;
    public string? SmartCriteriaJson { get; set; }

    /// <summary>
    /// Overall progress percentage for this job (0-100).
    /// Calculated as: (SuccessfulCount + FailedCount) / TotalTracks * 100
    /// </summary>
    public double ProgressPercentage
    {
        get
        {
            if (TotalTracks == 0) return 0;
            return (double)(SuccessfulCount + FailedCount) / TotalTracks * 100;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Recalculates status counts from PlaylistTracks.
    /// </summary>
    public void RefreshStatusCounts()
    {
        SuccessfulCount = PlaylistTracks.Count(t => t.Status == Models.TrackStatus.Downloaded);
        FailedCount = PlaylistTracks.Count(t => t.Status == Models.TrackStatus.Failed || t.Status == Models.TrackStatus.Skipped);
        MissingCount = PlaylistTracks.Count(t => t.Status == Models.TrackStatus.Missing);
    }

    /// <summary>
    /// Gets a user-friendly summary of the job progress.
    /// </summary>
    public override string ToString()
    {
        return $"{SourceTitle} ({SourceType}) - {SuccessfulCount}/{TotalTracks} downloaded";
    }
}
