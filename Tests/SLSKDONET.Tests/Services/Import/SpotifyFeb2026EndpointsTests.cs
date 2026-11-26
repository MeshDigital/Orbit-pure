using System.Collections.Generic;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SpotifyAPI.Web;
using Xunit;

namespace SLSKDONET.Tests.Services.Import;

/// <summary>
/// Regression tests for Epic #508 / #515 (SP4: Spotify Feb 2026 Web API endpoint migration).
/// </summary>
public sealed class SpotifyFeb2026EndpointsTests
{
    [Fact]
    public void PlaylistItemsPageSize_RespectsFeb2026MaxOf50()
    {
        Assert.Equal(50, SpotifyInputSource.PlaylistItemsPageSize);
    }

    [Theory]
    [InlineData(20, 10)]
    [InlineData(10, 10)]
    [InlineData(5, 5)]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    public void ClampSearchLimit_ClampsToFeb2026Range1To10(int requested, int expected)
    {
        Assert.Equal(expected, SpotifyInputSource.ClampSearchLimit(requested));
    }

    [Fact]
    public void ExtractFullTrack_ReadsNewItemPropertyFromFeb2026Payload()
    {
        var track = new FullTrack { Id = "trk123", Name = "Horizon" };
        var playlistItem = new PlaylistTrack<IPlayableItem> { Item = track };

        var extracted = SpotifyInputSource.ExtractFullTrack(playlistItem);

        Assert.Same(track, extracted);
    }

    [Fact]
    public void BuildPlaylistUrl_BuildsCanonicalUrlFromId_EvenWhenExternalUrlsMissing()
    {
        var url = SpotifyInputSource.BuildPlaylistUrl("3cEYpjA9oz9GiPac4AsH4n", new Dictionary<string, string>());
        Assert.Equal("https://open.spotify.com/playlist/3cEYpjA9oz9GiPac4AsH4n", url);
    }

    [Fact]
    public void PlaylistViewModel_ExposesOwnedVsFollowedAccessBadge()
    {
        var owned = new SpotifyPlaylistViewModel { IsOwnedOrCollaborative = true };
        var followed = new SpotifyPlaylistViewModel { IsOwnedOrCollaborative = false };

        Assert.Equal("Yours", owned.AccessBadge);
        Assert.Equal("Followed", followed.AccessBadge);
    }
}
