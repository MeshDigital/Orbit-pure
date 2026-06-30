using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Calls the local EDMFormer Python microservice (port 7774) for ML-grade
/// EDM phrase detection (Intro / Build / Drop / Breakdown / Outro).
///
/// The service is fully optional — ORBIT falls back to the rule-based
/// PhraseAlignmentService when the microservice is not running.
///
/// Start the service with:
///   conda activate edmformer
///   python Tools\edmformer_server.py
/// </summary>
public sealed class EdmFormerService : IEdmFormerService, IDisposable
{
    private const string BaseUrl = "http://127.0.0.1:7774";
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly ILogger<EdmFormerService> _logger;
    private readonly SemaphoreSlim _healthLock = new(1, 1);
    private DateTimeOffset _lastHealthCheck = DateTimeOffset.MinValue;

    public bool IsAvailable { get; private set; }

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public EdmFormerService(ILogger<EdmFormerService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5), BaseAddress = new Uri(BaseUrl) };
        // Fire-and-forget initial health probe so IsAvailable is set quickly
        _ = RefreshAvailabilityAsync();
    }

    public async Task RefreshAvailabilityAsync()
    {
        await _healthLock.WaitAsync();
        try
        {
            var resp = await _http.GetAsync("/health");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadFromJsonAsync<HealthResponse>(_json);
                IsAvailable = body?.Status == "ready";
                if (IsAvailable)
                    _logger.LogInformation("[EDMFormer] Service ready on {url} (device: {device})", BaseUrl, body?.Device);
            }
            else
            {
                IsAvailable = false;
            }
        }
        catch
        {
            // Service not running — silently mark unavailable
            IsAvailable = false;
        }
        finally
        {
            _lastHealthCheck = DateTimeOffset.UtcNow;
            _healthLock.Release();
        }
    }

    public async Task<IReadOnlyList<PhraseSegment>?> AnalyzeAsync(string audioFilePath, CancellationToken ct = default)
    {
        // Re-check health every 60 s in case the service was started after app launch
        if (DateTimeOffset.UtcNow - _lastHealthCheck > HealthCheckInterval)
            await RefreshAvailabilityAsync();

        if (!IsAvailable)
            return null;

        try
        {
            // EDMFormer needs full analysis which can take 30-120s on CPU or 2-4s on GPU
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));

            using var innerHttp = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5),
                BaseAddress = new Uri(BaseUrl),
            };

            var resp = await innerHttp.PostAsJsonAsync(
                "/analyze",
                new AnalyzeRequest { AudioPath = audioFilePath },
                _json,
                cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[EDMFormer] /analyze returned {code}: {err}", (int)resp.StatusCode, err);
                return null;
            }

            var result = await resp.Content.ReadFromJsonAsync<AnalyzeResponse>(_json, ct);
            if (result?.Segments is null || result.Segments.Count == 0)
                return null;

            _logger.LogInformation("[EDMFormer] Got {n} segments for {path} in {t}s",
                result.Segments.Count, System.IO.Path.GetFileName(audioFilePath), result.ElapsedSeconds);

            return MapToPhraseSegments(result.Segments);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[EDMFormer] Analysis timed out for {path}", audioFilePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EDMFormer] Analysis failed for {path}", audioFilePath);
            IsAvailable = false;  // disable until next health check
            return null;
        }
    }

    private static IReadOnlyList<PhraseSegment> MapToPhraseSegments(List<EdmSegment> segments)
    {
        var result = new List<PhraseSegment>(segments.Count);
        foreach (var s in segments)
        {
            result.Add(new PhraseSegment
            {
                Label = s.Label,
                Start = s.Start,
                Duration = s.Duration,
                Confidence = 0.9f,  // EDMFormer is ML-graded; treat as high confidence
                Color = LabelToColor(s.Label),
            });
        }
        return result;
    }

    private static string LabelToColor(string label) => label switch
    {
        "Intro"     => "#00C8C8",
        "Build"     => "#DDB800",
        "Drop"      => "#DD2828",
        "Breakdown" => "#8C32C8",
        "Outro"     => "#00A0B4",
        "Silence"   => "#333333",
        _           => "#666688",
    };

    public void Dispose()
    {
        _http.Dispose();
        _healthLock.Dispose();
    }

    // ── DTOs ────────────────────────────────────────────────────────────────

    private sealed class HealthResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("device")] public string? Device { get; set; }
    }

    private sealed class AnalyzeRequest
    {
        [JsonPropertyName("audio_path")] public string AudioPath { get; set; } = string.Empty;
    }

    private sealed class AnalyzeResponse
    {
        [JsonPropertyName("segments")] public List<EdmSegment> Segments { get; set; } = new();
        [JsonPropertyName("elapsed_seconds")] public float ElapsedSeconds { get; set; }
    }

    private sealed class EdmSegment
    {
        [JsonPropertyName("label")]    public string Label    { get; set; } = string.Empty;
        [JsonPropertyName("start")]    public float  Start    { get; set; }
        [JsonPropertyName("end")]      public float  End      { get; set; }
        [JsonPropertyName("duration")] public float  Duration { get; set; }
    }
}
