using System.Linq;
using SLSKDONET.Models;
using SLSKDONET.Services.Search;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>#427: Album-mode search groups by normalized (Artist, Album), picks best-per-track, and keeps alternates.</summary>
public class AlbumSearchGrouperTests
{
    private static AnalyzedSearchResultViewModel MakeResult(
        string artist,
        string title,
        string? album,
        string filename,
        int bitrate,
        double rank,
        string peer,
        bool isFilteredOut = false)
    {
        var track = new Track
        {
            Artist = artist,
            Title = title,
            Album = album,
            Filename = filename,
            Bitrate = bitrate,
            CurrentRank = rank,
            Username = peer,
            HasFreeUploadSlot = true,
        };
        var vm = new AnalyzedSearchResultViewModel(new SearchResult(track));
        if (isFilteredOut)
            vm.SetFilterVisibility(true, "Filtered by quality rule");
        return vm;
    }

    [Fact]
    public void Group_CollapsesDuplicatesPerTrackAndPicksBestMatch()
    {
        var results = new[]
        {
            MakeResult("Daft Punk", "01 - One More Time", "Discovery", "Daft Punk/Discovery/01 - One More Time.mp3", 192, 60, "peerA"),
            MakeResult("Daft Punk", "One More Time", "discovery ", "Daft Punk/Discovery/01. One More Time.flac", 1000, 95, "peerB"),
            MakeResult("Daft Punk", "02 - Aerodynamic", "Discovery", "Daft Punk/Discovery/02 - Aerodynamic.flac", 980, 90, "peerB"),
            MakeResult("Bicep", "Glue", null, "Bicep/Bicep (2017)/03 - Glue.flac", 950, 88, "peerC"),
        };

        var groups = AlbumSearchGrouper.Group(results);

        Assert.Equal(2, groups.Count);
        var discovery = groups[0];
        Assert.Equal("Discovery", discovery.AlbumTitle);
        Assert.Equal("Daft Punk", discovery.ArtistDisplay);
        Assert.Equal(2, discovery.TrackCount);
        Assert.Equal(3, discovery.TotalFilesCount);
        Assert.Equal(2, discovery.PeerCount);

        var oneMoreTime = discovery.Tracks.Single(t => t.Best.RawResult.Title.Contains("One More Time"));
        Assert.Equal("peerB", oneMoreTime.Best.RawResult.Username);
        Assert.Equal(1000, oneMoreTime.Best.RawResult.Bitrate);
        Assert.Single(oneMoreTime.Alternates);
        Assert.Equal("peerA", oneMoreTime.Alternates[0].RawResult.Username);

        // Folder-name fallback when Album tag is missing
        var bicep = groups[1];
        Assert.Equal("Bicep (2017)", bicep.AlbumTitle);
        Assert.Single(bicep.Tracks);
    }

    [Fact]
    public void Group_PrefersUnfilteredCandidateOverFilteredCandidate()
    {
        var results = new[]
        {
            MakeResult("Artist", "Track 1", "LP", "Artist/LP/01 - Track 1.flac", 1000, 99, "badPeer", isFilteredOut: true),
            MakeResult("Artist", "Track 1", "LP", "Artist/LP/01 - Track 1.mp3", 320, 80, "goodPeer", isFilteredOut: false),
        };

        var group = Assert.Single(AlbumSearchGrouper.Group(results));
        var track = Assert.Single(group.Tracks);
        Assert.Equal("goodPeer", track.Best.RawResult.Username);
        Assert.Single(track.Alternates);
        Assert.Equal("badPeer", track.Alternates[0].RawResult.Username);
    }
}
