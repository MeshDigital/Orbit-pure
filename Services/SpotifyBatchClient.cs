using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web; // For response models if needed, or we can use dynamic/custom

namespace SLSKDONET.Services;

/// <summary>
/// A thread-safe, throttled, and batched client for Spotify API requests.
/// Serializes all requests through a single Semaphore to prevent 429s.
/// Handles Retry-After headers and exponential backoff automatically.
/// </summary>
public class SpotifyBatchClient
{
    private readonly HttpClient _http;
    private readonly ILogger<SpotifyBatchClient> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Shared JSON options ensure enum values like ItemType deserialize from strings ("track").
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public SpotifyBatchClient(HttpClient http, ILogger<SpotifyBatchClient> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// updates the bearer token for the underlying HttpClient.
    /// </summary>
    public void SetAccessToken(string token)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent", "Orbit/1.2.2 (SLSKDONET)");
        }
    }

    private DateTime _forbiddenUntil = DateTime.MinValue;

    public async Task<T> GetAsync<T>(string url, CancellationToken ct = default)
    {
        // Global Circuit Breaker check
        if (DateTime.UtcNow < _forbiddenUntil)
        {
            _log.LogDebug("Circuit Breaker Open: Skipping Spotify request due to recent 403.");
            // throwing specifically allows callers to handle "skipped" vs "failed"
            throw new InvalidOperationException("Spotify Circuit Breaker Open (403)");
        }

        // Global lock: Serialize ALL Spotify calls from this client instance.
        await _lock.WaitAsync(ct); 
        try
        {
            int attempt = 0;
            // Exponential backoff up to a point, then fail? 
            // The prompt says "while (true)", implying infinite retry for 429s/transient.
            // We'll stick to the user's robust loop.

            while (true)
            {
                // Re-check inside lock in case another thread tripped it
                if (DateTime.UtcNow < _forbiddenUntil)
                {
                     throw new InvalidOperationException("Spotify Circuit Breaker Open (403)");
                }

                var response = await _http.GetAsync(url, ct);

                if (response.StatusCode == (HttpStatusCode)429)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 2; // Default to 2s if missing
                    _log.LogWarning("Spotify rate limit hit (429). Waiting {RetryAfter}s", retryAfter);

                    await Task.Delay(TimeSpan.FromSeconds(retryAfter), ct);
                    continue; // Retry the same request
                }

                if (!response.IsSuccessStatusCode)
                {
                    attempt++;
                     if (attempt > 3 || response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                         var errorContent = await response.Content.ReadAsStringAsync(ct);
                         
                         if (response.StatusCode == HttpStatusCode.Forbidden)
                         {
                             _log.LogWarning("Spotify request forbidden (403). Trip Circuit Breaker for 5 minutes. Url: {Url}. Reason: {Content}", url, errorContent);
                             _forbiddenUntil = DateTime.UtcNow.AddMinutes(5);
                             throw new InvalidOperationException("Spotify request forbidden (403)");
                         }
                         else
                         {
                             _log.LogError("Spotify request failed permanently. Url: {Url}, Status: {Status}, Content: {Content}", url, response.StatusCode, errorContent);
                         }
                         
                         response.EnsureSuccessStatusCode(); // Will throw HttpRequestException
                    }

                    var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)); // Exponential backoff
                    _log.LogWarning("Transient Spotify error {Status}. Retrying in {Delay}ms (Attempt {Attempt})",
                        response.StatusCode, delay.TotalMilliseconds, attempt);

                    await Task.Delay(delay, ct);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<T>(json, JsonOptions)!;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Batches a list of IDs into chunks and executes the fetch function for each chunk.
    /// Enforces a small delay between chunks.
    /// </summary>
    public async Task<List<T>> BatchAsync<T>(IEnumerable<string> ids, int batchSize, Func<string, Task<T>> fetch)
    {
        var results = new List<T>();
        var distinctIds = ids.Distinct().ToList();

        if (!distinctIds.Any()) return results;

        foreach (var chunk in distinctIds.Chunk(batchSize))
        {
            try 
            {
                // Join IDs with comma for the API query
                var idString = string.Join(",", chunk);
                
                // Execute the fetch strategy (which calls GetAsync internally)
                var result = await fetch(idString);
                
                if (result != null)
                {
                    results.Add(result);
                }

                // Safety delay between batches to be nice to the API
                await Task.Delay(150); 
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("403") || (ex.InnerException?.Message.Contains("403") ?? false) || ex.Message.Contains("Circuit Breaker"))
                {
                    _log.LogDebug("Skipping batch chunk: Circuit Breaker Open or Forbidden (403).");
                    break; // Stop trying subsequent chunks
                }
                else
                {
                    _log.LogWarning("Failed to fetch batch chunk. Skipping. Error: {Message}", ex.Message);
                }
                // Continue to next chunk for non-403 errors
            }
        }

        return results;
    }
}
