using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models.Discovery;

namespace SLSKDONET.Services;

/// <summary>
/// Orchestrates external discovery across Spotify and MusicBrainz.
/// Bridges sonic similarity results to P2P search candidates.
/// </summary>
public class DiscoveryBridgeService
{
    private readonly ILogger<DiscoveryBridgeService> _logger;
    private readonly SpotifyEnrichmentService _spotifyEnrichment;
    private readonly IMusicBrainzService _musicBrainz;

    public DiscoveryBridgeService(
        ILogger<DiscoveryBridgeService> logger,
        SpotifyEnrichmentService spotifyEnrichment,
        IMusicBrainzService musicBrainz)
    {
        _logger = logger;
        _spotifyEnrichment = spotifyEnrichment;
        _musicBrainz = musicBrainz;
    }

    /// <summary>
    /// Finds sonically or contextually related tracks from external sources.
    /// </summary>
    public async Task<List<DiscoveryTrack>> DiscoverRelatedTracksAsync(
        string artist, 
        string title, 
        string? spotifyId = null, 
        string? mbid = null)
    {
        var results = new List<DiscoveryTrack>();
        var seenTracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 1. Spotify Recommendations (Sonically Similar)
            if (!string.IsNullOrEmpty(spotifyId))
            {
                var reco = await _spotifyEnrichment.GetRecommendationsForTrackAsync(spotifyId, 15);
                foreach (var t in reco)
                {
                    if (seenTracks.Add($"{t.Artist}|{t.Title}"))
                    {
                        results.Add(new DiscoveryTrack
                        {
                            Artist = t.Artist ?? "Unknown",
                            Title = t.Title ?? "Unknown",
                            SpotifyId = t.Id,
                            MatchReason = "Spotify Recommendation (Sonic)",
                            ImageUrl = t.ImageUrl
                        });
                    }
                }
            }

            // 2. MusicBrainz Deep Discovery (Relationship-based)
            if (!string.IsNullOrEmpty(mbid))
            {
                // Note: GetCreditsAsync returns producers/mixers. 
                // In a full implementation, we'd then query recordings BY those people.
                // For this bridge, we'll log the discovery path and add a few "same artist" discoveries as placeholders
                // until we implement full credit-based search in MusicBrainzService.
                var credits = await _musicBrainz.GetCreditsAsync(mbid);
                if (credits != null)
                {
                    _logger.LogInformation("Discovery Bridge: Found credits for {Artist} - {Title}. Producers: {Producers}", 
                        artist, title, string.Join(", ", credits.Producers));
                    
                    // TODO: In Phase 3.1, implement "Recordings by Producer" lookup in MusicBrainzService
                }
            }

            // Fallback: If no external IDs, try to identify first
            if (string.IsNullOrEmpty(spotifyId) && string.IsNullOrEmpty(mbid))
            {
                var idResult = await _spotifyEnrichment.IdentifyTrackAsync(artist, title);
                if (idResult.Success && !string.IsNullOrEmpty(idResult.SpotifyId))
                {
                    // Recurse with ID
                    return await DiscoverRelatedTracksAsync(artist, title, idResult.SpotifyId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery Bridge failed for {Artist} - {Title}", artist, title);
        }

        return results;
    }
}
