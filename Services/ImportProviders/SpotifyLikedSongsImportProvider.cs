using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.ImportProviders;

/// <summary>
/// Import provider for fetching the user's "Liked Songs" from Spotify.
/// Uses the authenticated SpotifyAuthService.
/// </summary>
public class SpotifyLikedSongsImportProvider : IImportProvider, IStreamingImportProvider
{
    private readonly ILogger<SpotifyLikedSongsImportProvider> _logger;
    private readonly SpotifyAuthService _authService;
    private readonly ISpotifyMetadataService _metadataService;

    public string Name => "Spotify Liked Songs";
    public string IconGlyph => "❤️";
    public bool ForceRefresh { get; set; }

    public SpotifyLikedSongsImportProvider(
        ILogger<SpotifyLikedSongsImportProvider> logger,
        SpotifyAuthService authService,
        ISpotifyMetadataService metadataService)
    {
        _logger = logger;
        _authService = authService;
        _metadataService = metadataService;
    }

    public bool CanHandle(string input)
    {
        return input.Equals("spotify:liked", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ImportResult> ImportAsync(string input)
    {
        var result = new ImportResult
        {
            Success = false,
            Tracks = new List<SearchQuery>()
        };

        try
        {
            if (!await _authService.IsAuthenticatedAsync())
            {
                result.ErrorMessage = "Please connect your Spotify account in Settings first.";
                return result;
            }

            var client = await _authService.GetAuthenticatedClientAsync();
            if (client == null)
            {
                result.ErrorMessage = "Failed to authenticate with Spotify.";
                return result;
            }

            _logger.LogInformation("Fetching Spotify Liked Songs...");

            var tracks = new List<SearchQuery>();
            var initialPage = await client.Library.GetTracks(new LibraryTracksRequest { Limit = 50 });
            
            await ProcessPage(initialPage, tracks);
            
            var total = initialPage.Total;
            var fetched = tracks.Count;

            // Fetch all pages
            var currentPage = initialPage;
            while (currentPage.Next != null)
            {
                // Simple rate limit avoidance
                await Task.Delay(100); 
                currentPage = await client.NextPage(currentPage);
                await ProcessPage(currentPage, tracks);
                fetched = tracks.Count;
                
                // Optional: Progress update? 
                // We're inside a single async call, hard to report progress back without an implementation change.
                _logger.LogInformation("Fetched {Fetched}/{Total} liked songs", fetched, total);
            }

            result.Success = true;
            result.SourceTitle = "Liked Songs (Spotify)";
            result.Tracks = tracks;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import Spotify Liked Songs");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    private async Task ProcessPage(Paging<SavedTrack> page, List<SearchQuery> tracks)
    {
        if (page.Items == null) return;

        foreach (var item in page.Items)
        {
            var track = item.Track;
            if (track == null) continue;

            var query = MapToSearchQuery(track);
            tracks.Add(query);
        }
        await Task.CompletedTask;
    }

    private SearchQuery MapToSearchQuery(FullTrack track)
    {
        return new SearchQuery
        {
            Title = track.Name,
            Artist = track.Artists.FirstOrDefault()?.Name ?? "Unknown Artist",
            Album = track.Album.Name,
            SpotifyTrackId = track.Id,
            ReleaseDate = !string.IsNullOrEmpty(track.Album.ReleaseDate) ? DateTime.TryParse(track.Album.ReleaseDate, out var d) ? d : null : null,
            CanonicalDuration = track.DurationMs,
            Popularity = track.Popularity,
            AlbumArtUrl = track.Album.Images?.FirstOrDefault()?.Url,
            ForceRefresh = this.ForceRefresh
        };
    }

    public async IAsyncEnumerable<ImportBatchResult> ImportStreamAsync(string input)
    {
        if (!await _authService.IsAuthenticatedAsync()) yield break;
        
        var client = await _authService.GetAuthenticatedClientAsync();
        Paging<SavedTrack> page;

        try 
        {
            _logger.LogInformation("Streaming Spotify Liked Songs...");
            page = await client.Library.GetTracks(new LibraryTracksRequest { Limit = 50 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Spotify stream");
            yield break;
        }

        int total = page.Total ?? 0;
        
        // Yield first page
        var firstBatch = new List<SearchQuery>();
        await ProcessPage(page, firstBatch);
        yield return new ImportBatchResult 
        { 
            Tracks = firstBatch, 
            SourceTitle = "Liked Songs (Spotify)",
            TotalEstimated = total 
        };

        // Fetch remaining pages
        while (page.Next != null)
        {
            ImportBatchResult? batchResult = null;
            try
            {
                await Task.Delay(100); // Rate limiting
                page = await client.NextPage(page);
                
                var batch = new List<SearchQuery>();
                await ProcessPage(page, batch);
                
                if (batch.Any())
                {
                    batchResult = new ImportBatchResult 
                    { 
                        Tracks = batch, 
                        SourceTitle = "Liked Songs (Spotify)",
                        TotalEstimated = total 
                    };
                }
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, "Error streaming page");
                 break; // Stop on error but keep what we have
            }

            if (batchResult != null)
            {
                yield return batchResult;
            }
        }
    }
}
