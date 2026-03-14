using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Interface for library persistence and management.
/// Manages three distinct indexes: LibraryEntry (main), PlaylistJob (header), PlaylistTrack (relational).
/// </summary>
public interface ILibraryService
{
    // Events now published via IEventBus (ProjectDeletedEvent, ProjectUpdatedEvent)

    /// <summary>
    /// Fired when a new playlist job is created/added.
    /// </summary>



    // ===== INDEX 1: LibraryEntry (Main Global Index) =====
    
    /// <summary>
    /// Retrieves a library entry by its UniqueHash (asynchronous).
    /// </summary>
    Task<LibraryEntry?> FindLibraryEntryAsync(string uniqueHash);
    Task<LibraryEntryEntity?> GetTrackEntityByHashAsync(string uniqueHash);

    /// <summary>
    /// Loads all library entries (main global index).
    /// </summary>
    Task<List<LibraryEntry>> LoadAllLibraryEntriesAsync();

    /// <summary>
    /// Loads a specific set of library entries by their UniqueHash.
    /// Used for Smart Crates and bulk retrievals.
    /// </summary>
    Task<List<LibraryEntry>> GetLibraryEntriesByHashesAsync(List<string> hashes);
    
    /// <summary>
    /// Searches library entries and returns enrichment status.
    /// </summary>
    Task<List<LibraryEntry>> SearchLibraryEntriesWithStatusAsync(string query, int limit = 50);

    /// <summary>
    /// Atomically saves (inserts or updates) a library entry based on its UniqueHash.
    /// This replaces separate Add and Update methods to prevent race conditions.
    /// </summary>
    Task SaveOrUpdateLibraryEntryAsync(LibraryEntry entry);
    
    /// <summary>
    /// Scans all completed PlaylistTracks and populates the global LibraryEntry index if missing.
    /// Called on startup to fix "All Tracks" empty view for existing downloads.
    /// </summary>
    Task SyncLibraryEntriesFromTracksAsync();

    /// <summary>
    /// Standardized entry point for adding a successful download to the global library index.
    /// </summary>
    Task AddTrackToLibraryIndexAsync(PlaylistTrack track, string finalPath);

    /// <summary>
    /// Removes a track from the global library index.
    /// Does NOT delete the physical file (handled by caller).
    /// </summary>
    Task RemoveTrackFromLibraryAsync(string trackHash);

    // ===== INDEX 2: PlaylistJob (Playlist Headers) =====

    /// <summary>
    /// Loads all playlist jobs (playlist history).
    /// Used to populate the Playlist List view in the UI.
    /// </summary>
    /// <summary>
    /// Loads all playlist jobs (playlist history).
    /// Used to populate the Playlist List view in the UI.
    /// </summary>
    Task<List<PlaylistJob>> LoadAllPlaylistJobsAsync();

    /// <summary>
    /// Loads historical (completed/cancelled) jobs for history view.
    /// </summary>
        Task<List<PlaylistJob>> GetHistoricalJobsAsync();
        
        // Activity Logging
        Task LogPlaylistActivityAsync(Guid playlistId, string action, string details);
        Task<bool> UndoLastActivityAsync(Guid playlistId, string action);

    /// <summary>
    /// Loads a specific playlist job by ID (asynchronous).
    /// Includes related PlaylistTrack entries.
    /// </summary>
    Task<PlaylistJob?> FindPlaylistJobAsync(Guid playlistId);

    /// <summary>
    /// Finds a playlist job by its SourceType (e.g., "Spotify Liked").
    /// Used to detect if a sync job already exists.
    /// </summary>
    Task<PlaylistJob?> FindPlaylistJobBySourceTypeAsync(string sourceType);
    
    /// <summary>
    /// Finds a playlist job by its Source URL (ignoring query parameters if implemented).
    /// Used for robust duplicate detection.
    /// </summary>
    Task<PlaylistJob?> FindPlaylistJobBySourceUrlAsync(string sourceUrl);

    /// <summary>
    /// Saves a new or existing playlist job.
    /// Called when importing a new source.
    /// </summary>
    Task SavePlaylistJobAsync(PlaylistJob job);

    /// <summary>
    /// Atomically saves a new playlist job AND its tracks.
    /// Updates the reactive Playlists collection immediately.
    /// </summary>
    Task SavePlaylistJobWithTracksAsync(PlaylistJob job);

    /// <summary>
    /// Deletes a playlist job and its related PlaylistTrack entries.
    /// </summary>
    Task DeletePlaylistJobAsync(Guid playlistId);
    Task<List<PlaylistJob>> LoadDeletedPlaylistJobsAsync();
    Task RestorePlaylistJobAsync(Guid jobId);
    
    // Phase 15
    Task<List<StyleDefinitionEntity>> GetStyleDefinitionsAsync();
    Task DeletePlaylistTracksAsync(Guid jobId);
    Task DeletePlaylistTrackAsync(Guid playlistTrackId);

    // Phase 16.2: Vibe Match
    Task<List<AudioFeaturesEntity>> GetAllAudioFeaturesAsync();

    /// <summary>
    /// Creates a new empty user playlist.
    /// </summary>
    Task<PlaylistJob> CreateEmptyPlaylistAsync(string title);

    /// <summary>
    /// Updates sort order of tracks in a playlist.
    /// </summary>
    Task SaveTrackOrderAsync(Guid playlistId, IEnumerable<PlaylistTrack> tracks);

    // ===== INDEX 3: PlaylistTrack (Relational Index) =====

    /// <summary>
    /// Loads all tracks for a specific playlist job.
    /// Used for the Playlist Track Detail view.
    /// </summary>
    Task<List<PlaylistTrack>> LoadPlaylistTracksAsync(Guid playlistId);

    /// <summary>
    /// Loads ALL tracks from all active playlists.
    /// Used for "All Tracks" view.
    /// </summary>
    Task<List<PlaylistTrack>> GetAllPlaylistTracksAsync();
    
    /// <summary>
    /// Gets the count of tracks in a playlist, optionally filtered.
    /// </summary>
    Task<int> GetTrackCountAsync(Guid playlistId, string? filter = null, bool? downloadedOnly = null);

    /// <summary>
    /// Loads a page of tracks for a specific playlist.
    /// </summary>
    Task<List<PlaylistTrack>> GetPagedPlaylistTracksAsync(Guid playlistId, int skip, int take, string? filter = null, bool? downloadedOnly = null);

    /// <summary>
    /// Loads a specific track from a playlist by its unique hash.
    /// Used for efficient metadata updates without reloading the entire playlist.
    /// </summary>
    Task<PlaylistTrack?> GetPlaylistTrackByHashAsync(Guid playlistId, string trackHash);

    /// <summary>
    /// Saves a single playlist track entry.
    /// Called during import to create the relational index.
    /// </summary>
    Task SavePlaylistTrackAsync(PlaylistTrack track);

    /// <summary>
    /// Updates a playlist track (e.g., status or resolved path).
    /// Called when a download completes.
    /// </summary>
    Task UpdatePlaylistTrackAsync(PlaylistTrack track);

    /// <summary>
    /// Bulk save multiple playlist tracks.
    /// Called after initial import to create all relational entries at once.
    /// </summary>
    Task SavePlaylistTracksAsync(List<PlaylistTrack> tracks);

    // ===== Legacy / Compatibility =====

    /// <summary>
    /// Async version of LoadDownloadedTracks.
    /// </summary>
    Task<List<LibraryEntry>> LoadDownloadedTracksAsync();

    /// <summary>
    /// Adds a track to the library with optional source playlist reference (legacy).
    /// </summary>
    Task AddTrackAsync(Track track, string actualFilePath, Guid sourcePlaylistId);

    // Phase 1: Heavy Data Lazy Loading
    Task<TrackTechnicalEntity?> GetTechnicalDetailsAsync(Guid playlistTrackId);
    Task SaveTechnicalDetailsAsync(TrackTechnicalEntity details);

    // Phase 11.5: Verification
    Task MarkTrackAsVerifiedAsync(string trackHash);
    
    // Phase 16.2: Vibe Match
    Task<AudioFeaturesEntity?> GetAudioFeaturesByHashAsync(string uniqueHash);


    Task<List<LibraryEntry>> GetTracksAddedSinceAsync(DateTime since);

    /// <summary>
    /// Adds existing tracks to a project by creating new relational entries.
    /// </summary>
    Task AddTracksToProjectAsync(System.Collections.Generic.IEnumerable<PlaylistTrack> tracks, Guid targetProjectId);

    /// <summary>
    /// Updates the cue points for all instances of a track (Library and Playlist entries).
    /// </summary>
    Task UpdateTrackCuePointsAsync(string trackHash, string cuePointsJson);

    /// <summary>
    /// Phase 2: Updates the surgical structural features for a track.
    /// </summary>
    Task UpdateAudioFeaturesAsync(AudioFeaturesEntity entity);

    /// <summary>
    /// Global "Like" update: synchronizes status across Library and all Projects.
    /// </summary>
    Task UpdateLikeStatusAsync(string trackHash, bool isLiked);
    Task UpdateRatingAsync(string trackHash, int rating);

    /// <summary>
    /// Searches tracks across all playlists.
    /// </summary>
    Task<List<PlaylistTrack>> SearchAllPlaylists(string query, int limit = 50);

    /// <summary>
    /// Phase 2: Synergy - Finds if a track exists in other projects.
    /// </summary>
    Task<List<PlaylistTrack>> FindTrackInOtherProjectsAsync(string artist, string title, Guid currentProjectId);

    /// <summary>
    /// Phase 2: Structural - Loads all detected phrases for a track.
    /// </summary>
    Task<List<TrackPhraseEntity>> GetPhrasesByHashAsync(string trackHash);

    /// <summary>
    /// Phase 2: Structural - Persists detected structural segments.
    /// </summary>
    Task SavePhrasesAsync(List<TrackPhraseEntity> phrases);
}
