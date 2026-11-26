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
            // 1. MusicBrainz Deep Discovery (Relationship-based)
            // Note (Epic #508 / #510): Spotify GET /recommendations is unavailable for dev-mode apps,
            // so external discovery relies on MusicBrainz and local sonic similarity.
            if (!string.IsNullOrEmpty(mbid))
            {
                var credits = await _musicBrainz.GetCreditsAsync(mbid);
                if (credits != null)
                {
                    _logger.LogInformation("Discovery Bridge: Found credits for {Artist} - {Title}. Producers: {Producers}", 
                        artist, title, string.Join(", ", credits.Producers));
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
