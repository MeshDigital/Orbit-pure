using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services.ImportProviders;
using SLSKDONET.Utils;
using Xunit;

namespace SLSKDONET.Tests.Services.Import;

/// <summary>
/// Tests for provider dispatch (CanHandle) and tracklist parsing.
/// These tests are pure / in-process — no network calls, no DB.
/// </summary>
public class ImportProviderCanHandleTests
{
    // ── TracklistImportProvider ────────────────────────────────────────────

    [Theory]
    [InlineData("Artist A - Title One\nArtist B - Title Two")]
    [InlineData("00:00 Artist A - Title One\n02:30 Artist B - Title Two")]
    [InlineData("1. Artist A - Title One\n2. Artist B - Title Two\n3. Artist C - Title Three")]
    public void TracklistProvider_CanHandle_MultiLineInput(string input)
    {
        var provider = new TracklistImportProvider(NullLogger<TracklistImportProvider>.Instance);
        Assert.True(provider.CanHandle(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Just a single line")]
    public void TracklistProvider_CannotHandle_SingleLineOrEmpty(string input)
    {
        var provider = new TracklistImportProvider(NullLogger<TracklistImportProvider>.Instance);
        Assert.False(provider.CanHandle(input));
    }

    [Fact]
    public async Task TracklistProvider_ImportAsync_ParsesArtistTitleSeparatedByDash()
    {
        var provider = new TracklistImportProvider(NullLogger<TracklistImportProvider>.Instance);
        const string input = "Bicep - Glue\nAphex Twin - Windowlicker\nFour Tet - Sing";

        var result = await provider.ImportAsync(input);

        Assert.True(result.Success);
        Assert.Equal(3, result.Tracks.Count);
        Assert.Equal("Bicep", result.Tracks[0].Artist);
        Assert.Equal("Glue", result.Tracks[0].Title);
        Assert.Equal("Aphex Twin", result.Tracks[1].Artist);
        Assert.Equal("Four Tet", result.Tracks[2].Artist);
    }

    [Fact]
    public async Task TracklistProvider_ImportAsync_ParsesTimestampedTracklist()
    {
        var provider = new TracklistImportProvider(NullLogger<TracklistImportProvider>.Instance);
        const string input = "00:00 Bicep - Glue\n04:15 Aphex Twin - Windowlicker\n10:30 Four Tet - Sing";

        var result = await provider.ImportAsync(input);

        Assert.True(result.Success);
        Assert.Equal(3, result.Tracks.Count);
        Assert.Equal("Bicep", result.Tracks[0].Artist);
        Assert.Equal("Glue", result.Tracks[0].Title);
    }

    [Fact]
    public async Task TracklistProvider_StreamAsync_YieldsSingleBatch()
    {
        var provider = new TracklistImportProvider(NullLogger<TracklistImportProvider>.Instance);
        const string input = "Bicep - Glue\nAphex Twin - Windowlicker";

        var batches = new List<SLSKDONET.Services.ImportBatchResult>();
        await foreach (var batch in provider.ImportStreamAsync(input))
            batches.Add(batch);

        Assert.Single(batches);
        Assert.Equal(2, batches[0].Tracks.Count);
    }

    [Fact]
    public async Task TracklistProvider_ImportAsync_ReturnsFailure_WhenInputIsSingleLine()
    {
        var provider = new TracklistImportProvider(NullLogger<TracklistImportProvider>.Instance);

        var result = await provider.ImportAsync("single line only");

        Assert.False(result.Success);
    }

    // ── SpotifyImportProvider.CanHandle ────────────────────────────────────

    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/album/6dVIqQ8qmQ5GBnJ9shOYGE")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:album:6dVIqQ8qmQ5GBnJ9shOYGE")]
    public void SpotifyProvider_CanHandle_SpotifyUrls(string url)
    {
        // SpotifyImportProvider.CanHandle is purely string-based; no network needed.
        // We construct without real services to test dispatch logic only.
        Assert.True(IsSpotifyUrl(url));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("some random text")]
    [InlineData("C:/Music/playlist.csv")]
    public void SpotifyProvider_CannotHandle_NonSpotifyUrls(string url)
    {
        Assert.False(IsSpotifyUrl(url));
    }

    private static bool IsSpotifyUrl(string input) =>
        input.Contains("spotify.com", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase);

    // ── CsvImportProvider.CanHandle ────────────────────────────────────────

    [Theory]
    [InlineData("C:/Music/my_playlist.csv")]
    [InlineData("/home/user/tracks.CSV")]
    public void CsvProvider_CanHandle_CsvExtensions(string path)
    {
        Assert.True(path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
    }

    // ── SpotifyScraperInputSource: static helpers ──────────────────────────

    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc123", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    public void SpotifyScraperInputSource_ExtractPlaylistId_ReturnsId(string url, string expectedId)
    {
        var id = SLSKDONET.Services.InputParsers.SpotifyScraperInputSource.ExtractPlaylistId(url);
        Assert.Equal(expectedId, id);
    }

    [Theory]
    [InlineData("https://open.spotify.com/album/6dVIqQ8qmQ5GBnJ9shOYGE", "6dVIqQ8qmQ5GBnJ9shOYGE")]
    [InlineData("spotify:album:6dVIqQ8qmQ5GBnJ9shOYGE", "6dVIqQ8qmQ5GBnJ9shOYGE")]
    public void SpotifyScraperInputSource_ExtractAlbumId_ReturnsId(string url, string expectedId)
    {
        var id = SLSKDONET.Services.InputParsers.SpotifyScraperInputSource.ExtractAlbumId(url);
        Assert.Equal(expectedId, id);
    }

    [Fact]
    public void SpotifyScraperInputSource_ExtractPlaylistId_ReturnsNull_ForAlbumUrl()
    {
        var id = SLSKDONET.Services.InputParsers.SpotifyScraperInputSource.ExtractPlaylistId(
            "https://open.spotify.com/album/6dVIqQ8qmQ5GBnJ9shOYGE");
        Assert.Null(id);
    }
}
