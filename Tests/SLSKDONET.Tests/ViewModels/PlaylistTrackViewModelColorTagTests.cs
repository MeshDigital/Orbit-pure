using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

// ─────────────────────────────────────────────────────────────────────────
// PlaylistTrackViewModel.ColorTag — mirrors the established Rating settable-
// property pattern: setter updates the model, raises PropertyChanged, and
// fire-and-forgets persistence via ILibraryService.UpdateColorTagAsync.
// ─────────────────────────────────────────────────────────────────────────

public class PlaylistTrackViewModelColorTagTests
{
    private static (PlaylistTrackViewModel vm, Mock<ILibraryService> libraryService) BuildVm(string? initialColorTag = null)
    {
        var mockLibraryService = new Mock<ILibraryService>();
        var track = new PlaylistTrack
        {
            TrackUniqueHash = "hash-123",
            Title = "Test Track",
            Artist = "Test Artist",
            ColorTag = initialColorTag,
        };

        var vm = new PlaylistTrackViewModel(track, libraryService: mockLibraryService.Object);
        return (vm, mockLibraryService);
    }

    [Fact]
    public void SettingColorTag_UpdatesModelAndPersistsViaLibraryService()
    {
        var (vm, mockLibraryService) = BuildVm();

        vm.ColorTag = "#FF0000";

        Assert.Equal("#FF0000", vm.Model.ColorTag);
        mockLibraryService.Verify(s => s.UpdateColorTagAsync("hash-123", "#FF0000"), Times.Once);
    }

    [Fact]
    public void ClearingColorTag_PersistsNull()
    {
        var (vm, mockLibraryService) = BuildVm(initialColorTag: "#0000FF");

        vm.ColorTag = null;

        Assert.Null(vm.Model.ColorTag);
        mockLibraryService.Verify(s => s.UpdateColorTagAsync("hash-123", null), Times.Once);
    }

    [Fact]
    public void SettingSameColorTag_DoesNotReTriggerPersistence()
    {
        var (vm, mockLibraryService) = BuildVm(initialColorTag: "#00FF00");

        vm.ColorTag = "#00FF00";

        mockLibraryService.Verify(s => s.UpdateColorTagAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }
}
