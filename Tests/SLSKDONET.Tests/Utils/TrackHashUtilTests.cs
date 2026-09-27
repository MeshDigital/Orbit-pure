using SLSKDONET.Utils;
using Xunit;

namespace SLSKDONET.Tests.Utils;

/// <summary>
/// Locks in the single canonical "artist-title" hash format after it silently drifted into three
/// incompatible variants across the codebase (Models.Track.UniqueHash's own formula,
/// SpotifyScraperInputSource's "artist|title", and a short-lived third variant in a fix to
/// SpotifyInputSource) — each mismatch broke ImportOrchestrator's sync-merge dedup, since a track
/// hashed under one variant could never be recognized as already present when a later sync hashed
/// it under another, and got silently re-added as a duplicate. Expected values here are lifted
/// directly from real hashes sampled off already-downloaded tracks in a live library.
/// </summary>
public class TrackHashUtilTests
{
    [Theory]
    [InlineData("Blaine Stranger", "Dragon", "blainestranger-dragon")]
    [InlineData("Sub Focus", "Miracle - VIP Mix", "subfocus-miracle-vipmix")]
    [InlineData("Camo & Krooked", "Nebula", "camo&krooked-nebula")]
    [InlineData("Rova", "Eyes On Me", "rova-eyesonme")]
    public void Compute_MatchesRealLibraryHashes(string artist, string title, string expected)
    {
        Assert.Equal(expected, TrackHashUtil.Compute(artist, title));
    }

    [Fact]
    public void Compute_IsCaseInsensitive()
    {
        Assert.Equal(TrackHashUtil.Compute("ARTIST", "TITLE"), TrackHashUtil.Compute("artist", "title"));
    }

    [Fact]
    public void Compute_StripsAllSpaces_NotJustLeadingTrailing()
    {
        Assert.Equal("twowordartist-twowordtitle", TrackHashUtil.Compute("Two Word Artist", "Two Word Title"));
    }

    [Fact]
    public void Compute_NullArtistOrTitle_DoesNotThrow_AndTrimsTheSeparator()
    {
        Assert.Equal("title", TrackHashUtil.Compute(null, "Title"));
        Assert.Equal("artist", TrackHashUtil.Compute("Artist", null));
        Assert.Equal("", TrackHashUtil.Compute(null, null));
    }

    [Fact]
    public void Compute_MatchesModelsTrackUniqueHash()
    {
        // Track.UniqueHash delegates to TrackHashUtil.Compute — this is the regression guard for
        // that delegation staying intact rather than re-diverging into its own inline formula.
        var track = new SLSKDONET.Models.Track { Artist = "Rova", Title = "Eyes On Me" };
        Assert.Equal(TrackHashUtil.Compute("Rova", "Eyes On Me"), track.UniqueHash);
    }
}
