using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;

namespace SLSKDONET.Services.ImportProviders;

/// <summary>
/// Import provider for Spotify playlists.
/// </summary>
public class SpotifyImportProvider : IStreamingImportProvider
{
    private readonly ILogger<SpotifyImportProvider> _logger;
    private readonly SpotifyInputSource _spotifyInputSource;
    private readonly SpotifyScraperInputSource _spotifyScraperInputSource;
    private readonly Configuration.AppConfig _config;

    public string Name => "Spotify";
    public string IconGlyph => "🎵";

    public SpotifyImportProvider(
        ILogger<SpotifyImportProvider> logger,
        SpotifyInputSource spotifyInputSource,
        SpotifyScraperInputSource spotifyScraperInputSource,
        Configuration.AppConfig config)
    {
        _logger = logger;
        _spotifyInputSource = spotifyInputSource;
        _spotifyScraperInputSource = spotifyScraperInputSource;
        _config = config;
    }

    public bool CanHandle(string input)
    {
        return !string.IsNullOrWhiteSpace(input) && 
               (input.Contains("spotify.com") || input.StartsWith("spotify:"));
    }

    public async Task<ImportResult> ImportAsync(string playlistUrl)
    {
        try
        {
            _logger.LogInformation("Importing from Spotify: {Url}", playlistUrl);

            // Determine which import method to use
            // Determine which import method to use
            // Fix: Check InputSource configuration which includes User Auth (PKCE)
            var useApi = _spotifyInputSource.IsConfigured;

            var tracks = useApi
                ? await _spotifyInputSource.ParseAsync(playlistUrl)
                : await _spotifyScraperInputSource.ParseAsync(playlistUrl);

            if (!tracks.Any())
            {
                return new ImportResult
                {
                    Success = false,
                    ErrorMessage = "No tracks found in the Spotify playlist"
                };
            }

            var sourceTitle = tracks.FirstOrDefault()?.SourceTitle ?? "Spotify Playlist";

            _logger.LogInformation("Successfully imported {Count} tracks from Spotify playlist '{Title}'", 
                tracks.Count, sourceTitle);

            return new ImportResult
            {
                Success = true,
                SourceTitle = sourceTitle,
                Tracks = tracks
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import from Spotify: {Url}", playlistUrl);
            return new ImportResult
            {
                Success = false,
                ErrorMessage = $"Spotify import failed: {ex.Message}"
            };
        }
    }
    public async IAsyncEnumerable<ImportBatchResult> ImportStreamAsync(string input)
    {
        // Use API streaming if configured, otherwise fallback to scraper (which is mostly blocking/single-batch)
        var useApi = _spotifyInputSource.IsConfigured;

        if (useApi)
        {
             await foreach (var batch in _spotifyInputSource.ParseStreamAsync(input))
             {
                 yield return new ImportBatchResult
                 {
                     Tracks = batch,
                     SourceTitle = batch.FirstOrDefault()?.SourceTitle ?? "Spotify Playlist",
                     TotalEstimated = batch.FirstOrDefault()?.TotalTracks ?? 0
                 };
             }
        }
        else
        {
            // Scraper fallback (blocking)
            var result = await ImportAsync(input);
            if (result.Success && result.Tracks.Any())
            {
                yield return new ImportBatchResult
                {
                    Tracks = result.Tracks,
                    SourceTitle = result.SourceTitle,
                    TotalEstimated = result.Tracks.Count
                };
            }
        }
    }
}
