using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class SearchFilterViewModelTests
{
    [Fact]
    public void GetHiddenReason_ShouldExplainFormatFilter()
    {
        var viewModel = new SearchFilterViewModel();
        viewModel.SelectedFormats.Clear();
        viewModel.SelectedFormats.Add("MP3");

        var result = new SearchResult(new Track
        {
            Filename = "Artist - Title.flac",
            Bitrate = 900,
            Length = 200,
            Size = 22_000_000,
            Format = "flac"
        });

        var reason = viewModel.GetHiddenReason(result);

        Assert.Equal("Format filtered out (FLAC)", reason);
    }

    [Fact]
    public void GetHiddenReason_ShouldExplainBitrateFloor()
    {
        var viewModel = new SearchFilterViewModel
        {
            MinBitrate = 320
        };

        var result = new SearchResult(new Track
        {
            Filename = "Artist - Title.mp3",
            Bitrate = 128,
            Length = 200,
            Size = 3_200_000,
            Format = "mp3"
        });

        var reason = viewModel.GetHiddenReason(result);

        Assert.Contains("Bitrate below filter floor", reason);
    }

    /// <summary>
    /// Files that Soulseek peers share without embedded bitrate metadata arrive with
    /// Bitrate == 0.  The filter must treat unknown bitrate as "pass" so that the
    /// majority of Soulseek results are not silently hidden on the search page.
    /// </summary>
    [Fact]
    public void GetFilterPredicate_ShouldPassResultsWithUnknownBitrate()
    {
        var viewModel = new SearchFilterViewModel { MinBitrate = 320 };

        var result = new SearchResult(new Track
        {
            Filename = "Artist - Title.mp3",
            Bitrate = 0,   // No bitrate metadata reported
            Format = "mp3"
        });

        var predicate = viewModel.GetFilterPredicate();

        Assert.True(predicate(result), "Results with unknown bitrate (0) should not be filtered out.");
    }

    [Fact]
    public void GetFilterPredicate_ShouldRejectResultsWithKnownLowBitrate()
    {
        var viewModel = new SearchFilterViewModel { MinBitrate = 320 };

        var result = new SearchResult(new Track
        {
            Filename = "Artist - Title.mp3",
            Bitrate = 128,  // Known low bitrate — should be filtered
            Format = "mp3"
        });

        var predicate = viewModel.GetFilterPredicate();

        Assert.False(predicate(result), "Results with known low bitrate should be filtered out.");
    }

    [Fact]
    public void GetHiddenReason_ShouldReturnNullForUnknownBitrate()
    {
        var viewModel = new SearchFilterViewModel { MinBitrate = 320 };

        var result = new SearchResult(new Track
        {
            Filename = "Artist - Title.mp3",
            Bitrate = 0,   // No bitrate metadata
            Format = "mp3"
        });

        var reason = viewModel.GetHiddenReason(result);

        Assert.Null(reason);
    }

    [Fact]
    public void IsMatch_ShouldReturnTrueForUnknownBitrate()
    {
        var viewModel = new SearchFilterViewModel { MinBitrate = 320 };

        var result = new SearchResult(new Track
        {
            Filename = "Artist - Title.mp3",
            Bitrate = 0,
            Format = "mp3"
        });

        Assert.True(viewModel.IsMatch(result));
    }
}
