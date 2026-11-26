using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Utils;
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
            var albumArtist = fullAlbum.Artists?.FirstOrDefault()?.Name;
            var total = fullAlbum.Tracks.Total ?? 0;
            
            // Note: Albums.GetTracks returns SimpleTrack (no album object inside)
            // So we pass 'fullAlbum' to mapping helper
            var request = new AlbumTracksRequest { Limit = 50 };
            var items = await client.Albums.GetTracks(albumId, request);
            var batch = new List<SearchQuery>();

            await foreach (var simpleTrack in client.Paginate(items))
            {
                var mapped = MapSimpleTrackToSearchQuery(simpleTrack, fullAlbum, total);
                mapped.SourceOwner = albumArtist;
                batch.Add(mapped);

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
            var playlistOwner = playlist.Owner?.DisplayName;
            
            var request = new PlaylistGetItemsRequest { Limit = PlaylistItemsPageSize };
            var items = await client.Playlists.GetPlaylistItems(playlistId, request);
            var total = items.Total ?? 0;
            var batch = new List<SearchQuery>();
            
            await foreach (var item in client.Paginate(items))
                {
                    if (ExtractFullTrack(item) is { } track)
                    {
                        var mapped = MapToSearchQuery(track, playlistName, total, playlistId);
                        mapped.SourceOwner = playlistOwner;
                        batch.Add(mapped);
                        if (batch.Count >= PlaylistItemsPageSize)
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
            queries.Add(MapSimpleTrackToSearchQuery(simpleTrack, fullAlbum, total, albumId));
        }
        return queries;
    }

	/// <summary>Max playlist page size under Spotify's Feb 2026 dev-mode rules (/playlists/{id}/items).</summary>
	public const int PlaylistItemsPageSize = 50;

	/// <summary>Max search limit under Spotify's Feb 2026 dev-mode rules (GET /search).</summary>
	public const int MaxSearchLimit = 10;

	/// <summary>
	/// Extracts the <see cref="FullTrack"/> from a playlist item. Spotify's Feb 2026
	/// <c>/playlists/{id}/items</c> endpoint populates <see cref="PlaylistTrack{T}.Item"/>
	/// rather than the deprecated <c>Track</c> field.
	/// </summary>
	internal static FullTrack? ExtractFullTrack(PlaylistTrack<IPlayableItem> item) =>
		item.Item as FullTrack;

	/// <summary>Clamps a requested Spotify search limit to the Feb 2026 max of 10.</summary>
	internal static int ClampSearchLimit(int requested) => Math.Clamp(requested, 1, MaxSearchLimit);

	/// <summary>Builds a canonical Spotify playlist URL directly from its ID.</summary>
	internal static string BuildPlaylistUrl(string? playlistId, Dictionary<string, string>? externalUrls = null)
	{
		if (!string.IsNullOrWhiteSpace(playlistId))
			return $"https://open.spotify.com/playlist/{playlistId}";
		return externalUrls != null && externalUrls.TryGetValue("spotify", out var url) ? url : string.Empty;
	}

	private async Task<List<SearchQuery>> FetchPlaylistTracksAsync(SpotifyClient client, string playlistId)
	{
		var queries = new List<SearchQuery>();
		var playlist = await client.Playlists.Get(playlistId);
		
		var request = new PlaylistGetItemsRequest { Limit = PlaylistItemsPageSize };
		var items = await client.Playlists.GetPlaylistItems(playlistId, request);
		var total = items.Total ?? 0;

		await foreach (var item in client.Paginate(items))
			{
				if (ExtractFullTrack(item) is { } track)
				{
					queries.Add(MapToSearchQuery(track, playlist.Name ?? "Spotify Playlist", total, playlistId));
				}
			}


		return queries;
	}

	private SearchQuery MapToSearchQuery(FullTrack track, string sourceTitle, int total, string? playlistId = null)
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
			Popularity = 0, // Removed in Spotify Feb 2026 dev-mode API
			CanonicalDuration = track.DurationMs,
			ReleaseDate = DateTime.TryParse(track.Album?.ReleaseDate, out var rd) ? rd : null,
            ISRC = null, // ExternalIds marked removed in SpotifyAPI.Web 7.4.2
            SpotifyPlaylistId = playlistId,
            SpotifyUri = track.Uri,
            IsEnriched = false,
            TrackHash = TrackHashUtil.Compute(artist, title)
		};
	}

    private SearchQuery MapSimpleTrackToSearchQuery(SimpleTrack track, FullAlbum album, int total, string? playlistId = null)
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
            Popularity = 0, // Removed in Spotify Feb 2026 dev-mode API
            CanonicalDuration = track.DurationMs,
            ReleaseDate = DateTime.TryParse(album.ReleaseDate, out var rd) ? rd : null,
            SpotifyPlaylistId = playlistId,
            SpotifyUri = track.Uri,
            IsEnriched = false,
            TrackHash = TrackHashUtil.Compute(artist, title)
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

    public async Task<List<SpotifyPlaylistViewModel>> SearchPlaylistsAsync(string query, int limit = MaxSearchLimit)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        
        var client = await GetClientAsync();
        var searchRequest = new SearchRequest(SearchRequest.Types.Playlist, query) { Limit = ClampSearchLimit(limit) };
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
                    TrackCount = p.Items?.Total ?? 0,
                    Owner = p.Owner?.DisplayName ?? "Unknown",
                    Url = BuildPlaylistUrl(p.Id, p.ExternalUrls)
                });
            }
        }
        return results;
    }

    public async Task<List<SpotifyPlaylistViewModel>> SearchAlbumsAsync(string query, int limit = MaxSearchLimit)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();

        var client = await GetClientAsync();
        var searchRequest = new SearchRequest(SearchRequest.Types.Album, query) { Limit = ClampSearchLimit(limit) };
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
                    Url = !string.IsNullOrWhiteSpace(a.Id)
                        ? $"https://open.spotify.com/album/{a.Id}"
                        : (a.ExternalUrls != null && a.ExternalUrls.ContainsKey("spotify")) ? a.ExternalUrls["spotify"] : ""
                });
            }
        }
        return results;
    }
}
