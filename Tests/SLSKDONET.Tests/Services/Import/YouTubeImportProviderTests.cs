using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.ImportProviders;
using Xunit;

namespace SLSKDONET.Tests.Services.Import;

/// <summary>#424: YouTubeImportProvider URL detection, title normalization, Data API pagination, and public HTML extraction.</summary>
public class YouTubeImportProviderTests
{
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(new StubHandler(_handler));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PL123456", true)]
    [InlineData("https://music.youtube.com/playlist?list=PLabcdef", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLxyz", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?list=PLxyz", true)]
    [InlineData("youtube:liked", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false)]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", false)]
    [InlineData("not a url", false)]
    public void CanHandle_RecognizesYouTubePlaylistsAndLiked(string input, bool expected)
    {
        var provider = new YouTubeImportProvider(
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new AppConfig(),
            NullLogger<YouTubeImportProvider>.Instance);

        Assert.Equal(expected, provider.CanHandle(input));
    }

    [Theory]
    [InlineData("Bicep - Glue (Official Video)", "Bicep", "Bicep", "Glue")]
    [InlineData("Daft Punk – Aerodynamic [Official Audio]", "Daft Punk", "Daft Punk", "Aerodynamic")]
    [InlineData("Delilah (pull me out of this) [Visualizer]", "Fred again.. - Topic", "Fred again..", "Delilah (pull me out of this)")]
    [InlineData("Midnight City (Official Music Video)", "M83VEVO", "M83", "Midnight City")]
    public void NormalizeVideoTitle_StripsNoiseAndExtractsArtistAndTitle(
        string rawTitle, string channel, string expectedArtist, string expectedTitle)
    {
        var (artist, title) = YouTubeImportProvider.NormalizeVideoTitle(rawTitle, channel);
        Assert.Equal(expectedArtist, artist);
        Assert.Equal(expectedTitle, title);
    }

    [Fact]
    public async Task ImportStreamAsync_DataApi_PaginatesAndSkipsDeletedVideos()
    {
        var factory = new StubHttpClientFactory(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/playlists?"))
            {
                return JsonResponse("""{"items":[{"snippet":{"title":"Late Night House"}}]}""");
            }
            if (url.Contains("pageToken=PAGE2"))
            {
                return JsonResponse("""
                {
                  "pageInfo": { "totalResults": 3 },
                  "items": [
                    { "snippet": { "title": "Deleted video", "videoOwnerChannelTitle": "" } },
                    { "snippet": { "title": "Peggy Gou - (It Goes Like) Nanana (Official Audio)", "videoOwnerChannelTitle": "Peggy Gou" } }
                  ]
                }
                """);
            }
            return JsonResponse("""
            {
              "nextPageToken": "PAGE2",
              "pageInfo": { "totalResults": 3 },
              "items": [
                { "snippet": { "title": "Bicep - Apricots (Official Video)", "videoOwnerChannelTitle": "Bicep" } }
              ]
            }
            """);
        });

        var provider = new YouTubeImportProvider(
            factory,
            new AppConfig { YouTubeApiKey = "test-key" },
            NullLogger<YouTubeImportProvider>.Instance);

        var batches = new List<ImportBatchResult>();
        await foreach (var b in provider.ImportStreamAsync("https://www.youtube.com/playlist?list=PLhouse"))
            batches.Add(b);

        Assert.Equal(2, batches.Count);
        Assert.Equal("Late Night House", batches[0].SourceTitle);
        Assert.Equal("Bicep", batches[0].Tracks[0].Artist);
        Assert.Equal("Apricots", batches[0].Tracks[0].Title);
        Assert.Equal("Peggy Gou", batches[1].Tracks[0].Artist);
        Assert.Equal("(It Goes Like) Nanana", batches[1].Tracks[0].Title);
    }

    [Fact]
    public async Task ImportStreamAsync_PublicHtml_ParsesYtInitialData()
    {
        const string html = """
        <html><head><script>var ytInitialData = {
          "metadata": { "playlistMetadataRenderer": { "title": "Club Weapons" } },
          "contents": {
            "items": [
              { "playlistVideoRenderer": {
                  "title": { "runs": [{ "text": "Overmono - Good Lies (Official Audio)" }] },
                  "shortBylineText": { "runs": [{ "text": "Overmono" }] },
                  "lengthSeconds": "245"
              }},
              { "playlistVideoRenderer": {
                  "title": { "simpleText": "[Private video]" },
                  "shortBylineText": { "simpleText": "" }
              }}
            ]
          }
        };</script></head></html>
        """;

        var factory = new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        });

        var provider = new YouTubeImportProvider(factory, new AppConfig(), NullLogger<YouTubeImportProvider>.Instance);

        var batches = new List<ImportBatchResult>();
        await foreach (var b in provider.ImportStreamAsync("https://music.youtube.com/playlist?list=PLclub"))
            batches.Add(b);

        var batch = Assert.Single(batches);
        Assert.Equal("Club Weapons", batch.SourceTitle);
        var track = Assert.Single(batch.Tracks);
        Assert.Equal("Overmono", track.Artist);
        Assert.Equal("Good Lies", track.Title);
        Assert.Equal(245, track.Length);
    }

    [Fact]
    public async Task ImportStreamAsync_LikedVideosWithoutOAuth_ThrowsRequiresSignIn()
    {
        var provider = new YouTubeImportProvider(
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            new AppConfig(),
            NullLogger<YouTubeImportProvider>.Instance);

        var ex = await Assert.ThrowsAsync<ImportSourceException>(async () =>
        {
            await foreach (var _ in provider.ImportStreamAsync("youtube:liked")) { }
        });

        Assert.True(ex.RequiresSignIn);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
