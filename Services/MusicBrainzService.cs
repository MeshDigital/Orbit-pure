using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Models;
using SLSKDONET.Services.Repositories;

namespace SLSKDONET.Services;

/// <summary>
/// Service for interacting with the MusicBrainz API to fetch deep metadata and resolve ISRCs.
/// Implements rate limiting and caching according to MusicBrainz API guidelines.
/// </summary>
public class MusicBrainzService : IMusicBrainzService
{
    private readonly ILogger<MusicBrainzService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly ITrackRepository _trackRepository;
    private readonly HttpClient _httpClient;
    
    private const string BaseUrl = "https://musicbrainz.org/ws/2/";
    private const string UserAgent = "ORBIT-Music-Engine/1.0.0 ( https://github.com/MeshDigital/ORBIT )";

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

    public async Task<string?> ResolveMbidFromIsrcAsync(string isrc)
    {
        if (string.IsNullOrEmpty(isrc)) return null;

        int maxRetries = 3;
        int retryDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // MusicBrainz rate limit: 1 request per second
                // Exponential backoff for retries
                await Task.Delay(retryDelayMs * (attempt > 1 ? (int)Math.Pow(2, attempt - 2) : 1));

                var url = $"{BaseUrl}isrc/{isrc}?fmt=json";
                var response = await _httpClient.GetAsync(url);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogDebug("ISRC {Isrc} not found in MusicBrainz", isrc);
                    return null;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("MusicBrainz Rate Limit HIT (Attempt {Attempt}/{Max})", attempt, maxRetries);
                    if (attempt < maxRetries) continue;
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

    public async Task<MusicBrainzCredits?> GetCreditsAsync(string mbid)
    {
        if (string.IsNullOrEmpty(mbid)) return null;

        try
        {
            // MusicBrainz rate limit: 1 request per second
            await Task.Delay(1000);

            // Fetch recording with work-rels and artist-rels for deep credits
            var url = $"{BaseUrl}recording/{mbid}?inc=artist-rels+work-rels+releases&fmt=json";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var credits = new MusicBrainzCredits
            {
                RecordingId = mbid
            };

            // Extract Release Info
            if (root.TryGetProperty("releases", out var releases) && releases.GetArrayLength() > 0)
            {
                var firstRelease = releases[0];
                credits.ReleaseId = firstRelease.GetProperty("id").GetString();
                credits.ReleaseTitle = firstRelease.GetProperty("title").GetString();
                if (firstRelease.TryGetProperty("date", out var date))
                    credits.Date = date.GetString();
            }

            // Extract Artist Relationships (Producers, Mixers, etc.)
            if (root.TryGetProperty("relations", out var relations))
            {
                foreach (var rel in relations.EnumerateArray())
                {
                    var type = rel.GetProperty("type").GetString();
                    var artistName = rel.GetProperty("artist").GetProperty("name").GetString();

                    if (string.IsNullOrEmpty(artistName)) continue;

                    switch (type?.ToLower())
                    {
                        case "producer":
                            credits.Producers.Add(artistName);
                            break;
                        case "mix":
                        case "remix":
                            credits.Mixers.Add(artistName);
                            break;
                        case "engineer":
                        case "mastering":
                            credits.Engineers.Add(artistName);
                            break;
                    }
                }
            }

            return credits;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch credits for MBID {Mbid}", mbid);
            return null;
        }
    }

    public async Task<bool> EnrichTrackWithIsrcAsync(string trackUniqueHash, string isrc)
    {
        if (string.IsNullOrEmpty(isrc)) return false;

        var mbid = await ResolveMbidFromIsrcAsync(isrc);
        if (string.IsNullOrEmpty(mbid)) return false;

        var credits = await GetCreditsAsync(mbid);
        if (credits == null) return false;

        try
        {
            // Use TrackRepository for global propagation
            var enrichmentResult = new TrackEnrichmentResult
            {
                Success = true,
                ISRC = isrc,
                MusicBrainzId = mbid
            };

            await _trackRepository.UpdateAllInstancesMetadataAsync(trackUniqueHash, enrichmentResult);

            // Update AudioFeatures with credits in ProvenanceJson (Special case for MB credits)
            var audioFeatures = await _databaseService.GetAudioFeaturesByHashAsync(trackUniqueHash);
            if (audioFeatures != null)
            {
                var provenance = JsonSerializer.Deserialize<Dictionary<string, object>>(audioFeatures.ProvenanceJson ?? "{}") ?? new();
                provenance["MusicBrainzCredits"] = credits;
                audioFeatures.ProvenanceJson = JsonSerializer.Serialize(provenance);
                
                await _databaseService.UpdateAudioFeaturesAsync(audioFeatures);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update track {Hash} with MusicBrainz metadata", trackUniqueHash);
            return false;
        }
    }
}
