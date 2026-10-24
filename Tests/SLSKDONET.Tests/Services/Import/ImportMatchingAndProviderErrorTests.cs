using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Import;
using SLSKDONET.Services.ImportProviders;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Utils;
using Xunit;

namespace SLSKDONET.Tests.Services.Import;

/// <summary>
/// The identity rules shared by the import preview and DownloadManager.QueueProject, and the
/// providers' failure contract (throw ImportSourceException instead of yielding nothing).
/// </summary>
public class ImportMatchingAndProviderErrorTests
{
    // ── LibraryMatchIndex ────────────────────────────────────────────────────

    [Fact]
    public void LibraryMatch_ByHash_SpotifyId_LooseKey_AndFuzzy()
    {
        var byHash = new LibraryEntry { Artist = "Bicep", Title = "Glue", UniqueHash = TrackHashUtil.Compute("Bicep", "Glue") };
        var bySpotify = new LibraryEntry { Artist = "X", Title = "Y", UniqueHash = "h2", SpotifyTrackId = "sp-1" };
        var byLoose = new LibraryEntry { Artist = "Four-Tet", Title = "Baby!", UniqueHash = "h3" };
        var index = new LibraryMatchIndex(new[] { byHash, bySpotify, byLoose });

        Assert.Same(byHash, index.Resolve("Bicep", "Glue", null, TrackHashUtil.Compute("Bicep", "Glue")));
        Assert.Same(bySpotify, index.Resolve("Other", "Name", "sp-1", "nohash"));
        Assert.Same(byLoose, index.Resolve("Four Tet", "Baby", null, "nohash"));
        Assert.Null(index.Resolve("Completely", "Different", null, "nohash"));
    }

    [Theory]
    [InlineData("Bicep", "Glue (Original Mix)", "Bicep", "Glue (Extended Mix)")]
    [InlineData("Four Tet", "Baby", "Four Tet", "Babys")]
    [InlineData("Burial", "Archangel", "Burial", "Near Dark")]
    public void LibraryMatch_FuzzyBands_FollowTheThresholds(string libArtist, string libTitle, string artist, string title)
    {
        var entry = new LibraryEntry { Artist = libArtist, Title = libTitle, UniqueHash = "h" };
        var index = new LibraryMatchIndex(new[] { entry });
        var expected = StringDistanceUtils.GetNormalizedMatchScore(
            StringDistanceUtils.Normalize($"{artist} {title}"),
            StringDistanceUtils.Normalize($"{libArtist} {libTitle}"));

        var (owned, similar, _) = index.Match(artist, title, null, "x");

        Assert.Equal(expected >= LibraryMatchIndex.FuzzyOwnedThreshold, owned != null);
        Assert.Equal(expected >= LibraryMatchIndex.FuzzySimilarThreshold && expected < LibraryMatchIndex.FuzzyOwnedThreshold, similar != null);
    }

    // ── PlaylistMembershipIndex ──────────────────────────────────────────────

    [Fact]
    public void Membership_FindsRowStoredUnderTheLibraryFileHash()
    {
        // QueueProject stores linked rows under the library file's hash; re-imports must find them.
        var row = new PlaylistTrack { Id = Guid.NewGuid(), Artist = "Bicep feat. Clara", Title = "Glue (Original Mix)", TrackUniqueHash = "library-hash" };
        var index = new PlaylistMembershipIndex(new[] { row });

        Assert.Null(index.Find("bicep-glue", null, null, "Bicep", "Glue"));
        Assert.Same(row, index.Find("bicep-glue", "library-hash", null, "Bicep", "Glue"));
    }

    [Fact]
    public void Membership_FindsBySpotifyIdAndLooseKey_AndFlagsRetries()
    {
        var failed = new PlaylistTrack { Id = Guid.NewGuid(), Artist = "A", Title = "One", TrackUniqueHash = "x", SpotifyTrackId = "sp", Status = TrackStatus.Failed };
        var done = new PlaylistTrack { Id = Guid.NewGuid(), Artist = "B-B", Title = "Two!", TrackUniqueHash = "y", Status = TrackStatus.Downloaded };
        var index = new PlaylistMembershipIndex(new[] { failed, done });

        Assert.Same(failed, index.Find("nope", null, "sp", "Z", "Z"));
        Assert.Same(done, index.Find("nope", null, null, "BB", "Two"));
        Assert.True(PlaylistMembershipIndex.NeedsRetry(failed));
        Assert.False(PlaylistMembershipIndex.NeedsRetry(done));
    }

    [Fact]
    public void QueueResult_CountsDownloadsAsNewMinusLinkedPlusRetries()
    {
        Assert.Equal(7, new ProjectQueueResult(Added: 10, LinkedFromLibrary: 4, Retried: 1, 0, 0, 0).QueuedForDownload);
    }

    [Fact]
    public void Mapper_KeepsSpotifyPlaylistIdAndUri()
    {
        var track = ImportTrackMapper.ToTrack(new SearchQuery
        {
            Artist = "A", Title = "B", SpotifyTrackId = "t", SpotifyPlaylistId = "p", SpotifyUri = "spotify:track:t", CanonicalDuration = 200_000
        });

        Assert.Equal("p", track.SpotifyPlaylistId);
        Assert.Equal("spotify:track:t", track.SpotifyUri);
        Assert.Equal(200_000, track.CanonicalDuration);
    }

    // ── Provider failure contract ────────────────────────────────────────────

    [Fact]
    public async Task TracklistProvider_NothingParseable_ThrowsInsteadOfYieldingNothing()
    {
        var provider = new TracklistImportProvider(NullLogger<TracklistImportProvider>.Instance);

        var ex = await Assert.ThrowsAsync<ImportSourceException>(async () =>
        {
            await foreach (var _ in provider.ImportStreamAsync("\n\n   \n")) { }
        });

        Assert.Contains("No tracks found", ex.Message);
        Assert.False(ex.RequiresSignIn);
    }

    [Fact]
    public async Task CsvProvider_MissingFile_Throws()
    {
        var provider = new CsvImportProvider(
            NullLogger<CsvImportProvider>.Instance,
            new CsvInputSource(),
            new Mock<ISpotifyMetadataService>().Object);
        var path = Path.Combine(Path.GetTempPath(), $"orbit-missing-{Guid.NewGuid():N}.csv");

        var ex = await Assert.ThrowsAsync<ImportSourceException>(async () =>
        {
            await foreach (var _ in provider.ImportStreamAsync(path)) { }
        });

        Assert.Contains("File not found", ex.Message);
    }

    [Fact]
    public void SpotifyErrors_AreTranslatedToActionableMessages()
    {
        var unauthorized = SpotifyImportErrors.Translate(new InvalidOperationException("Spotify API is not configured or authenticated."));
        Assert.True(unauthorized.RequiresSignIn);

        var network = SpotifyImportErrors.Translate(new HttpRequestException("boom"));
        Assert.Contains("Couldn't reach Spotify", network.Message);
        Assert.False(network.RequiresSignIn);

        var passthrough = new ImportSourceException("already friendly", requiresSignIn: true);
        Assert.Same(passthrough, SpotifyImportErrors.Translate(passthrough));
    }
}
