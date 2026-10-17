using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// Regression coverage for the import-preview commit button staying disabled ("not highlighted")
/// when it should be enabled. The button is driven purely by AddToLibraryCommand.CanExecute, and
/// Avalonia only re-queries that on CanExecuteChanged — which used to be raised only on checkbox
/// toggles, never when loading finished, a merge target was picked or the destination changed.
/// </summary>
public class ImportPreviewCommitStateTests
{
    private static ImportPreviewViewModel CreateSut(Mock<ILibraryService>? library = null)
    {
        library ??= CreateLibrary();
        var vm = new ImportPreviewViewModel(
            NullLogger<ImportPreviewViewModel>.Instance,
            null!,
            new Mock<INavigationService>().Object,
            library.Object);
        vm.RunOnUiThread = a => { a(); return Task.CompletedTask; };
        return vm;
    }

    private static Mock<ILibraryService> CreateLibrary(params PlaylistJob[] jobs)
    {
        var library = new Mock<ILibraryService>();
        library.Setup(l => l.LoadAllLibraryEntriesAsync()).ReturnsAsync(new List<LibraryEntry>());
        library.Setup(l => l.LoadAllPlaylistJobsAsync()).ReturnsAsync(jobs.ToList());
        library.Setup(l => l.LoadPlaylistTracksAsync(It.IsAny<Guid>())).ReturnsAsync(new List<PlaylistTrack>());
        return library;
    }

    private static List<SearchQuery> Queries(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new SearchQuery { Artist = $"Artist {i}", Title = $"Title {i}" })
            .ToList();

    [Fact]
    public async Task LoadCompletes_WithTracksAutoSelected_CommandBecomesExecutable_AndRaisesCanExecuteChanged()
    {
        var vm = CreateSut();
        vm.InitializeStreamingPreview("Spotify", "Spotify", Guid.NewGuid(), "https://open.spotify.com/playlist/abc");
        await vm.AddTracksToPreviewAsync(Queries(3));

        // Tracks are auto-selected while still loading — the command must not run yet.
        Assert.True(vm.SelectedCount > 0);
        Assert.False(vm.AddToLibraryCommand.CanExecute(null));

        var raised = 0;
        vm.AddToLibraryCommand.CanExecuteChanged += (_, _) => raised++;

        vm.IsLoading = false; // what ImportOrchestrator does once the stream is exhausted

        Assert.True(raised > 0, "Finishing the load must re-query the commit button's CanExecute");
        Assert.True(vm.AddToLibraryCommand.CanExecute(null));
    }

    [Fact]
    public async Task MergeMode_WithoutTarget_CannotExecute()
    {
        var vm = CreateSut();
        vm.InitializeStreamingPreview("Spotify", "Spotify", Guid.NewGuid(), "https://open.spotify.com/playlist/abc");
        await vm.AddTracksToPreviewAsync(Queries(2));
        vm.IsLoading = false;
        Assert.True(vm.AddToLibraryCommand.CanExecute(null));

        var raised = 0;
        vm.AddToLibraryCommand.CanExecuteChanged += (_, _) => raised++;

        vm.IsMergeMode = true;

        Assert.True(raised > 0, "Switching destination must re-query CanExecute");
        Assert.False(vm.AddToLibraryCommand.CanExecute(null));
    }

    [Fact]
    public async Task MergeMode_PickingATarget_MakesCommandExecutable()
    {
        var target = new PlaylistJob { Id = Guid.NewGuid(), SourceTitle = "Night Drive", SourceType = "Spotify", TotalTracks = 10 };
        var vm = CreateSut(CreateLibrary(target));
        vm.InitializeStreamingPreview("Spotify", "Spotify", Guid.NewGuid(), "https://open.spotify.com/playlist/abc");
        await vm.AddTracksToPreviewAsync(Queries(2));
        vm.IsLoading = false;
        vm.IsMergeMode = true;
        Assert.False(vm.AddToLibraryCommand.CanExecute(null));

        var raised = 0;
        vm.AddToLibraryCommand.CanExecuteChanged += (_, _) => raised++;

        vm.SelectedMergeTarget = vm.MergeTargets.Single(t => t.Job.Id == target.Id);

        Assert.True(raised > 0, "Picking a merge target must re-query CanExecute");
        Assert.True(vm.AddToLibraryCommand.CanExecute(null));
    }
}
