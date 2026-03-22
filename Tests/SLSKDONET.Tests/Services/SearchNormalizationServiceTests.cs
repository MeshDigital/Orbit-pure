using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class SearchNormalizationServiceTests
{
    private readonly SearchNormalizationService _sut = new(NullLogger<SearchNormalizationService>.Instance);

    [Fact]
    public void BuildSearchPlan_ShouldCreateStrictStandardAndDesperateQueries()
    {
        var plan = _sut.BuildSearchPlan("Artist - Track (Original Mix)");

        Assert.Equal("Artist Track (Original Mix)", plan.StrictQuery);
        Assert.Equal("Artist Track", plan.StandardQuery);
        Assert.Equal("Artist", plan.DesperateQuery);
    }

    [Fact]
    public void BuildSearchPlan_ShouldPreferTitleForDesperateQuery_WhenArtistIsStopWord()
    {
        var plan = _sut.BuildSearchPlan("Various Artists - Summer Anthem");

        Assert.Equal("Summer Anthem", plan.DesperateQuery);
    }

    [Fact]
    public void GenerateSearchVariations_ShouldReturnPlannedQueriesInPriorityOrder()
    {
        var variations = _sut.GenerateSearchVariations("Artist - Track (Original Mix)");

        Assert.NotEmpty(variations);
        Assert.Equal("Artist Track (Original Mix)", variations[0]);
        Assert.Contains("Artist Track", variations);
        Assert.Contains("Artist", variations);
    }

    [Fact]
    public void BuildSearchPlan_FromPlaylistTrack_ShouldCarryAlbumAndDurationMetadata()
    {
        var track = new PlaylistTrack
        {
            Artist = "Artist",
            Title = "Track (Extended Mix)",
            Album = "Album Name",
            CanonicalDuration = 245000
        };

        var plan = _sut.BuildSearchPlan(track, "Artist Track (Extended Mix)");

        Assert.Equal("Album Name", plan.Target.Album);
        Assert.Equal(245, plan.Target.DurationSeconds);
        Assert.Equal("Artist", plan.DesperateQuery);
    }

    [Fact]
    public void BuildSearchPlan_FromSearchQuery_ShouldUseQueryMetadata()
    {
        var query = new SearchQuery
        {
            Artist = "Artist",
            Title = "Track",
            Album = "Album",
            CanonicalDuration = 180000
        };

        var plan = _sut.BuildSearchPlan(query);

        Assert.Equal("Album", plan.Target.Album);
        Assert.Equal(180, plan.Target.DurationSeconds);
        Assert.Equal("Artist Track", plan.StrictQuery);
    }
}