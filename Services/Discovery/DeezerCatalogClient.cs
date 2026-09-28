using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Discovery;

/// <summary>
/// Deezer's public catalog API (no key, no login): artist search, related artists and artist top
/// tracks. Deezer allows about 50 requests per 5 seconds; this keeps at most 3 in flight.
/// </summary>
public sealed class DeezerCatalogClient
{
    private const string ApiBase = "https://api.deezer.com";
    private readonly HttpClient _http;
    private readonly ILogger<DeezerCatalogClient> _logger;
    private readonly SemaphoreSlim _gate = new(3, 3);

    public DeezerCatalogClient(HttpClient http, ILogger<DeezerCatalogClient> logger)
    {
        _http = http;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public sealed record DeezerArtist(long Id, string Name);

    /// <summary>The Deezer artist whose name matches <paramref name="name"/>, or null.</summary>
    public async Task<DeezerArtist?> FindArtistAsync(string name, CancellationToken ct = default)
    {
        var artists = await GetArtistsAsync($"/search/artist?q={Uri.EscapeDataString(name)}&limit=5", ct).ConfigureAwait(false);
        var wanted = PlaylistDiscoveryService.NormalizeArtist(name);
        return artists.FirstOrDefault(a => PlaylistDiscoveryService.NormalizeArtist(a.Name) == wanted);
    }

    public Task<List<DeezerArtist>> RelatedArtistsAsync(long artistId, int limit, CancellationToken ct = default) =>
        GetArtistsAsync($"/artist/{artistId}/related?limit={limit}", ct);

    public async Task<List<DiscoveryCandidate>> TopTracksAsync(long artistId, int limit, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"/artist/{artistId}/top?limit={limit}", ct).ConfigureAwait(false);
        return doc is null ? new() : ParseTracks(doc.RootElement);
    }

    /// <summary>Parses a Deezer <c>{ "data": [track…] }</c> payload. Public for tests.</summary>
    public static List<DiscoveryCandidate> ParseTracks(JsonElement root)
    {
        var list = new List<DiscoveryCandidate>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;

        foreach (var t in data.EnumerateArray())
        {
            var title = Str(t, "title_short") ?? Str(t, "title");
            var artist = t.TryGetProperty("artist", out var a) ? Str(a, "name") : null;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) continue;

            // Deezer puts the version ("VIP", "Extended Mix") in title_version as "(VIP)".
            var version = Str(t, "title_version")?.Trim().Trim('(', ')').Trim();

            var c = new DiscoveryCandidate
            {
                Artist = artist,
                Title = title,
                MixName = string.IsNullOrWhiteSpace(version) ? null : version,
                DeezerUrl = Str(t, "link"),
                PreviewUrl = Str(t, "preview") is { Length: > 0 } p ? p : null,
            };
            if (t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
                c.DurationSeconds = d.GetDouble();
            if (t.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
            {
                c.Album = Str(album, "title");
                c.ImageUrl = Str(album, "cover_medium") ?? Str(album, "cover");
            }
            list.Add(c);
        }
        return list;
    }

    private async Task<List<DeezerArtist>> GetArtistsAsync(string path, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(path, ct).ConfigureAwait(false);
        var list = new List<DeezerArtist>();
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var a in data.EnumerateArray())
        {
            if (a.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && Str(a, "name") is { } name)
                list.Add(new DeezerArtist(id.GetInt64(), name));
        }
        return list;
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var response = await _http.GetAsync(ApiBase + path, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Discover] Deezer {Path} returned {Status}", path, (int)response.StatusCode);
                return null;
            }
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            // Deezer reports quota and lookup failures as 200 with an "error" object.
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err))
            {
                _logger.LogWarning("[Discover] Deezer {Path} error: {Error}", path, err.ToString());
                doc.Dispose();
                return null;
            }
            return doc;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Discover] Deezer request failed: {Path}", path);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
