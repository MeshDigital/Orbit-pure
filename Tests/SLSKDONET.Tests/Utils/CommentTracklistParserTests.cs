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
}
