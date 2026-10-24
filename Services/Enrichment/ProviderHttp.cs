using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services.Enrichment;

public enum FetchKind { Ok, NotFound, RateLimited, Forbidden, Failed, CircuitOpen, Disabled }

/// <summary>One HTTP answer, already classified for the breaker.</summary>
public sealed record HttpFetch(FetchKind Kind, string? Body = null, int StatusCode = 0, TimeSpan? RetryAfter = null, string? Error = null, bool FromCache = false, TimeSpan? PauseAfter = null)
{
    /// <summary>The matching <see cref="ProviderLookup"/> for anything but Ok/NotFound.</summary>
    public ProviderLookup ToLookupFailure() => Kind switch
    {
        FetchKind.RateLimited or FetchKind.CircuitOpen => ProviderLookup.Limited(RetryAfter, Error ?? "rate limited"),
        FetchKind.Forbidden or FetchKind.Disabled => ProviderLookup.Unavailable(Error ?? "access denied"),
        FetchKind.NotFound => ProviderLookup.NoMatch(),
        _ => ProviderLookup.Fail(Error ?? $"HTTP {StatusCode}"),
    };
}

/// <summary>
/// The one way providers talk HTTP: cache first, then the provider's circuit breaker, then its
/// rate limiter, then the request. Classifies the answer (429 + Retry-After, 401/403, 5xx, network
/// errors), feeds the breaker, logs failures (never silent) and caches 200/404 bodies.
/// </summary>
public sealed class ProviderHttp
{
    private readonly HttpClient _http;
    private readonly ProviderRuntime _runtime;
    private readonly ProviderRateLimiter _limiter;
    private readonly IMetadataLookupCache? _cache;
    private readonly ILogger _logger;

    public ProviderHttp(HttpClient http, ProviderRuntime runtime, IMetadataLookupCache? cache, ILogger logger, ProviderRateLimiter? limiter = null)
    {
        _http = http;
        _runtime = runtime;
        _limiter = limiter ?? runtime.Limiter;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// GET (or any request <paramref name="buildRequest"/> makes). <paramref name="cacheKey"/> null
    /// means "don't cache". <paramref name="inspect"/> may reclassify a 200 (Deezer reports its
    /// quota as 200 + error code; Discogs announces its remaining budget in headers).
    /// </summary>
    public async Task<HttpFetch> SendAsync(
        Func<HttpRequestMessage> buildRequest,
        string? cacheKey,
        CancellationToken ct,
        Func<HttpResponseMessage, string, HttpFetch?>? inspect = null)
    {
        if (cacheKey != null && _cache != null)
        {
            var cached = await _cache.GetAsync(_runtime.Name, cacheKey, ct).ConfigureAwait(false);
            if (cached != null)
            {
                _runtime.CountCacheHit();
                return cached.StatusCode == 200
                    ? new HttpFetch(FetchKind.Ok, cached.Payload, 200, FromCache: true)
                    : new HttpFetch(FetchKind.NotFound, null, cached.StatusCode, FromCache: true);
            }
        }

        var breaker = _runtime.Breaker;
        if (breaker.IsDisabledForSession)
            return new HttpFetch(FetchKind.Disabled, Error: breaker.DisabledReason ?? "disabled for this session");
        if (breaker.OpenUntil is { } until)
            return new HttpFetch(FetchKind.CircuitOpen, RetryAfter: until - DateTime.UtcNow, Error: "paused after a rate limit");

        await _limiter.WaitAsync(ct).ConfigureAwait(false);

        HttpResponseMessage response;
        try
        {
            using var request = buildRequest();
            _runtime.CountRequest();
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            breaker.RecordFailure();
            _runtime.LastError = ex is TaskCanceledException ? "timed out" : ex.Message;
            _logger.LogWarning(ex, "[Enrichment] {Provider} request failed", _runtime.Name);
            return new HttpFetch(FetchKind.Failed, Error: _runtime.LastError);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                breaker.RecordFailure();
                _runtime.LastError = ex.Message;
                _logger.LogWarning(ex, "[Enrichment] {Provider} response could not be read", _runtime.Name);
                return new HttpFetch(FetchKind.Failed, StatusCode: status, Error: ex.Message);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return RateLimited(RetryAfterOf(response), status, "HTTP 429");

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                var reason = response.StatusCode == HttpStatusCode.Unauthorized ? "the key or token was rejected (401)" : "access denied (403)";
                breaker.RecordForbidden(reason);
                _runtime.LastError = reason;
                _logger.LogWarning("[Enrichment] {Provider}: {Reason}{Disabled}", _runtime.Name, reason,
                    breaker.IsDisabledForSession ? " — disabled for this session" : string.Empty);
                return new HttpFetch(FetchKind.Forbidden, StatusCode: status, Error: reason);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                breaker.RecordSuccess();
                if (cacheKey != null && _cache != null)
                    await _cache.PutAsync(_runtime.Name, cacheKey, 404, null, ct).ConfigureAwait(false);
                return new HttpFetch(FetchKind.NotFound, StatusCode: 404);
            }

            if (status >= 500 || !response.IsSuccessStatusCode)
            {
                breaker.RecordFailure();
                _runtime.LastError = $"HTTP {status}";
                _logger.LogWarning("[Enrichment] {Provider} returned HTTP {Status}", _runtime.Name, status);
                return new HttpFetch(FetchKind.Failed, StatusCode: status, Error: _runtime.LastError);
            }

            var reclassified = inspect?.Invoke(response, body);
            if (reclassified != null)
            {
                if (reclassified.Kind == FetchKind.RateLimited)
                    return RateLimited(reclassified.RetryAfter, status, reclassified.Error ?? "quota exceeded");
                if (reclassified.Kind == FetchKind.NotFound && cacheKey != null && _cache != null)
                    await _cache.PutAsync(_runtime.Name, cacheKey, 404, null, ct).ConfigureAwait(false);
                if (reclassified.Kind == FetchKind.Failed)
                {
                    _runtime.LastError = reclassified.Error;
                    _logger.LogWarning("[Enrichment] {Provider}: {Error}", _runtime.Name, reclassified.Error);
                }
                if (reclassified.Kind != FetchKind.Ok) return reclassified;
            }

            breaker.RecordSuccess();
            if (reclassified?.PauseAfter is { } pause)
            {
                var resumeAt = breaker.RecordRateLimited(pause);
                _logger.LogInformation("[Enrichment] {Provider} request budget nearly used; pausing until {Until:u}", _runtime.Name, resumeAt);
            }
            if (cacheKey != null && _cache != null)
                await _cache.PutAsync(_runtime.Name, cacheKey, 200, body, ct).ConfigureAwait(false);
            return new HttpFetch(FetchKind.Ok, body, status);
        }
    }

    public Task<HttpFetch> GetAsync(string url, string? cacheKey, CancellationToken ct, Action<HttpRequestMessage>? configure = null,
        Func<HttpResponseMessage, string, HttpFetch?>? inspect = null) =>
        SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            configure?.Invoke(req);
            return req;
        }, cacheKey, ct, inspect);

    private HttpFetch RateLimited(TimeSpan? retryAfter, int status, string error)
    {
        var until = _runtime.Breaker.RecordRateLimited(retryAfter);
        _runtime.LastError = $"rate limited until {until.ToLocalTime():HH:mm:ss}";
        _logger.LogWarning("[Enrichment] {Provider} rate limited ({Error}); pausing until {Until:u}", _runtime.Name, error, until);
        return new HttpFetch(FetchKind.RateLimited, StatusCode: status, RetryAfter: until - DateTime.UtcNow, Error: error);
    }

    internal static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra == null) return null;
        if (ra.Delta is { } delta) return delta;
        if (ra.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }
}
