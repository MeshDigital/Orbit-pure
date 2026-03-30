using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class PlaylistMosaicServiceTests
{
    /// <summary>
    /// Creates a <see cref="SLSKDONET.Services.PlaylistMosaicService"/> whose HTTP calls
    /// are all intercepted by the supplied <paramref name="handler"/>.
    /// </summary>
    private static SLSKDONET.Services.PlaylistMosaicService CreateSut(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new SLSKDONET.Services.PlaylistMosaicService(
            NullLogger<SLSKDONET.Services.PlaylistMosaicService>.Instance,
            http);
    }

    [Fact]
    public async Task GenerateMosaicAsync_EmptyUrls_ReturnsNull()
    {
        var sut = CreateSut(new AlwaysFailHandler());

        var result = await sut.GenerateMosaicAsync(Enumerable.Empty<string?>());

        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateMosaicAsync_AllNullOrWhitespaceUrls_ReturnsNull()
    {
        var sut = CreateSut(new AlwaysFailHandler());

        var result = await sut.GenerateMosaicAsync(new string?[] { null, "  ", "" });

        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateMosaicAsync_AllDownloadsFail_ReturnsNull()
    {
        var sut = CreateSut(new AlwaysFailHandler());

        // Even with valid-looking URLs, if the HTTP client can't download them the
        // mosaic should gracefully return null rather than throw.
        var result = await sut.GenerateMosaicAsync(new[]
        {
            "http://invalid.local/art1.jpg",
            "http://invalid.local/art2.jpg"
        });

        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateMosaicAsync_RepeatedCallWithSameUrls_DoesNotThrow()
    {
        // When the HTTP response contains invalid image data, the service should
        // gracefully return null both times rather than throw.  This also exercises
        // the deduplication path (same key computed twice).
        var sut = CreateSut(new StaticResponseHandler(MinimalPng()));

        var urls = new[] { "http://example.local/art.jpg" };

        // Neither call should throw; both may return null if SkiaSharp cannot
        // decode the minimal test PNG in a headless environment.
        var first  = await Record.ExceptionAsync(() => sut.GenerateMosaicAsync(urls));
        var second = await Record.ExceptionAsync(() => sut.GenerateMosaicAsync(urls));

        Assert.Null(first);   // no exception on first call
        Assert.Null(second);  // no exception on second call
    }

    [Fact]
    public async Task GenerateMosaicAsync_OnlyFourDistinctUrlsUsed()
    {
        // Five distinct URLs supplied — only the first four should be requested.
        var requestedUrls = new List<string>();
        var handler = new RecordingHandler(requestedUrls, MinimalPng());
        var sut = CreateSut(handler);

        var urls = new[]
        {
            "http://example.local/art1.jpg",
            "http://example.local/art2.jpg",
            "http://example.local/art3.jpg",
            "http://example.local/art4.jpg",
            "http://example.local/art5.jpg",  // must be ignored
        };

        await sut.GenerateMosaicAsync(urls);

        // At most 4 distinct URLs should have been fetched
        Assert.True(requestedUrls.Distinct().Count() <= 4);
        Assert.DoesNotContain("http://example.local/art5.jpg", requestedUrls);
    }

    [Fact]
    public async Task GenerateMosaicAsync_DuplicateUrlsDeduped()
    {
        var requestedUrls = new List<string>();
        var handler = new RecordingHandler(requestedUrls, MinimalPng());
        var sut = CreateSut(handler);

        // Same URL repeated — should be downloaded only once
        var urls = new[]
        {
            "http://example.local/art.jpg",
            "http://example.local/art.jpg",
            "http://example.local/art.jpg",
        };

        await sut.GenerateMosaicAsync(urls);

        Assert.Single(requestedUrls);
    }

    // ── Minimal test PNG helpers ─────────────────────────────────────────────

    /// <summary>Returns a valid 1×1 transparent PNG as a byte array.</summary>
    private static byte[] MinimalPng() => new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR chunk length + type
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1×1
        0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53, // 8-bit RGB, CRC
        0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, // IDAT chunk
        0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
        0x00, 0x00, 0x02, 0x00, 0x01, 0xE2, 0x21, 0xBC,
        0x33, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, // IEND chunk
        0x44, 0xAE, 0x42, 0x60, 0x82
    };

    // ── Helpers: custom HttpMessageHandlers ───────────────────────────────────

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Simulated network failure");
        }
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        public StaticResponseHandler(byte[] body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<string> _log;
        private readonly byte[] _body;

        public RecordingHandler(List<string> log, byte[] body)
        {
            _log = log;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            _log.Add(request.RequestUri?.ToString() ?? "");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body)
            };
            return Task.FromResult(response);
        }
    }
}
