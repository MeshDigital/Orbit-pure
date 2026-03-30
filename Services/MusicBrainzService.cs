using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Models;
using SLSKDONET.Services.Repositories;

namespace SLSKDONET.Services;

/// <summary>
/// Service for interacting with the MusicBrainz API to fetch deep metadata and resolve ISRCs.
/// Implements rate limiting and caching according to MusicBrainz API guidelines (max 1 req/sec).
/// 
/// Capabilities:
///   - ISRC → Recording MBID resolution
///   - Artist + title fuzzy search (no ISRC required)
///   - Producer / mixer / engineer credits
///   - Composer / lyricist / songwriter credits
///   - Community genre/mood tags
///   - Record label extraction
///   - Release date extraction
/// </summary>
public class MusicBrainzService : IMusicBrainzService
{
    private readonly ILogger<MusicBrainzService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly ITrackRepository _trackRepository;
    private readonly HttpClient _httpClient;

    private const string BaseUrl = "https://musicbrainz.org/ws/2/";
    private const string UserAgent = "ORBIT-Music-Engine/1.0.0 ( https://github.com/MeshDigital/ORBIT )";

    // Respect MusicBrainz guidelines: max 1 request per second.
    private readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);
    private DateTime _lastRequestTime = DateTime.MinValue;
    private const int MinRequestIntervalMs = 1100; // Slightly over 1 s to be safe

    public MusicBrainzService(
        ILogger<MusicBrainzService> logger,
        DatabaseService databaseService,
        ITrackRepository trackRepository)
    {
        _logger = logger;
        _databaseService = databaseService;
        _trackRepository = trackRepository;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    // ---------------------------------------------------------------------------
    // Rate limiting helper
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Enforces the MusicBrainz rate limit (1 request/second).
    /// All HTTP calls must go through this method.
    /// </summary>
    private async Task<HttpResponseMessage> GetWithRateLimitAsync(string url, CancellationToken ct = default)
    {
        await _rateLimitSemaphore.WaitAsync(ct);
        try
        {
            var elapsed = (DateTime.UtcNow - _lastRequestTime).TotalMilliseconds;
            if (elapsed < MinRequestIntervalMs)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(MinRequestIntervalMs - elapsed), ct);
            }

            _lastRequestTime = DateTime.UtcNow;
            return await _httpClient.GetAsync(url, ct);
        }
        finally
        {
            _rateLimitSemaphore.Release();
        }
    }

    // ---------------------------------------------------------------------------
    // ISRC resolution
    // ---------------------------------------------------------------------------

    public async Task<string?> ResolveMbidFromIsrcAsync(string isrc)
    {
        if (string.IsNullOrEmpty(isrc)) return null;

        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var url = $"{BaseUrl}isrc/{isrc}?fmt=json";
                var response = await GetWithRateLimitAsync(url);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogDebug("ISRC {Isrc} not found in MusicBrainz", isrc);
                    return null;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("MusicBrainz Rate Limit HIT (Attempt {Attempt}/{Max})", attempt, maxRetries);
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                        continue;
                    }
                }

                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                // Extract the first recording ID from the recordings array
                if (doc.RootElement.TryGetProperty("recordings", out var recordings) &&
                    recordings.GetArrayLength() > 0)
                {
                    var mbid = recordings[0].GetProperty("id").GetString();
                    _logger.LogInformation("Resolved ISRC {Isrc} to MBID {Mbid}", isrc, mbid);
                    return mbid;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Attempt {Attempt}/{Max} failed for ISRC {Isrc}", attempt, maxRetries, isrc);
                if (attempt == maxRetries)
                {
                    _logger.LogError(ex, "MusicBrainz failed after {Max} attempts for ISRC {Isrc}", maxRetries, isrc);
                    return null;
                }
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------------
    // Recording detail / credits
    // ---------------------------------------------------------------------------

    public async Task<MusicBrainzCredits?> GetCreditsAsync(string mbid)
    {
        if (string.IsNullOrEmpty(mbid)) return null;

        try
        {
            // Include artist-rels (producers, engineers, composers, etc.),
            // work-rels (for work-level composer/lyricist lookups via the work entity),
            // releases (for label + release date), and tags (community genres).
            var url = $"{BaseUrl}recording/{mbid}?inc=artist-rels+work-rels+releases+label-rels+tags&fmt=json";
            var response = await GetWithRateLimitAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var credits = new MusicBrainzCredits
            {
                RecordingId = mbid,
                RecordingTitle = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null,
                DurationMs = root.TryGetProperty("length", out var lenProp) && lenProp.ValueKind != JsonValueKind.Null
                    ? lenProp.GetInt32()
                    : null
            };

            // Artist credit (primary performer)
            if (root.TryGetProperty("artist-credit", out var artistCredit) && artistCredit.GetArrayLength() > 0)
            {
                var firstCredit = artistCredit[0];
                if (firstCredit.TryGetProperty("artist", out var artistEl))
                    credits.ArtistName = artistEl.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            }

            // Release info: pick the earliest release to get the original release date and label
            if (root.TryGetProperty("releases", out var releases) && releases.GetArrayLength() > 0)
            {
                ExtractReleaseInfo(releases, credits);
            }

            // Community genre/mood tags — only include tags voted on by at least 2 users
            if (root.TryGetProperty("tags", out var tags))
            {
                foreach (var tag in tags.EnumerateArray())
                {
                    var count = tag.TryGetProperty("count", out var countProp) ? countProp.GetInt32() : 0;
                    var name = tag.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    if (!string.IsNullOrEmpty(name) && count >= 2)
                        credits.Tags.Add(name);
                }
            }

            // Artist relationships on the recording (producers, engineers, composers, etc.)
            if (root.TryGetProperty("relations", out var relations))
            {
                ExtractRelationCredits(relations, credits);
            }

            return credits;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch credits for MBID {Mbid}", mbid);
            return null;
        }
    }

    // ---------------------------------------------------------------------------
    // Search by artist + title
    // ---------------------------------------------------------------------------

    public async Task<MusicBrainzCredits?> SearchByArtistTitleAsync(string artist, string title)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            return null;

        try
        {
            // MusicBrainz Lucene query: recording title + artist name
            var query = Uri.EscapeDataString($"recording:\"{title}\" AND artist:\"{artist}\"");
            var url = $"{BaseUrl}recording?query={query}&limit=5&fmt=json";

            var response = await GetWithRateLimitAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("MusicBrainz Rate Limit HIT during search for {Artist} - {Title}", artist, title);
                return null;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("recordings", out var recordings) ||
                recordings.GetArrayLength() == 0)
            {
                _logger.LogDebug("No MusicBrainz recording found for {Artist} - {Title}", artist, title);
                return null;
            }

            // Pick the best match: MusicBrainz returns results ordered by relevance score.
            // We additionally verify the score is high enough (≥ 85 out of 100).
            var best = recordings[0];
            var score = best.TryGetProperty("score", out var scoreProp) ? scoreProp.GetInt32() : 0;

            if (score < 85)
            {
                _logger.LogDebug(
                    "MusicBrainz search for {Artist} - {Title}: best score {Score} is below threshold (85), skipping",
                    artist, title, score);
                return null;
            }

            var mbid = best.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrEmpty(mbid))
                return null;

            _logger.LogInformation(
                "MusicBrainz: found recording {Mbid} for {Artist} - {Title} (score {Score})",
                mbid, artist, title, score);

            // Build a basic credits object from the search result (no extra round-trip needed for key fields)
            var credits = new MusicBrainzCredits
            {
                RecordingId = mbid,
                RecordingTitle = best.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null,
                DurationMs = best.TryGetProperty("length", out var lenEl) && lenEl.ValueKind != JsonValueKind.Null
                    ? lenEl.GetInt32()
                    : null
            };

            // Primary artist
            if (best.TryGetProperty("artist-credit", out var artistCredit) && artistCredit.GetArrayLength() > 0)
            {
                var firstCredit = artistCredit[0];
                if (firstCredit.TryGetProperty("artist", out var artistEl))
                    credits.ArtistName = artistEl.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            }

            // ISRC(s) from search result — capture the primary ISRC so callers can store it
            string? resolvedIsrc = null;
            if (best.TryGetProperty("isrcs", out var isrcs) && isrcs.GetArrayLength() > 0)
            {
                resolvedIsrc = isrcs[0].GetString();
            }

            if (!string.IsNullOrEmpty(resolvedIsrc))
                credits.ISRC = resolvedIsrc;

            // Releases from the search result
            if (best.TryGetProperty("releases", out var releases) && releases.GetArrayLength() > 0)
            {
                ExtractReleaseInfo(releases, credits);
            }

            // Fetch the full recording detail to get credits and tags
            var full = await GetCreditsAsync(mbid);
            if (full != null)
            {
                // Merge full details into the search-result credits
                credits.Producers.AddRange(full.Producers);
                credits.Mixers.AddRange(full.Mixers);
                credits.Engineers.AddRange(full.Engineers);
                credits.Composers.AddRange(full.Composers);
                credits.Lyricists.AddRange(full.Lyricists);
                credits.Songwriters.AddRange(full.Songwriters);
                credits.Tags.AddRange(full.Tags);

                // Prefer full details for release info if richer
                if (string.IsNullOrEmpty(credits.Date) && !string.IsNullOrEmpty(full.Date))
                {
                    credits.Date = full.Date;
                    credits.ReleaseId = full.ReleaseId;
                    credits.ReleaseTitle = full.ReleaseTitle;
                }
                if (credits.Labels.Count == 0) credits.Labels.AddRange(full.Labels);
            }

            return credits;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MusicBrainz search failed for {Artist} - {Title}", artist, title);
            return null;
        }
    }

    // ---------------------------------------------------------------------------
    // High-level enrichment: by ISRC
    // ---------------------------------------------------------------------------

    public async Task<bool> EnrichTrackWithIsrcAsync(string trackUniqueHash, string isrc)
    {
        if (string.IsNullOrEmpty(isrc)) return false;

        var mbid = await ResolveMbidFromIsrcAsync(isrc);
        if (string.IsNullOrEmpty(mbid)) return false;

        var credits = await GetCreditsAsync(mbid);
        if (credits == null) return false;

        return await ApplyCreditsToTrackAsync(trackUniqueHash, isrc, credits);
    }

    // ---------------------------------------------------------------------------
    // High-level enrichment: by artist + title
    // ---------------------------------------------------------------------------

    public async Task<bool> EnrichTrackByNameAsync(string trackUniqueHash, string artist, string title)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return false;

        var credits = await SearchByArtistTitleAsync(artist, title);
        if (credits == null) return false;

        return await ApplyCreditsToTrackAsync(trackUniqueHash, null, credits);
    }

    // ---------------------------------------------------------------------------
    // Internal helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Applies a resolved <see cref="MusicBrainzCredits"/> object to all track instances in the database.
    /// </summary>
    private async Task<bool> ApplyCreditsToTrackAsync(string trackUniqueHash, string? isrc, MusicBrainzCredits credits)
    {
        try
        {
            // Build enrichment result with everything we have from MusicBrainz
            var enrichmentResult = new TrackEnrichmentResult
            {
                Success = true,
                ISRC = isrc ?? credits.ISRC,  // Use explicit ISRC, or one found via name search
                MusicBrainzId = credits.RecordingId,
                Label = credits.Labels.FirstOrDefault(),
                Genres = credits.Tags.Count > 0 ? credits.Tags : null
            };

            // Parse release date
            if (!string.IsNullOrEmpty(credits.Date) && DateTime.TryParse(credits.Date, out var releaseDate))
            {
                enrichmentResult.ReleaseDate = releaseDate;
            }

            await _trackRepository.UpdateAllInstancesMetadataAsync(trackUniqueHash, enrichmentResult);

            // Store full credits in AudioFeatures ProvenanceJson
            var audioFeatures = await _databaseService.GetAudioFeaturesByHashAsync(trackUniqueHash);
            if (audioFeatures != null)
            {
                var provenance = JsonSerializer.Deserialize<Dictionary<string, object>>(audioFeatures.ProvenanceJson ?? "{}") ?? new();
                provenance["MusicBrainzCredits"] = credits;
                audioFeatures.ProvenanceJson = JsonSerializer.Serialize(provenance);
                await _databaseService.UpdateAudioFeaturesAsync(audioFeatures);
            }

            _logger.LogInformation(
                "MusicBrainz enrichment applied for {Hash}: MBID={Mbid}, Label={Label}, Tags={Tags}, Composers={Composers}",
                trackUniqueHash, credits.RecordingId, enrichmentResult.Label,
                string.Join(", ", credits.Tags),
                string.Join(", ", credits.Composers));

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply MusicBrainz credits to track {Hash}", trackUniqueHash);
            return false;
        }
    }

    /// <summary>
    /// Parses label and date from a MusicBrainz releases array.
    /// Prefers the earliest-dated release to get the original release year.
    /// </summary>
    private static void ExtractReleaseInfo(JsonElement releases, MusicBrainzCredits credits)
    {
        // Collect all releases with a date and pick the earliest
        string? bestDate = null;
        string? bestReleaseId = null;
        string? bestReleaseTitle = null;
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in releases.EnumerateArray())
        {
            var dateStr = rel.TryGetProperty("date", out var dateProp) ? dateProp.GetString() : null;
            var relId = rel.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var relTitle = rel.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;

            // Pick the earliest release
            if (!string.IsNullOrEmpty(dateStr))
            {
                if (bestDate == null ||
                    string.Compare(dateStr, bestDate, StringComparison.Ordinal) < 0)
                {
                    bestDate = dateStr;
                    bestReleaseId = relId;
                    bestReleaseTitle = relTitle;
                }
            }

            // Collect label names across all releases (deduplicated)
            if (rel.TryGetProperty("label-info", out var labelInfo))
            {
                foreach (var li in labelInfo.EnumerateArray())
                {
                    if (li.TryGetProperty("label", out var labelEl) &&
                        labelEl.TryGetProperty("name", out var labelNameProp))
                    {
                        var labelName = labelNameProp.GetString();
                        if (!string.IsNullOrEmpty(labelName) && seenLabels.Add(labelName))
                            credits.Labels.Add(labelName);
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(bestDate)) credits.Date = bestDate;
        if (!string.IsNullOrEmpty(bestReleaseId)) credits.ReleaseId = bestReleaseId;
        if (!string.IsNullOrEmpty(bestReleaseTitle)) credits.ReleaseTitle = bestReleaseTitle;
    }

    /// <summary>
    /// Parses artist relationship credits (producer, engineer, composer, etc.) from a recording's
    /// relations array. Skips non-artist relations safely.
    /// </summary>
    private static void ExtractRelationCredits(JsonElement relations, MusicBrainzCredits credits)
    {
        foreach (var rel in relations.EnumerateArray())
        {
            var type = rel.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            // Only process artist relations; safely skip work, label, url relations
            if (!rel.TryGetProperty("artist", out var artistEl)) continue;

            var artistName = artistEl.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (string.IsNullOrEmpty(artistName)) continue;

            switch (type?.ToLowerInvariant())
            {
                case "producer":
                    credits.Producers.Add(artistName);
                    break;
                case "mix":
                case "remix":
                case "mix-dj":
                    credits.Mixers.Add(artistName);
                    break;
                case "engineer":
                case "mastering":
                case "recording":
                    credits.Engineers.Add(artistName);
                    break;
                case "composer":
                case "co-composer":
                    credits.Composers.Add(artistName);
                    break;
                case "lyricist":
                    credits.Lyricists.Add(artistName);
                    break;
                case "writer":
                case "songwriter":
                    credits.Songwriters.Add(artistName);
                    break;
            }
        }
    }
}
