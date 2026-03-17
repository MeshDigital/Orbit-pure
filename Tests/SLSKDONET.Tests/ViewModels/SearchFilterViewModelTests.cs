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
}
