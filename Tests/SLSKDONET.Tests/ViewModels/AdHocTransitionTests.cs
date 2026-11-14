using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Repositories;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>#204: Library → "Edit transition…" for any two library tracks (AdHoc context).</summary>
public class AdHocTransitionTests
{
    [Fact]
    public async Task TwoLibraryTracks_LoadInTheSharedEditor_WithSaveBlockedHonestly()
    {
        var library = new Mock<ILibraryService>();
        library.Setup(l => l.FindLibraryEntryAsync("a")).ReturnsAsync(new LibraryEntry { UniqueHash = "a", Title = "One", Artist = "X", FilePath = "a.flac", BPM = 174 });
        library.Setup(l => l.FindLibraryEntryAsync("b")).ReturnsAsync(new LibraryEntry { UniqueHash = "b", Title = "Two", Artist = "Y", FilePath = "b.flac", BPM = 174 });
        var repo = new Mock<ITransitionRepository>();
        using var vm = Create(library, repo);

        Assert.True(await vm.LoadAdHocPairAsync("a", "b"));

        Assert.True(vm.HasLoadedPair);
        Assert.False(vm.IsSetContext);
        Assert.Equal(MixTransitionViewModel.AdHocSaveBlockedReason, vm.SaveBlockedReason);
        Assert.False(((System.Windows.Input.ICommand)vm.SaveCommand).CanExecute(null)); // no placebo save
        Assert.Equal("a", vm.OutgoingTrack!.Model.TrackUniqueHash);
        Assert.Equal("b", vm.IncomingTrack!.Model.TrackUniqueHash);
        // The playlist store is never touched for an ad-hoc pair.
        repo.Verify(r => r.UpsertTransitionAsync(It.IsAny<SLSKDONET.Models.Timeline.PlaylistTrackTransition>()), Times.Never);
    }

    [Fact]
    public async Task ATrackOutsideTheLibrary_IsReported()
    {
        var library = new Mock<ILibraryService>();
        library.Setup(l => l.FindLibraryEntryAsync(It.IsAny<string>())).ReturnsAsync((LibraryEntry?)null);
        using var vm = Create(library, new Mock<ITransitionRepository>());

        Assert.False(await vm.LoadAdHocPairAsync("a", "b"));
        Assert.Contains("library", vm.StatusMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static MixTransitionViewModel Create(Mock<ILibraryService> library, Mock<ITransitionRepository> repo)
    {
        var cues = new Mock<ICuePointService>();
        cues.Setup(c => c.GetByTrackIdAsync(It.IsAny<string>(), It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(new List<CuePointEntity>());
        return new MixTransitionViewModel(
            NullLogger<MixTransitionViewModel>.Instance,
            library.Object,
            new EventBusService(),
            null!,
            repo.Object,
            new Mock<ITransitionPreviewPlayer>().Object,
            new Mock<ITrackRepository>().Object,
            cues.Object,
            new Mock<IServiceProvider>().Object);
    }
}
