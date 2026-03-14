using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SpotifyAPI.Web;

namespace SLSKDONET.Services;

/// <summary>
/// "The Bulk Brain"
/// Implements the Three Laws of robust Spotify fetching:
/// 1. Rule of 50/100 (Chunking)
/// 2. Two-Pass Fetch (Metadata + Features)
/// 3. Retry-After Mandate (via Configured Client)
/// </summary>
public class SpotifyBulkFetcher
{
    private readonly SpotifyAuthService _auth;

    public SpotifyBulkFetcher(SpotifyAuthService auth)
    {
        _auth = auth;
    }

    /// <summary>
    /// Downloads Track Metadata AND Audio Features (BPM, Key) for a list of IDs.
    /// Handles chunking (50/100 limits) automatically.
    /// </summary>
    public async Task<List<FullTrackWithFeatures>> GetEnrichedTracksAsync(List<string> trackIds)
    {
        if (trackIds == null || !trackIds.Any())
            return new List<FullTrackWithFeatures>();

        var client = await _auth.GetAuthenticatedClientAsync();
        var distinctIds = trackIds.Distinct().ToList();

        // 1. Get Basic Metadata (Limit 50 per call)
        // Rule of 50: Tracks endpoint limit
        var metadataTasks = distinctIds.Chunk(50).Select(async batch => 
        {
            var response = await client.Tracks.GetSeveral(new TracksRequest(batch.ToList()));
            return response.Tracks;
        });

        // 2. Get Audio Features (Limit 100 per call)
        // Rule of 100: Audio Features endpoint limit
        var featuresTasks = distinctIds.Chunk(100).Select(async batch => 
        {
            var response = await client.Tracks.GetSeveralAudioFeatures(new TracksAudioFeaturesRequest(batch.ToList()));
            return response.AudioFeatures;
        });

        // 3. Wait for all requests to finish (Parallel execution)
        var metadataResults = (await Task.WhenAll(metadataTasks)).SelectMany(x => x).ToList();
        var featuresResults = (await Task.WhenAll(featuresTasks)).SelectMany(x => x).ToList();

        // 4. Stitch them together
        var enrichedTracks = new List<FullTrackWithFeatures>();
        
        foreach (var track in metadataResults)
        {
            if (track == null) continue; // Handle dead IDs

            var feature = featuresResults.FirstOrDefault(f => f != null && f.Id == track.Id);
            enrichedTracks.Add(new FullTrackWithFeatures 
            { 
                Track = track, 
                Features = feature // This might be null if analysis isn't available
            });
        }

        return enrichedTracks;
    }
}

// Helper DTO to keep your "Brain" organized
public class FullTrackWithFeatures
{
    public FullTrack Track { get; set; } = null!;
    public TrackAudioFeatures? Features { get; set; }
}
