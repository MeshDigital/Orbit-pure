using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services;
using SLSKDONET.Services.ImportProviders;
using Xunit;

namespace SLSKDONET.Tests.Services.Import;

/// <summary>#425: PlaylistFileImportProvider (.m3u/.m3u8/.txt/.json) and SoundCloudImportProvider.</summary>
public class ImportSourcesProvidersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orbit-import-src-{Guid.NewGuid():N}");

    public ImportSourcesProvidersTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

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

    [Fact]
    public async Task PlaylistFile_ParsesM3u8WithExtInfAndFilenameFallback()
    {
        var path = Path.Combine(_dir, "Warmup Set.m3u8");
        await File.WriteAllTextAsync(path, """
        #EXTM3U
        #EXTINF:312,Bicep - Glue
        /music/Bicep - Glue.flac
        /music/Overmono - Good Lies.wav
        """);

        var provider = new PlaylistFileImportProvider(NullLogger<PlaylistFileImportProvider>.Instance);
        Assert.True(provider.CanHandle(path));

        var result = await provider.ImportAsync(path);
        Assert.True(result.Success);
        Assert.Equal("Warmup Set", result.SourceTitle);
        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal("Bicep", result.Tracks[0].Artist);
        Assert.Equal("Glue", result.Tracks[0].Title);
        Assert.Equal(312, result.Tracks[0].Length);
        Assert.Equal("Overmono", result.Tracks[1].Artist);
        Assert.Equal("Good Lies", result.Tracks[1].Title);
    }

    [Fact]
    public async Task PlaylistFile_ParsesTxtAndJsonExports()
    {
        var txtPath = Path.Combine(_dir, "Club.txt");
        await File.WriteAllTextAsync(txtPath, "Daft Punk - Aerodynamic\nFred again.. - Delilah\n");

        var jsonPath = Path.Combine(_dir, "TuneMyMusic.json");
        await File.WriteAllTextAsync(jsonPath, """
        {
          "tracks": [
            { "artist": "Peggy Gou", "title": "Nanana", "album": "I Hear You", "duration": 224 }
          ]
        }
        """);

        var provider = new PlaylistFileImportProvider(NullLogger<PlaylistFileImportProvider>.Instance);

        var txt = await provider.ImportAsync(txtPath);
        Assert.True(txt.Success);
        Assert.Equal(2, txt.Tracks.Count);

        var json = await provider.ImportAsync(jsonPath);
        Assert.True(json.Success);
        var t = Assert.Single(json.Tracks);
        Assert.Equal("Peggy Gou", t.Artist);
        Assert.Equal("Nanana", t.Title);
        Assert.Equal("I Hear You", t.Album);
        Assert.Equal(224, t.Length);
    }

    [Fact]
    public async Task SoundCloud_ParsesHydrationPlaylist()
    {
        const string html = """
        <html><script>window.__sc_hydration = [
          { "hydratable": "playlist", "data": {
              "title": "Rooftop Breaks",
              "tracks": [
                { "title": "Mall Grab - Liverpool Street in the Rain", "duration": 295000, "user": { "username": "Mall Grab" } },
                { "title": "Sunburst", "duration": 240000, "user": { "username": "Four Tet" } }
              ]
          }}
        ];</script></html>
        """;

        var provider = new SoundCloudImportProvider(
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            }),
            NullLogger<SoundCloudImportProvider>.Instance);

        Assert.True(provider.CanHandle("https://soundcloud.com/dj/sets/rooftop-breaks"));
        Assert.False(provider.CanHandle("https://soundcloud.com/dj/single-track"));

        var result = await provider.ImportAsync("https://soundcloud.com/dj/sets/rooftop-breaks");
        Assert.True(result.Success);
        Assert.Equal("Rooftop Breaks", result.SourceTitle);
        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal("Mall Grab", result.Tracks[0].Artist);
        Assert.Equal(295, result.Tracks[0].Length);
        Assert.Equal("Four Tet", result.Tracks[1].Artist);
        Assert.Equal("Sunburst", result.Tracks[1].Title);
    }
}
