using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels.Library;

public class BulkRenameViewModelTests
{
    [Fact]
    public void Resolve_SubstitutesEveryToken()
    {
        var result = BulkRenameViewModel.Resolve(
            "{artist} - {title} ({album}) [{tracknumber}] {year} {genre}",
            artist: "Chase & Status", title: "Massive & Crew", album: "No More Idols",
            trackNumber: "4", year: "2011", genre: "Drum & Bass");

        Assert.Equal("Chase & Status - Massive & Crew (No More Idols) [4] 2011 Drum & Bass", result);
    }

    [Fact]
    public void Resolve_MissingFieldsBecomeEmptyNotLiteralToken()
    {
        var result = BulkRenameViewModel.Resolve(
            "{artist} - {title} [{tracknumber}]",
            artist: "Artist", title: "Title", album: "", trackNumber: "", year: "", genre: "");

        Assert.Equal("Artist - Title []", result);
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveToTokenCasing()
    {
        var result = BulkRenameViewModel.Resolve(
            "{ARTIST} - {Title}",
            artist: "Artist", title: "Title", album: "", trackNumber: "", year: "", genre: "");

        Assert.Equal("Artist - Title", result);
    }

    [Fact]
    public void Resolve_PatternWithNoTokens_ReturnsPatternVerbatim()
    {
        var result = BulkRenameViewModel.Resolve(
            "fixed-name",
            artist: "Artist", title: "Title", album: "", trackNumber: "", year: "", genre: "");

        Assert.Equal("fixed-name", result);
    }

    [Fact]
    public void PreviewLines_ReflectsCurrentPatternAndAppendsExtension()
    {
        var vm = new BulkRenameViewModel(2, new[]
        {
            new BulkRenamePreviewTrack { Artist = "A", Title = "B", Extension = ".mp3" },
        });

        vm.Pattern = "{artist} - {title}";

        Assert.Equal(new[] { "A - B.mp3" }, vm.PreviewLines);
    }

    [Fact]
    public void CanSave_FalseWhenPatternBlank()
    {
        var vm = new BulkRenameViewModel(1, System.Array.Empty<BulkRenamePreviewTrack>());
        vm.Pattern = "   ";

        Assert.False(vm.CanSave);
    }

    [Fact]
    public void HasPreview_FalseWhenNoTracksSelected()
    {
        var vm = new BulkRenameViewModel(0, System.Array.Empty<BulkRenamePreviewTrack>());

        Assert.False(vm.HasPreview);
    }
}
