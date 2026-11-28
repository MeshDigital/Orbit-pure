using System.Collections.Generic;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Intake;
using Xunit;

namespace SLSKDONET.Tests.Intake;

public sealed class IntakeLibraryIndexTests
{
    private static IntakeLibraryTrack Track(string hash, string artist, string title, int? duration,
        string format = "mp3", int bitrate = 320, string? content = null, string? path = null) =>
        new(hash, path ?? $@"D:\Music\{hash}.{format}", artist, title, format, bitrate, duration, content);

    private static IntakeLibraryIndex Index(IEnumerable<IntakeLibraryTrack> tracks, ISet<string>? missing = null) =>
        new(tracks, p => missing == null || !missing.Contains(p));

    [Fact]
    public void ContentHash_MatchesWhateverTheTags()
    {
        var index = Index(new[] { Track("a", "Someone", "Something", 300, content: "pcm:abc") });
        var m = index.FindMatch("pcm:abc", null, null, 0);
        Assert.NotNull(m);
        Assert.Equal(IntakeMatchKind.ContentHash, m!.Kind);
    }

    [Fact]
    public void Metadata_MatchesAcrossCaseAndPunctuation_WithinTwoSeconds()
    {
        var index = Index(new[] { Track("a", "The Prototypes", "Pale Blue Dot", 301) });
        Assert.Equal("a", index.FindMatch(null, "prototypes", "Pale Blue Dot (Original Mix)", 303)?.TrackHash);
        Assert.Null(index.FindMatch(null, "The Prototypes", "Pale Blue Dot", 305));
    }

    [Fact]
    public void Remix_DoesNotMatchTheOriginal()
    {
        var index = Index(new[] { Track("a", "Artist", "Track", 300) });
        Assert.Null(index.FindMatch(null, "Artist", "Track (Someone Remix)", 300));
    }

    [Fact]
    public void ExtendedAndRadioEdit_AreSeparatedByDuration()
    {
        var index = Index(new[] { Track("radio", "Artist", "Track", 190) });
        Assert.Null(index.FindMatch(null, "Artist", "Track (Extended Mix)", 380));
    }

    [Fact]
    public void SeveralOwnedCopies_ReturnsTheBestOne()
    {
        var index = Index(new[]
        {
            Track("low", "Artist", "Track", 300, "mp3", 128),
            Track("flac", "Artist", "Track", 300, "flac", 900),
        });
        Assert.Equal("flac", index.FindMatch(null, "Artist", "Track", 300)?.TrackHash);
    }

    [Fact]
    public void OwnedTrackWhoseFileIsGone_DoesNotMatch()
    {
        var gone = Track("a", "Artist", "Track", 300, content: "pcm:x");
        var index = Index(new[] { gone }, new HashSet<string> { gone.FilePath });
        Assert.Null(index.FindMatch("pcm:x", "Artist", "Track", 300));
    }

    [Fact]
    public void UntaggedFile_MatchesNothingByName()
    {
        var index = Index(new[] { Track("a", "Artist", "Track", 300) });
        Assert.Null(index.FindMatch(null, null, "Track", 300));
    }
}
