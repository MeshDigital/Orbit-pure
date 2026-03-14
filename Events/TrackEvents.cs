using System;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Events;

// NOTE: TrackMetadataUpdatedEvent moved to Models/Events.cs to avoid duplication
// This namespace intentionally left minimal

/// <summary>
/// Published when a track's status, progress, or metadata changes.
/// </summary>
public class TrackStatusChangedEvent
{
    public Guid PlaylistId { get; }
    public string TrackUniqueHash { get; }
    public TrackStatus? NewStatus { get; }
    public double? Progress { get; }
    public string? ErrorMessage { get; }

    public TrackStatusChangedEvent(Guid playlistId, string trackUniqueHash, TrackStatus? newStatus = null, double? progress = null, string? errorMessage = null)
    {
        PlaylistId = playlistId;
        TrackUniqueHash = trackUniqueHash;
        NewStatus = newStatus;
        Progress = progress;
        ErrorMessage = errorMessage;
    }
}

/// <summary>
/// Published when a search produces new results for a track.
/// </summary>
public class TrackSearchStartedEvent
{
    public Guid PlaylistId { get; }
    public string TrackUniqueHash { get; }
    
    public TrackSearchStartedEvent(Guid playlistId, string trackUniqueHash)
    {
        PlaylistId = playlistId;
        TrackUniqueHash = trackUniqueHash;
    }
}


// NOTE: TrackAddedEvent and TrackRemovedEvent moved to Models/Events.cs to avoid duplication

// TrackProgressChangedEvent moved to Models/Events.cs
// TrackStateChangedEvent moved to Models/Events.cs

/// <summary>
/// Published for significant library changes (e.g. project reload required)
/// </summary>
public class LibraryUpdatedEvent
{
    public string Reason { get; }

    public LibraryUpdatedEvent(string reason)
    {
        Reason = reason;
    }
}
