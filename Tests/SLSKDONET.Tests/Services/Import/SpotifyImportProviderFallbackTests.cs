using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.ImportProviders;
using SLSKDONET.Services.InputParsers;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using Xunit;

namespace SLSKDONET.Tests.Services.Import;

/// <summary>
/// A beta tester signed in with an account the built-in (development-mode) Client ID doesn't
/// serve: every API call is 403 "not registered". Public playlists must still import via the
/// public web page instead of failing.
/// </summary>
public class SpotifyImportProviderFallbackTests
{
    private const string PlaylistUrl = "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M";

    private static SpotifyImportProvider CreateProvider()
    {
        var config = new AppConfig();
        var auth = new SpotifyAuthService(NullLogger<SpotifyAuthService>.Instance, config, Mock.Of<ISecureTokenStorage>());
        return new SpotifyImportProvider(
            NullLogger<SpotifyImportProvider>.Instance,
            new SpotifyInputSource(NullLogger<SpotifyInputSource>.Instance, config, auth),
            new SpotifyScraperInputSource(NullLogger<SpotifyScraperInputSource>.Instance),
            config)
        {
            IsApiConfigured = () => true,
        };
    }

    private static APIException Api(HttpStatusCode status, string message)
    {
        var response = new Mock<IResponse>();
        response.SetupGet(r => r.StatusCode).Returns(status);
        response.SetupGet(r => r.Body).Returns($"{{\"error\":{{\"status\":{(int)status},\"message\":\"{message}\"}}}}");
        response.SetupGet(r => r.ContentType).Returns("application/json");
        return new APIException(response.Object);
    }

    private static List<SearchQuery> Tracks(params string[] titles) =>
        titles.Select(t => new SearchQuery { Artist = "Artist", Title = t, SourceTitle = "Public Mix" }).ToList();

    private static async IAsyncEnumerable<List<SearchQuery>> Throwing(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162 // makes this an async iterator
        yield break;
#pragma warning restore CS0162
    }

    private static async Task<List<ImportBatchResult>> Drain(IAsyncEnumerable<ImportBatchResult> stream)
    {
        var batches = new List<ImportBatchResult>();
        await foreach (var b in stream) batches.Add(b);
        return batches;
    }

    [Fact]
    public async Task NotAllowlisted_FallsBackToPublicPage()
    {
        var provider = CreateProvider();
        provider.ApiParseStream = _ => Throwing(Api(HttpStatusCode.Forbidden, "Check settings on developer.spotify.com/dashboard, the user may not be registered."));
        provider.ScrapeAsync = _ => Task.FromResult(Tracks("One", "Two"));

        var batch = Assert.Single(await Drain(provider.ImportStreamAsync(PlaylistUrl)));

        Assert.Equal(2, batch.Tracks.Count);
        Assert.Equal("Public Mix", batch.SourceTitle);
    }

    [Fact]
    public async Task NotAllowlisted_AndPrivatePlaylist_ShowsTheAllowlistReason()
    {
        var provider = CreateProvider();
        provider.ApiParseStream = _ => Throwing(Api(HttpStatusCode.Forbidden, "the user may not be registered."));
        provider.ScrapeAsync = _ => throw new InvalidOperationException("Unable to extract tracks.");

        var ex = await Assert.ThrowsAsync<ImportSourceException>(() => Drain(provider.ImportStreamAsync(PlaylistUrl)));

        Assert.Contains("allowlist", ex.Message);
        Assert.False(ex.RequiresSignIn);
    }

    [Fact]
    public async Task LikedSongs_NeverFallBack()
    {
        var provider = CreateProvider();
        var scraped = false;
        provider.ApiParseStream = _ => Throwing(Api(HttpStatusCode.Forbidden, "the user may not be registered."));
        provider.ScrapeAsync = _ => { scraped = true; return Task.FromResult(Tracks("x")); };

        await Assert.ThrowsAnyAsync<Exception>(() => Drain(provider.ImportStreamAsync("liked")));
        Assert.False(scraped);
    }

    [Fact]
    public async Task ServerError_DoesNotFallBack()
    {
        var provider = CreateProvider();
        var scraped = false;
        provider.ApiParseStream = _ => Throwing(Api(HttpStatusCode.InternalServerError, "server error"));
        provider.ScrapeAsync = _ => { scraped = true; return Task.FromResult(Tracks("x")); };

        await Assert.ThrowsAnyAsync<Exception>(() => Drain(provider.ImportStreamAsync(PlaylistUrl)));
        Assert.False(scraped);
    }

    [Fact]
    public async Task NonStreaming_NotAllowlisted_FallsBackToPublicPage()
    {
        var provider = CreateProvider();
        provider.ApiParseAsync = _ => Task.FromException<List<SearchQuery>>(Api(HttpStatusCode.Forbidden, "the user may not be registered."));
        provider.ScrapeAsync = _ => Task.FromResult(Tracks("One"));

        var result = await provider.ImportAsync(PlaylistUrl);

        Assert.True(result.Success);
        Assert.Single(result.Tracks);
    }
}
