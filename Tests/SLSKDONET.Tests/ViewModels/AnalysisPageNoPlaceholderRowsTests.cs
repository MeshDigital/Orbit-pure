using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// 2026-10-19: on a fresh install with a broken schema the Analysis page showed 2,650
/// "Selected Artist — Selected Track" rows, each marked "Analysis failed", over the real
/// "Couldn't load your library" error. Analysis requests for tracks the page hasn't loaded must
/// resolve to the real library entry or show nothing — never an invented row.
/// </summary>
[Collection(NonParallelCollection.Name)]
public class AnalysisPageNoPlaceholderRowsTests : IDisposable
{
    private readonly EventBusService _bus = new();
    private readonly Mock<ILibraryService> _library = new();
    private AnalysisPageViewModel? _vm;

    public void Dispose()
    {
        _vm?.Dispose();
        _bus.Dispose();
    }

    private AnalysisPageViewModel CreateVm()
    {
        _vm = new AnalysisPageViewModel(_bus, new LifecycleProjectionService(_library.Object), _library.Object);
        return _vm;
    }

    // The initial library load runs in the background (file-existence sweep); let it finish so it
    // doesn't clear rows added by the request under test.
    private static void WaitForLibraryLoad(AnalysisPageViewModel vm)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.IsLibraryLoading && sw.Elapsed < TimeSpan.FromSeconds(5))
            System.Threading.Thread.Sleep(10);
        Assert.False(vm.IsLibraryLoading, "library load did not finish");
    }

    [Fact]
    public void LibraryLoadFails_AnalysisRequestsAndFailures_AddNoRows()
    {
        _library.Setup(l => l.LoadAllLibraryEntriesAsync())
            .ThrowsAsync(new InvalidOperationException("SQLite Error 1: 'no such column: a.VectorEmbedding'."));
        var vm = CreateVm();
        Assert.True(vm.HasLibraryLoadError);
        Assert.Contains("VectorEmbedding", vm.LibraryLoadErrorMessage);

        for (var i = 0; i < 25; i++)
        {
            _bus.Publish(new TrackAnalysisRequestedEvent($"hash-{i}"));
            _bus.Publish(new TrackAnalysisFailedEvent($"hash-{i}", "no such column"));
        }

        Assert.Empty(vm.LibraryTracks);
        Assert.Empty(vm.AnalysisQueue);
        Assert.DoesNotContain(vm.LibraryTracks, t => t.Title == "Selected Track");
        // The lookup isn't even attempted: the library can't be read and the error already says so.
        _library.Verify(l => l.FindLibraryEntryAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RequestForUnlistedTrack_ResolvesTheRealEntry()
    {
        _library.Setup(l => l.LoadAllLibraryEntriesAsync()).ReturnsAsync(new List<LibraryEntry>());
        _library.Setup(l => l.GetAllPlaylistTracksAsync()).ReturnsAsync(new List<PlaylistTrack>());
        _library.Setup(l => l.FindLibraryEntryAsync("new-hash"))
            .ReturnsAsync(new LibraryEntry { UniqueHash = "new-hash", Artist = "Real Artist", Title = "Real Title" });
        var vm = CreateVm();

        WaitForLibraryLoad(vm);
        Assert.False(vm.HasLibraryLoadError);

        _bus.Publish(new TrackAnalysisRequestedEvent("new-hash"));

        _library.Verify(l => l.FindLibraryEntryAsync("new-hash"), Times.Once);
        var row = Assert.Single(vm.AnalysisQueue);
        Assert.Equal("Real Artist", row.Artist);
        Assert.Equal("Real Title", row.Title);
        Assert.Contains(row, vm.LibraryTracks);
    }

    [Fact]
    public void RequestForTrackNotInLibrary_AddsNoRow_AndItsFailureIsNotCounted()
    {
        _library.Setup(l => l.LoadAllLibraryEntriesAsync()).ReturnsAsync(new List<LibraryEntry>());
        _library.Setup(l => l.GetAllPlaylistTracksAsync()).ReturnsAsync(new List<PlaylistTrack>());
        _library.Setup(l => l.FindLibraryEntryAsync(It.IsAny<string>())).ReturnsAsync((LibraryEntry?)null);
        var vm = CreateVm();
        WaitForLibraryLoad(vm);
        Assert.False(vm.HasLibraryLoadError);

        _bus.Publish(new TrackAnalysisRequestedEvent("ghost-hash"));
        _bus.Publish(new TrackAnalysisFailedEvent("ghost-hash", "Track file not found for analysis: ghost-hash"));

        _library.Verify(l => l.FindLibraryEntryAsync("ghost-hash"), Times.Once);
        Assert.Empty(vm.LibraryTracks);
        Assert.Empty(vm.AnalysisQueue);
        Assert.Equal(0, vm.QueueTrackCount);
    }
}
