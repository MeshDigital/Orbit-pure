using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Models;
using SLSKDONET.Services.Models;

namespace SLSKDONET.Services.Repositories;

public interface ITrackRepository
{
    Task<List<TrackEntity>> LoadTracksAsync();
    Task<TrackEntity?> FindTrackAsync(string globalId);
    Task SaveTrackAsync(TrackEntity track);
    Task UpdateTrackFilePathAsync(string globalId, string newPath);
    Task RemoveTrackAsync(string globalId);
    Task<List<PlaylistTrackEntity>> LoadPlaylistTracksAsync(Guid playlistId);
    Task<PlaylistTrackEntity?> GetPlaylistTrackByHashAsync(Guid playlistId, string hash);
    Task SavePlaylistTrackAsync(PlaylistTrackEntity track);
    Task<List<PlaylistTrackEntity>> GetAllPlaylistTracksAsync();
    Task<int> GetPlaylistTrackCountAsync(Guid playlistId, string? filter = null, bool? downloadedOnly = null);
    Task<List<PlaylistTrackEntity>> GetPagedPlaylistTracksAsync(Guid playlistId, int skip, int take, string? filter = null, bool? downloadedOnly = null);
    Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingEnrichmentAsync(int limit);
    Task UpdateLibraryEntryEnrichmentAsync(string uniqueHash, TrackEnrichmentResult result);
    Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingEnrichmentAsync(int limit);
    Task UpdatePlaylistTrackEnrichmentAsync(Guid id, TrackEnrichmentResult result);
    Task<List<Guid>> UpdatePlaylistTrackStatusAndRecalculateJobsAsync(string trackUniqueHash, TrackStatus newStatus, string? resolvedPath, int searchRetryCount = 0, int notFoundRestartCount = 0);
    Task SavePlaylistTracksAsync(IEnumerable<PlaylistTrackEntity> tracks);
    Task DeletePlaylistTracksAsync(Guid playlistId);
    Task UpdatePlaylistTracksPriorityAsync(Guid playlistId, int newPriority);
    Task UpdatePlaylistTrackPriorityAsync(Guid trackId, int newPriority);
    Task DeleteSinglePlaylistTrackAsync(Guid trackId);
    Task<TrackTechnicalEntity?> GetTrackTechnicalDetailsAsync(Guid playlistTrackId);
    Task<TrackTechnicalEntity> GetOrCreateTechnicalDetailsAsync(Guid playlistTrackId);
    Task SaveTechnicalDetailsAsync(TrackTechnicalEntity details);
    Task<List<LibraryEntryEntity>> GetAllLibraryEntriesAsync();
    Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingGenresAsync(int limit);
    Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingGenresAsync(int limit);
    Task UpdateLibraryEntriesGenresAsync(Dictionary<string, List<string>> artistGenreMap);
    Task MarkTrackAsVerifiedAsync(string trackHash);
    Task<int> GetTotalLibraryTrackCountAsync(string? filter = null, bool? downloadedOnly = null);
    Task<List<PlaylistTrackEntity>> GetPagedAllTracksAsync(int skip, int take, string? filter = null, bool? downloadedOnly = null);
    Task<List<LibraryEntryEntity>> SearchLibraryFtsAsync(string searchTerm, int limit = 100);
    Task UpdateAllInstancesMetadataAsync(string trackHash, TrackEnrichmentResult result);
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
    Task<List<PlaylistTrackEntity>> SearchPlaylistTracksAsync(string query, int limit = 50);

    /// <summary>
    /// Finds downloaded copies of a track in projects other than the specified one.
    /// Used by the Cross-Project Synergy feature.
    /// </summary>
    Task<List<PlaylistTrackEntity>> FindTracksInOtherProjectsAsync(string artist, string title, Guid excludeProjectId);

    /// <summary>
    /// Retrieves all detected phrases for a specific track.
    /// </summary>
    Task<List<TrackPhraseEntity>> GetPhrasesByHashAsync(string trackHash);

    /// <summary>
    /// Persists a collection of detected musical phrases, replacing any existing ones for the track.
    /// </summary>
    Task SavePhrasesAsync(List<TrackPhraseEntity> phrases);
}
