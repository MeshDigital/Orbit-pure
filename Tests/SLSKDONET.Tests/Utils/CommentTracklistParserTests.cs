using SLSKDONET.Utils;
using Xunit;

namespace SLSKDONET.Tests.Utils;

public class CommentTracklistParserTests
{
    [Fact]
    public void Parse_TracklistWithTimestampDashTitle_ParsesAllTracks()
    {
        var input = """
        🎧 Tracklist:
        00:00 - Razor in the Rain
        02:54 - Black Squad
        05:15 - Grin In The Ashes
        08:06 - Fall Into The Void
        12:08 - Glass in My Veins
        15:18 - Teeth On The Floor
        18:44 - Bite Back The Void
        21:07 - Cathedral of Static
        23:27 - Shards in My Veins
        26:08 - Bruised Knuckles, Quiet Halo
        """;

        var result = CommentTracklistParser.Parse(input);

        Assert.Equal(10, result.Count);
        Assert.All(result, track => Assert.Equal("Unknown Artist", track.Artist));
        Assert.Equal("Razor in the Rain", result[0].Title);
        Assert.Equal("Bruised Knuckles, Quiet Halo", result[^1].Title);
    }

    [Fact]
    public void Parse_TracklistWithArtistAndTitle_StillParsesArtistTitlePairs()
    {
        var input = """
        Artist One - First Song
        Artist Two - Second Song
        """;

        var result = CommentTracklistParser.Parse(input);

        Assert.Equal(2, result.Count);
        Assert.Equal("Artist One", result[0].Artist);
        Assert.Equal("First Song", result[0].Title);
        Assert.Equal("Artist Two", result[1].Artist);
        Assert.Equal("Second Song", result[1].Title);
    }

    private const string Sample1001Tracklist = """
    Kanine @ Summer Essentials Vol. 8 2026-06-29

    [00:00] Kanine ft. Poppy Basckomb - Wide Awake [UKF]
    [01:40] Metrik - Fatso [HOSPITAL]
    [02:30] Sub Focus ft. Fireboy DML & IRAH - Original Don [POSITIVA]
    w/ Synergy ft. RIENK - Stay [UKF]
    [10:50] KETTAMA - Comes and Goes (Soldat D&B Edit)

    Please set a backlink to keep the tracklist up-to-date: https://1001.tl/2r76u8p1
    """;

    [Fact]
    public void Parse_WithOutTitle_DetectsHeaderLineAsPlaylistTitle()
    {
        var tracks = CommentTracklistParser.Parse(Sample1001Tracklist, out var detectedTitle);

        Assert.Equal("Kanine @ Summer Essentials Vol. 8 2026-06-29", detectedTitle);
        Assert.NotEmpty(tracks);
    }

    [Fact]
    public void Parse_IgnoresTrailingBacklinkLine()
    {
        var tracks = CommentTracklistParser.Parse(Sample1001Tracklist, out _);

        Assert.DoesNotContain(tracks, t =>
            t.Artist.Contains("backlink", System.StringComparison.OrdinalIgnoreCase) ||
            t.Title.Contains("backlink", System.StringComparison.OrdinalIgnoreCase) ||
            t.Title.Contains("1001.tl", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_ExtractsExpectedTrackCountAndMixMarkerTrack()
    {
        var tracks = CommentTracklistParser.Parse(Sample1001Tracklist, out _);

        Assert.Equal(5, tracks.Count);
        Assert.Contains(tracks, t => t.Artist.Contains("Synergy") && t.Title.Contains("Stay"));
    }

    [Fact]
    public void Parse_NoLeadingHeaderLine_DetectedTitleIsNull()
    {
        var tracks = CommentTracklistParser.Parse("Artist One - Title One\nArtist Two - Title Two", out var detectedTitle);

        Assert.Null(detectedTitle);
        Assert.Equal(2, tracks.Count);
    }

    [Fact]
    public void Parse_DetectsBacklinkUrl()
    {
        CommentTracklistParser.Parse(Sample1001Tracklist, out _, out var detectedSourceUrl);

        Assert.Equal("https://1001.tl/2r76u8p1", detectedSourceUrl);
    }

    [Fact]
    public void Parse_NoUrlInInput_DetectedSourceUrlIsNull()
    {
        CommentTracklistParser.Parse("Artist One - Title One\nArtist Two - Title Two", out _, out var detectedSourceUrl);

        Assert.Null(detectedSourceUrl);
    }

    [Fact]
    public void Parse_FiltersUnidentifiedIdTracks()
    {
        var input = """
        [00:00] Friction - ID
        [01:44] Missy Elliott - Get UR Freak On (SKIYE Remix)
        [11:40] ID - ID
        [12:32] SOTA - Poison
        """;

        var tracks = CommentTracklistParser.Parse(input);

        Assert.Equal(2, tracks.Count);
        Assert.DoesNotContain(tracks, t => t.Title.Equals("ID", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tracks, t => t.Artist.Contains("Missy Elliott"));
        Assert.Contains(tracks, t => t.Artist.Contains("SOTA"));
    }

    [Fact]
    public void Parse_HeaderLineContainsHyphen_StillDetectedAsTitleNotTrack()
    {
        // Regression: a header like "Friction - Elevate: Live 005 2026-07-16" contains a "-"
        // separator, so on its own it looks exactly like a track line. It must still be detected
        // as the header (not parsed as a bogus "Friction" / "Elevate: Live 005..." track) because
        // every real track in this paste carries a timestamp and the header line does not.
        var input = """
        Friction - Elevate: Live 005 2026-07-16

        [00:00] Friction - ID
        [01:44] Missy Elliott - Get UR Freak On (SKIYE Remix)
        """;

        var tracks = CommentTracklistParser.Parse(input, out var detectedTitle);

        Assert.Equal("Friction - Elevate: Live 005 2026-07-16", detectedTitle);
        Assert.Single(tracks);
        Assert.Contains(tracks, t => t.Artist.Contains("Missy Elliott"));
    }
}
