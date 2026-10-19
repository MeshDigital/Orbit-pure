using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.Models;

public class AudioFileFormatTests
{
    [Theory]
    [InlineData("FLAC", null, "flac")]
    [InlineData(".mp3", null, "mp3")]
    [InlineData("Unknown", @"D:\Music\Blue Soul - Walk-r (Particle Remix).flac", "flac")] // the reported "UNKNOWN" badge
    [InlineData("", @"D:\Music\track.MP3", "mp3")]
    [InlineData(null, @"D:\Music\track.aiff", "aiff")]
    [InlineData("—", null, null)]
    [InlineData(null, @"D:\Music\track.txt", null)]   // not an audio extension
    [InlineData(null, @"D:\Music\no-extension", null)]
    [InlineData(null, null, null)]
    public void Resolve_PrefersARealStoredFormat_ThenTheFileExtension(string? stored, string? path, string? expected)
    {
        Assert.Equal(expected, AudioFileFormat.Resolve(stored, path));
    }

    [Fact]
    public void QualityBadge_ShowsTheFileFormat_InsteadOfUnknown()
    {
        var vm = new PlaylistTrackViewModel(new PlaylistTrack
        {
            Format = "Unknown",
            ResolvedFilePath = @"D:\Music\Blue Soul - Walk-r (Particle Remix).flac",
        });

        Assert.Equal("FLAC", vm.QualityBadgeText);
        Assert.Equal("FLAC", vm.FormatDisplay);
        Assert.True(vm.IsTierLossless);
        Assert.True(vm.HasQualityBadge);
        Assert.Equal("FLAC", vm.FormatBitrateDisplay);
    }

    [Fact]
    public void QualityBadge_HidesWhenNothingIsKnown()
    {
        var vm = new PlaylistTrackViewModel(new PlaylistTrack { Format = null, ResolvedFilePath = null });

        Assert.Equal(string.Empty, vm.QualityBadgeText);
        Assert.False(vm.HasQualityBadge);
        Assert.Equal("—", vm.FormatDisplay);
        Assert.False(vm.HasFormatFact);
    }

    [Fact]
    public void QualityBadge_ShowsBitrateForLossyFiles()
    {
        var vm = new PlaylistTrackViewModel(new PlaylistTrack { Format = "mp3", Bitrate = 256 });
        Assert.Equal("MP3 256", vm.QualityBadgeText);
        Assert.True(vm.IsTierLowBitrate);
    }

    [Theory]
    [InlineData(0, true, true)]      // unknown width: full toolbar
    [InlineData(1200, true, true)]
    [InlineData(700, false, true)]   // quality chips move into the Filters menu
    [InlineData(320, false, false)]  // status segment too; search narrows
    public void Toolbar_MovesWhatDoesNotFitIntoTheFiltersMenu(double width, bool tierChips, bool statusSegment)
    {
        Assert.Equal((tierChips, statusSegment), TrackListColumnLayout.FitToolbar(width));
    }
}
