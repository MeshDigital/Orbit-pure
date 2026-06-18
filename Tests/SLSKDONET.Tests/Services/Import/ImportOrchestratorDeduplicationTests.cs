using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace SLSKDONET.Tests.Services.Import;

/// <summary>
/// Tests for ImportOrchestrator's URL normalization and Spotify playlist ID extraction.
/// These use reflection to access private helpers so we test the real logic without
/// needing a fully-wired DI container.
/// </summary>
public class ImportOrchestratorDeduplicationTests
{
    private static readonly Type _orchestratorType =
        typeof(SLSKDONET.Services.ImportOrchestrator);

    private static string? InvokeNormalize(string input)
    {
        var method = _orchestratorType.GetMethod(
            "NormalizeImportInput",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string?)method!.Invoke(null, new object[] { input });
    }

    private static string? InvokeExtractSpotifyId(string input)
    {
        var method = _orchestratorType.GetMethod(
            "ExtractSpotifyPlaylistId",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string?)method!.Invoke(null, new object[] { input });
    }

    // ── NormalizeImportInput ───────────────────────────────────────────────

    [Fact]
    public void NormalizeImportInput_SpotifyUri_ToLowercase()
    {
        var result = InvokeNormalize("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M");
        Assert.Equal("spotify:playlist:37i9dqzf1dxcbwigoybm5m", result, StringComparer.Ordinal);
    }

    [Fact]
    public void NormalizeImportInput_SpotifyUrl_StripsQueryParameters()
    {
        var result = InvokeNormalize("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc123");
        Assert.NotNull(result);
        Assert.DoesNotContain("si=", result, StringComparison.Ordinal);
        Assert.Contains("37i9dqzf1dxcbwigoybm5m", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeImportInput_SameUrlVariants_ProduceIdenticalResult()
    {
        // With and without trailing ?si= should normalize to the same string
        var a = InvokeNormalize("https://open.spotify.com/playlist/ABC123?si=xyz");
        var b = InvokeNormalize("https://open.spotify.com/playlist/ABC123");

        Assert.Equal(a, b, StringComparer.Ordinal);
    }

    [Fact]
    public void NormalizeImportInput_ArbitraryText_ToLowercase()
    {
        var result = InvokeNormalize("Some Pasted Tracklist");
        Assert.Equal("some pasted tracklist", result, StringComparer.Ordinal);
    }

    // ── ExtractSpotifyPlaylistId ───────────────────────────────────────────

    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc", "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "37i9dQZF1DXcBWIGoYBM5M")]
    public void ExtractSpotifyPlaylistId_ValidInputs_ReturnsId(string input, string expected)
    {
        var id = InvokeExtractSpotifyId(input);
        Assert.Equal(expected, id, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("https://open.spotify.com/album/6dVIqQ8qmQ5GBnJ9shOYGE")]
    [InlineData("some random text")]
    [InlineData("")]
    public void ExtractSpotifyPlaylistId_NonPlaylistInput_ReturnsNullOrEmpty(string input)
    {
        var id = InvokeExtractSpotifyId(input);
        Assert.True(string.IsNullOrEmpty(id));
    }

    [Fact]
    public void ExtractSpotifyPlaylistId_UriAndUrlVariants_ReturnSameId()
    {
        // The deduplication key must be identical regardless of whether the user
        // pasted a browser URL or a spotify: URI for the same playlist.
        var fromUrl = InvokeExtractSpotifyId("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M");
        var fromUri = InvokeExtractSpotifyId("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M");

        Assert.Equal(fromUrl, fromUri, StringComparer.Ordinal);
    }

    // ── GuidGenerator (deterministic job IDs) ─────────────────────────────

    [Fact]
    public void GuidGenerator_CreateFromUrl_SameInputProducesSameGuid()
    {
        var a = SLSKDONET.Utils.GuidGenerator.CreateFromUrl("https://open.spotify.com/playlist/ABC");
        var b = SLSKDONET.Utils.GuidGenerator.CreateFromUrl("https://open.spotify.com/playlist/ABC");
        Assert.Equal(a, b);
    }

    [Fact]
    public void GuidGenerator_CreateFromUrl_DifferentInputsProduceDifferentGuids()
    {
        var a = SLSKDONET.Utils.GuidGenerator.CreateFromUrl("https://open.spotify.com/playlist/ABC");
        var b = SLSKDONET.Utils.GuidGenerator.CreateFromUrl("https://open.spotify.com/playlist/XYZ");
        Assert.NotEqual(a, b);
    }
}
