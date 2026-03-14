using System.Collections.Generic;
using System.Threading.Tasks;
using SLSKDONET.Models;
using SpotifyAPI.Web;

namespace SLSKDONET.Services;

public interface ISpotifyMetadataService
{
    /// <summary>
    /// Smart search for a track with fuzzy matching and confidence scoring.
    /// </summary>
    Task<FullTrack?> FindTrackAsync(string artist, string title, int? durationMs = null, bool forceRefresh = false);


    /// <summary>
    /// Enriches a PlaylistTrack with Spotify metadata (ID, Art, Key, BPM).
    /// Used by MetadataEnrichmentOrchestrator.
    /// </summary>
    Task<bool> EnrichTrackAsync(PlaylistTrack track);

    /// <summary>
    /// Enriches a SearchQuery with Spotify metadata.
    /// Used by CSV and other input providers to improve track identification.
    /// </summary>
    Task<bool> EnrichQueryAsync(SearchQuery query);

    /// <summary>
    /// Clears the internal metadata cache.
    /// </summary>
    Task ClearCacheAsync();
}

