using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SpotifyAPI.Web;

namespace SLSKDONET.Services;

/// <summary>
/// Bulk track-metadata fetch by Spotify ID, in chunks of 50 (the Tracks endpoint limit), through
/// the authenticated client whose retry handler honours Retry-After.
/// <para>
/// Audio features (BPM/key/energy) are deliberately NOT fetched: since November 2024 Spotify
/// returns 403 for <c>audio-features</c> and <c>recommendations</c> to new apps, and ORBIT's own
/// analysis owns BPM/key/energy anyway. Library enrichment lives in
/// <see cref="Enrichment.MetadataEnrichmentOrchestrator"/>.
/// </para>
/// </summary>
public class SpotifyBulkFetcher
{
    private readonly SpotifyAuthService _auth;

    public SpotifyBulkFetcher(SpotifyAuthService auth)
    {
        _auth = auth;
    }

    /// <summary>Full track metadata for a list of IDs; unknown IDs are skipped.</summary>
    public async Task<List<FullTrack>> GetTracksAsync(List<string> trackIds)
    {
        if (trackIds == null || trackIds.Count == 0)
            return new List<FullTrack>();

        var client = await _auth.GetAuthenticatedClientAsync();
        var results = new List<FullTrack>();
        foreach (var batch in trackIds.Distinct().Chunk(50))
        {
            var response = await client.Tracks.GetSeveral(new TracksRequest(batch.ToList()));
            results.AddRange(response.Tracks.Where(t => t != null));
        }
        return results;
    }
}
