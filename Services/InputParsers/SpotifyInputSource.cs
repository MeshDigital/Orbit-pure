using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SpotifyAPI.Web;

namespace SLSKDONET.Services.InputParsers;

/// <summary>
/// Spotify API-based playlist/album fetcher (Client Credentials flow).
/// </summary>
public class SpotifyInputSource : IInputSource
{
	private readonly ILogger<SpotifyInputSource> _logger;
	private readonly AppConfig _config;
	private readonly SpotifyAuthService _spotifyAuth;

	public InputType InputType => InputType.Spotify;

	public bool IsConfigured => 
		_spotifyAuth.IsAuthenticated || 
		(!string.IsNullOrWhiteSpace(_config.SpotifyClientId) && !string.IsNullOrWhiteSpace(_config.SpotifyClientSecret));

	public SpotifyInputSource(ILogger<SpotifyInputSource> logger, AppConfig config, SpotifyAuthService spotifyAuth)
	{
		_logger = logger;
		_config = config;
		_spotifyAuth = spotifyAuth;
	}

    public async Task<List<SearchQuery>> ParseAsync(string url)
	{
		if (!IsConfigured)
			throw new InvalidOperationException("Spotify API is not configured or authenticated.");

		_logger.LogInformation("Spotify API: parsing {Url}", url);

		var client = await GetClientAsync();
		var queries = new List<SearchQuery>();

		if (url.Equals("liked", StringComparison.OrdinalIgnoreCase) || url.Contains("liked-songs"))
		{
			queries = await FetchLikedSongsAsync(client);
		}
        else if (IsAlbumUrl(url))
        {
            var albumId = ExtractAlbumId(url);
            if (string.IsNullOrEmpty(albumId))
                throw new InvalidOperationException("Invalid Spotify album URL.");
            queries = await FetchAlbumTracksAsync(client, albumId);
        }
		else
		{
			var playlistId = ExtractPlaylistId(url);
			if (string.IsNullOrEmpty(playlistId))
				throw new InvalidOperationException("Invalid Spotify playlist URL.");

			queries = await FetchPlaylistTracksAsync(client, playlistId);
		}

		_logger.LogInformation("Spotify API: extracted {Count} tracks", queries.Count);
		return queries;
	}

    public async IAsyncEnumerable<List<SearchQuery>> ParseStreamAsync(string url)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Spotify API is not configured or authenticated.");

        _logger.LogInformation("Spotify API: parsing stream {Url}", url);

        var client = await GetClientAsync();

        if (url.Equals("liked", StringComparison.OrdinalIgnoreCase) || url.Contains("liked-songs"))
        {
            // ... Liked Songs Logic (Unchanged) ...
            var request = new LibraryTracksRequest { Limit = 50 };
            var response = await client.Library.GetTracks(request);
            var total = response.Total ?? 0;
            var batch = new List<SearchQuery>();

            await foreach (var item in client.Paginate(response))
            {
                if (item.Track != null)
                {
                    batch.Add(MapToSearchQuery(item.Track, "Liked Songs", total));
                    if (batch.Count >= 50)
                    {
                         yield return new List<SearchQuery>(batch);
                         batch.Clear();
                    }
                }
            }
            if (batch.Any()) yield return batch;
        }
        else if (IsAlbumUrl(url))
        {
            var albumId = ExtractAlbumId(url);
            if (string.IsNullOrEmpty(albumId))
                throw new InvalidOperationException("Invalid Spotify album URL.");

            var fullAlbum = await client.Albums.Get(albumId);
            var albumName = fullAlbum.Name ?? "Spotify Album";
            var total = fullAlbum.Tracks.Total ?? 0;
            
            // Note: Albums.GetTracks returns SimpleTrack (no album object inside)
            // So we pass 'fullAlbum' to mapping helper
            var request = new AlbumTracksRequest { Limit = 50 };
            var items = await client.Albums.GetTracks(albumId, request);
            var batch = new List<SearchQuery>();

            await foreach (var simpleTrack in client.Paginate(items))
            {
                batch.Add(MapSimpleTrackToSearchQuery(simpleTrack, fullAlbum, total));

                if (batch.Count >= 50)
                {
                    yield return new List<SearchQuery>(batch);
                    batch.Clear();
                }
            }
            if (batch.Any()) yield return batch;
        }
        else
        {
            var playlistId = ExtractPlaylistId(url);
            if (string.IsNullOrEmpty(playlistId))
                throw new InvalidOperationException("Invalid Spotify playlist URL.");

            // ... Playlist Logic (Unchanged) ...
            var playlist = await client.Playlists.Get(playlistId);
            var playlistName = playlist.Name ?? "Spotify Playlist";
            
            var request = new PlaylistGetItemsRequest { Limit = 100 };
            var items = await client.Playlists.GetItems(playlistId, request);
            var total = items.Total ?? 0;
            var batch = new List<SearchQuery>();
            
            await foreach (var item in client.Paginate(items))
                {
                    if (item.Track is FullTrack track)
                    {
                        batch.Add(MapToSearchQuery(track, playlistName, total));
                        if (batch.Count >= 50)
                        {
                             yield return new List<SearchQuery>(batch);
                             batch.Clear();
                        }
                    }
                }
                if (batch.Any()) yield return batch;
        }
    }

	private async Task<List<SearchQuery>> FetchLikedSongsAsync(SpotifyClient client)
	{
		var queries = new List<SearchQuery>();
		var request = new LibraryTracksRequest { Limit = 50 };
		var response = await client.Library.GetTracks(request);
		var total = response.Total ?? 0;

		await foreach (var item in client.Paginate(response))
		{
			if (item.Track != null)
			{
				queries.Add(MapToSearchQuery(item.Track, "Liked Songs", total));
			}
		}

		return queries;
	}

    private async Task<List<SearchQuery>> FetchAlbumTracksAsync(SpotifyClient client, string albumId)
    {
        var queries = new List<SearchQuery>();
        var fullAlbum = await client.Albums.Get(albumId);
        var request = new AlbumTracksRequest { Limit = 50 };
        var items = await client.Albums.GetTracks(albumId, request);
        var total = items.Total ?? 0;

        await foreach (var simpleTrack in client.Paginate(items))
        {
            queries.Add(MapSimpleTrackToSearchQuery(simpleTrack, fullAlbum, total));
        }
        return queries;
    }

	private async Task<List<SearchQuery>> FetchPlaylistTracksAsync(SpotifyClient client, string playlistId)
	{
		var queries = new List<SearchQuery>();
		var playlist = await client.Playlists.Get(playlistId);
		
		var request = new PlaylistGetItemsRequest { Limit = 100 };
		var items = await client.Playlists.GetItems(playlistId, request);
		var total = items.Total ?? 0;

		await foreach (var item in client.Paginate(items))
			{
				if (item.Track is FullTrack track)
				{
					queries.Add(MapToSearchQuery(track, playlist.Name ?? "Spotify Playlist", total));
				}
			}


		return queries;
	}

	private SearchQuery MapToSearchQuery(FullTrack track, string sourceTitle, int total)
	{
		var artist = track.Artists?.FirstOrDefault()?.Name ?? "Unknown Artist";
		var title = track.Name ?? "Unknown Track";
		
		return new SearchQuery
		{
			Artist = artist,
			Title = title,
			Album = track.Album?.Name,
			SourceTitle = sourceTitle,
			TotalTracks = total,
			DownloadMode = DownloadMode.Normal,
			SpotifyTrackId = track.Id,
			SpotifyAlbumId = track.Album?.Id,
			SpotifyArtistId = track.Artists?.FirstOrDefault()?.Id,
			AlbumArtUrl = track.Album?.Images?.FirstOrDefault()?.Url,
			Popularity = track.Popularity,
			CanonicalDuration = track.DurationMs,
			ReleaseDate = DateTime.TryParse(track.Album?.ReleaseDate, out var rd) ? rd : null,
            ISRC = track.ExternalIds != null && track.ExternalIds.ContainsKey("isrc") ? track.ExternalIds["isrc"] : null,
            IsEnriched = false 
		};
	}

    private SearchQuery MapSimpleTrackToSearchQuery(SimpleTrack track, FullAlbum album, int total)
    {
        var artist = track.Artists?.FirstOrDefault()?.Name ?? "Unknown Artist";
        var title = track.Name ?? "Unknown Track";

        return new SearchQuery
        {
            Artist = artist,
            Title = title,
            Album = album.Name, // Use FullAlbum name
            SourceTitle = album.Name,
            TotalTracks = total,
            DownloadMode = DownloadMode.Normal,
            SpotifyTrackId = track.Id,
            SpotifyAlbumId = album.Id,
            SpotifyArtistId = track.Artists?.FirstOrDefault()?.Id,
            AlbumArtUrl = album.Images?.FirstOrDefault()?.Url, // Use FullAlbum images
            Popularity = album.Popularity, // SimpleTrack lacks popularity, use Album's? Or fetch details? SimpleTrack usually lacks it.
            CanonicalDuration = track.DurationMs,
            ReleaseDate = DateTime.TryParse(album.ReleaseDate, out var rd) ? rd : null,
            // SimpleTrack doesn't provide ExternalIds usually, so ISRC might be missing here without fetch
            IsEnriched = false
        };
    }

	private async Task<SpotifyClient> GetClientAsync()
	{
		if (_spotifyAuth.IsAuthenticated)
		{
			return await _spotifyAuth.GetAuthenticatedClientAsync();
		}
		
		_logger.LogWarning("Spotify is not authenticated with User OAuth, falling back to Client Credentials...");
		var config = SpotifyClientConfig.CreateDefault()
            .WithRetryHandler(new SimpleRetryHandler() 
            { 
                RetryAfter = TimeSpan.FromSeconds(1), 
                RetryTimes = 3 
            });
		var request = new ClientCredentialsRequest(_config.SpotifyClientId!, _config.SpotifyClientSecret!);
		var response = await new OAuthClient(config).RequestToken(request);
		return new SpotifyClient(config.WithToken(response.AccessToken));
	}

    private bool IsAlbumUrl(string url)
    {
        return url.Contains("/album/") || url.StartsWith("spotify:album:");
    }

    private string? ExtractAlbumId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (url.StartsWith("spotify:album:", StringComparison.OrdinalIgnoreCase))
            return url.Replace("spotify:album:", "");

        if (url.Contains("spotify.com") && url.Contains("/album/"))
        {
            var parts = url.Split('/');
            var idx = Array.IndexOf(parts, "album");
            if (idx >= 0 && idx + 1 < parts.Length)
            {
                var id = parts[idx + 1].Split('?')[0];
                return (id.Length >= 20) ? id : null;
            }
        }
        return null;
    }

	public static string? ExtractPlaylistId(string url)
	{
		if (string.IsNullOrWhiteSpace(url)) return null;

		if (url.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase))
			return url.Replace("spotify:playlist:", "");

		if (url.Contains("spotify.com") && url.Contains("/playlist/"))
		{
			var parts = url.Split('/');
			var idx = Array.IndexOf(parts, "playlist");
			if (idx >= 0 && idx + 1 < parts.Length)
			{
				var id = parts[idx + 1].Split('?')[0];
				// Validation: Spotify IDs are typically 22 chars (base62). "64" is invalid.
				return (id.Length >= 20) ? id : null;
			}
		}

		return null;
	}

    public async Task<List<SpotifyPlaylistViewModel>> SearchPlaylistsAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        
        var client = await GetClientAsync();
        var searchRequest = new SearchRequest(SearchRequest.Types.Playlist, query) { Limit = limit };
        var response = await client.Search.Item(searchRequest);
        
        var results = new List<SpotifyPlaylistViewModel>();
        if (response.Playlists?.Items != null)
        {
            foreach (var p in response.Playlists.Items)
            {
                results.Add(new SpotifyPlaylistViewModel
                {
                    Id = p.Id ?? "",
                    Name = p.Name ?? "Unnamed Playlist",
                    ImageUrl = p.Images?.FirstOrDefault()?.Url ?? "",
                    TrackCount = p.Tracks?.Total ?? 0,
                    Owner = p.Owner?.DisplayName ?? "Unknown",
                    Url = (p.ExternalUrls != null && p.ExternalUrls.ContainsKey("spotify")) ? p.ExternalUrls["spotify"] : ""
                });
            }
        }
        return results;
    }

    public async Task<List<SpotifyPlaylistViewModel>> SearchAlbumsAsync(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();

        var client = await GetClientAsync();
        var searchRequest = new SearchRequest(SearchRequest.Types.Album, query) { Limit = limit };
        var response = await client.Search.Item(searchRequest);

        var results = new List<SpotifyPlaylistViewModel>();
        if (response.Albums?.Items != null)
        {
            foreach (var a in response.Albums.Items)
            {
                results.Add(new SpotifyPlaylistViewModel
                {
                    Id = a.Id ?? "",
                    Name = a.Name ?? "Unnamed Album",
                    ImageUrl = a.Images?.FirstOrDefault()?.Url ?? "",
                    TrackCount = a.TotalTracks,
                    Owner = a.Artists?.FirstOrDefault()?.Name ?? "Unknown Artist",
                    Url = (a.ExternalUrls != null && a.ExternalUrls.ContainsKey("spotify")) ? a.ExternalUrls["spotify"] : ""
                });
            }
        }
        return results;
    }
}
