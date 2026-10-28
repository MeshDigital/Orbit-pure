using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SpotifyAPI.Web;

namespace SLSKDONET.Services.ImportProviders;

/// <summary>
/// Import provider for Spotify playlists. Uses the Web API when Spotify is connected and falls back
/// to the public web page scraper when it isn't, or when the API refuses the link (an account that
/// isn't allowlisted on a development-mode Client ID, or a playlist the API won't share).
/// </summary>
public class SpotifyImportProvider : IStreamingImportProvider
{
    private readonly ILogger<SpotifyImportProvider> _logger;

    public string Name => "Spotify";
    public string IconGlyph => "spotify";

    public SpotifyImportProvider(
        ILogger<SpotifyImportProvider> logger,
        SpotifyInputSource spotifyInputSource,
        SpotifyScraperInputSource spotifyScraperInputSource,
        Configuration.AppConfig config)
    {
        _logger = logger;
        IsApiConfigured = () => spotifyInputSource.IsConfigured;
        ApiParseAsync = spotifyInputSource.ParseAsync;
        ApiParseStream = spotifyInputSource.ParseStreamAsync;
        ScrapeAsync = spotifyScraperInputSource.ParseAsync;
    }

    // Seams over the two concrete sources (neither is mockable) so the fallback can be tested.
    internal Func<bool> IsApiConfigured { get; set; }
    internal Func<string, Task<List<SearchQuery>>> ApiParseAsync { get; set; }
    internal Func<string, IAsyncEnumerable<List<SearchQuery>>> ApiParseStream { get; set; }
    internal Func<string, Task<List<SearchQuery>>> ScrapeAsync { get; set; }

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

            List<SearchQuery> tracks;
            if (IsApiConfigured())
            {
                try
                {
                    tracks = await ApiParseAsync(playlistUrl);
                }
                catch (Exception apiEx) when (CanFallBackToScraper(apiEx, playlistUrl))
                {
                    LogFallback(apiEx, playlistUrl);
                    tracks = await ScrapeOrRethrowApiErrorAsync(playlistUrl, apiEx);
                }
            }
            else
            {
                tracks = await ScrapeAsync(playlistUrl);
            }

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
                ErrorMessage = $"Spotify import failed: {SpotifyImportErrors.Translate(ex).Message}"
            };
        }
    }

    public async IAsyncEnumerable<ImportBatchResult> ImportStreamAsync(string input)
    {
        if (IsApiConfigured())
        {
            Exception? apiFailure = null;
            var yieldedAny = false;
            await using (var batches = ApiParseStream(input).GetAsyncEnumerator())
            {
                while (true)
                {
                    List<SearchQuery> batch;
                    try
                    {
                        if (!await batches.MoveNextAsync()) break;
                        batch = batches.Current;
                    }
                    // Only before the first batch: switching sources mid-playlist would duplicate rows.
                    catch (Exception ex) when (!yieldedAny && CanFallBackToScraper(ex, input))
                    {
                        apiFailure = ex;
                        break;
                    }

                    yieldedAny = true;
                    yield return new ImportBatchResult
                    {
                        Tracks = batch,
                        SourceTitle = batch.FirstOrDefault()?.SourceTitle ?? "Spotify Playlist",
                        SourceOwner = batch.FirstOrDefault()?.SourceOwner,
                        TotalEstimated = batch.FirstOrDefault()?.TotalTracks ?? 0
                    };
                }
            }

            if (apiFailure == null) yield break;

            LogFallback(apiFailure, input);
            var scraped = await ScrapeOrRethrowApiErrorAsync(input, apiFailure);
            yield return ScrapedBatch(scraped);
            yield break;
        }

        // Not connected: the public web page is the only source.
        List<SearchQuery> tracks;
        try
        {
            tracks = await ScrapeAsync(input);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spotify public-page import failed for {Url}", input);
            tracks = new List<SearchQuery>();
        }

        if (tracks.Count == 0)
        {
            // Not connected and the public page failed too (private playlist, consent wall, bad
            // link). Connecting the account is the usual fix.
            throw new ImportSourceException(
                "Couldn't read this Spotify link from its public page. Connect Spotify to import private or large playlists.",
                requiresSignIn: true);
        }

        yield return ScrapedBatch(tracks);
    }

    /// <summary>
    /// API refusals the public page can still answer: an account the Client ID won't serve
    /// (403 "not registered"), a playlist the API hides (403/404, e.g. Spotify-made mixes), or a
    /// dead session (401). Liked Songs only exist behind the API.
    /// </summary>
    internal static bool CanFallBackToScraper(Exception ex, string input)
    {
        if (input.Equals("liked", StringComparison.OrdinalIgnoreCase) || input.Contains("liked-songs", StringComparison.OrdinalIgnoreCase))
            return false;
        if (SpotifyImportErrors.IsAccountNotAllowlisted(ex)) return true;
        return ex is APIException { Response.StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound };
    }

    private void LogFallback(Exception apiEx, string input)
    {
        _logger.LogWarning("Spotify API refused {Url} ({Reason}); trying the public web page instead",
            input, SpotifyImportErrors.IsAccountNotAllowlisted(apiEx) ? "account not allowlisted for this Client ID" : apiEx.Message);
    }

    /// <summary>When the public page fails too, the API's reason is the one worth showing.</summary>
    private async Task<List<SearchQuery>> ScrapeOrRethrowApiErrorAsync(string input, Exception apiFailure)
    {
        List<SearchQuery> tracks;
        try
        {
            tracks = await ScrapeAsync(input);
        }
        catch (Exception scrapeEx)
        {
            _logger.LogWarning(scrapeEx, "Spotify public-page fallback failed for {Url}", input);
            tracks = new List<SearchQuery>();
        }

        if (tracks.Count == 0)
            throw SpotifyImportErrors.Translate(apiFailure);
        return tracks;
    }

    private static ImportBatchResult ScrapedBatch(List<SearchQuery> tracks) => new()
    {
        Tracks = tracks,
        SourceTitle = tracks.FirstOrDefault()?.SourceTitle ?? "Spotify Playlist",
        TotalEstimated = tracks.Count
    };
}
