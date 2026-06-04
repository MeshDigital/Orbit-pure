using System;
using System.Collections.Generic;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Models;

// Player Events
public record TrackPlaybackStartedEvent(string FilePath, string Artist, string Title);
public record TrackPlaybackPausedEvent();
public record TrackPlaybackResumedEvent();
public record TrackPlaybackStoppedEvent();
public record PlaybackProgressEvent(TimeSpan Position, TimeSpan Duration);

// Navigation & Global UI Events
public record NavigationEvent(PageType PageType);
public record TrackSelectionChangedEvent(PlaylistTrack? Track); // Phase 12.6: Inspector Sync
public record PlayTrackRequestEvent(PlaylistTrackViewModel Track);
public record AddToQueueRequestEvent(PlaylistTrackViewModel Track);
public record PlayAlbumRequestEvent(IEnumerable<PlaylistTrack> Tracks);
public record DownloadAlbumRequestEvent(object Album); // object to handle AlbumNode or PlaylistJob
public record RequestTheaterModeEvent();
public record AddToTimelineRequestEvent(IEnumerable<PlaylistTrack> Tracks);
public record AddToProjectRequestEvent(IEnumerable<PlaylistTrack> Tracks); // Phase 12.7: Context Menu Actions
public record RevealFileRequestEvent(string FilePath);
public record SeekRequestEvent(double PositionPercent); // 0.0 to 1.0
public record SeekToSecondsRequestEvent(double Seconds);
