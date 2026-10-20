using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.Services.Enrichment.Providers;
using Xunit;

namespace SLSKDONET.Tests.Services.Enrichment;

public class RateLimitAndBreakerTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MusicBrainzPolicy_NeverTwoRequestsWithinOneSecond()
    {
        var limiter = new ProviderRateLimiter(MusicBrainzProvider.Policy, () => T0, new Random(1));
        var slots = Enumerable.Range(0, 20).Select(_ => limiter.Reserve()).ToList();

        for (int i = 1; i < slots.Count; i++)
            Assert.True((slots[i] - slots[i - 1]).TotalMilliseconds >= 1000, $"slot {i} only {(slots[i] - slots[i - 1]).TotalMilliseconds} ms after the previous");
        // 20 requests take at least 19 s, i.e. ≤ 1/s.
        Assert.True((slots[^1] - slots[0]).TotalSeconds >= 19);
    }

    [Fact]
    public void DeezerPolicy_StaysUnderTheDocumentedQuota()
    {
        var limiter = new ProviderRateLimiter(DeezerProvider.Policy, () => T0, new Random(2));
        var slots = Enumerable.Range(0, 400).Select(_ => limiter.Reserve()).ToList();

        // Deezer documents 50 requests / 5 s. Check every sliding 5 s window, and ≤ 10/s.
        for (int i = 0; i < slots.Count; i++)
        {
            var inWindow = slots.Count(s => s >= slots[i] && s < slots[i] + TimeSpan.FromSeconds(5));
            Assert.True(inWindow <= 40, $"{inWindow} requests in the 5 s window starting at slot {i}");
            var inSecond = slots.Count(s => s >= slots[i] && s < slots[i] + TimeSpan.FromSeconds(1));
            Assert.True(inSecond <= 10, $"{inSecond} requests in one second");
        }
        var perSecond = slots.Count / (slots[^1] - slots[0]).TotalSeconds;
        Assert.InRange(perSecond, 5.0, 8.0); // ≤ 8 req/s target, not starved either
    }

    [Fact]
    public async Task Limiter_WaitAsync_RealTime_PacesCallers()
    {
        var policy = new RatePolicy(1, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200), TimeSpan.Zero);
        var limiter = new ProviderRateLimiter(policy);
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => limiter.WaitAsync(CancellationToken.None)));
        Assert.True(sw.ElapsedMilliseconds >= 550, $"4 calls at 200 ms spacing finished in {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Http429_WithRetryAfter_OpensTheBreaker_AndStopsRequests()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return r;
        });
        var runtime = new ProviderRuntime("deezer", RatePolicy.Unlimited);
        var http = new ProviderHttp(new HttpClient(handler), runtime, cache: null, NullLogger.Instance);

        var first = await http.GetAsync("https://api.deezer.com/track/1", null, CancellationToken.None);
        Assert.Equal(FetchKind.RateLimited, first.Kind);
        Assert.InRange(first.RetryAfter!.Value.TotalSeconds, 100, 121);
        Assert.True(runtime.Breaker.IsOpen);
        Assert.Equal(1, runtime.Breaker.RateLimitPauses);

        var second = await http.GetAsync("https://api.deezer.com/track/2", null, CancellationToken.None);
        Assert.Equal(FetchKind.CircuitOpen, second.Kind);
        Assert.Single(handler.Requests); // the paused provider was not asked again
        Assert.Equal(LookupStatus.RateLimited, second.ToLookupFailure().Status);
    }

    [Fact]
    public async Task Repeated403_DisablesTheProviderForTheSession()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var deezer = new DeezerProvider(new HttpClient(handler), new NoCache(), NullLogger<DeezerProvider>.Instance,
            new ProviderRuntime(DeezerProvider.ProviderName, RatePolicy.Unlimited));
        var track = new EnrichmentTrack { Hash = "h", Artist = "Calibre", Title = "Even If", Isrc = "GBCPZ0700001" };

        for (int i = 0; i < ProviderCircuitBreaker.ForbiddenLimit; i++)
            Assert.Equal(LookupStatus.Unavailable, (await deezer.LookupAsync(track, new KnownIds(), CancellationToken.None)).Status);

        Assert.True(deezer.Runtime.Breaker.IsDisabledForSession);
        Assert.Equal(ProviderAvailability.Blocked, deezer.GetAvailability());
        var requestsBefore = handler.Requests.Count;
        Assert.Equal(LookupStatus.Unavailable, (await deezer.LookupAsync(track, new KnownIds(), CancellationToken.None)).Status);
        Assert.Equal(requestsBefore, handler.Requests.Count); // no more requests once disabled
    }

    [Fact]
    public async Task DeezerQuotaError_In200Body_IsTreatedAsRateLimit()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{\"error\":{\"type\":\"Exception\",\"message\":\"Quota limit exceeded\",\"code\":4}}"));
        var deezer = new DeezerProvider(new HttpClient(handler), new NoCache(), NullLogger<DeezerProvider>.Instance,
            new ProviderRuntime(DeezerProvider.ProviderName, RatePolicy.Unlimited));
        var result = await deezer.LookupAsync(new EnrichmentTrack { Hash = "h", Artist = "A", Title = "B" }, new KnownIds(), CancellationToken.None);
        Assert.Equal(LookupStatus.RateLimited, result.Status);
        Assert.True(deezer.Runtime.Breaker.IsOpen);
    }

    [Fact]
    public void Breaker_WithoutRetryAfter_BacksOffExponentially()
    {
        var now = T0;
        var breaker = new ProviderCircuitBreaker(() => now);
        var first = breaker.RecordRateLimited(null) - now;
        var second = breaker.RecordRateLimited(null) - now;
        Assert.Equal(ProviderCircuitBreaker.DefaultRateLimitPause, first);
        Assert.Equal(ProviderCircuitBreaker.DefaultRateLimitPause * 2, second);
        now = now.AddHours(1);
        Assert.False(breaker.IsOpen);
    }

    internal sealed class NoCache : IMetadataLookupCache
    {
        public Task<CachedLookup?> GetAsync(string provider, string key, CancellationToken ct = default) => Task.FromResult<CachedLookup?>(null);
        public Task PutAsync(string provider, string key, int statusCode, string? payload, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class MemoryCache : IMetadataLookupCache
    {
        public Dictionary<string, CachedLookup> Items { get; } = new();
        public Task<CachedLookup?> GetAsync(string provider, string key, CancellationToken ct = default) =>
            Task.FromResult(Items.TryGetValue(provider + "|" + key, out var v) ? v : null);
        public Task PutAsync(string provider, string key, int statusCode, string? payload, CancellationToken ct = default)
        {
            Items[provider + "|" + key] = new CachedLookup(statusCode, payload, DateTime.UtcNow);
            return Task.CompletedTask;
        }
    }
}
