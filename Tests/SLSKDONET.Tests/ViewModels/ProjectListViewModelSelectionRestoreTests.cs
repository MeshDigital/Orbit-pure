using System;
using SLSKDONET.Models;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class ProjectListViewModelSelectionRestoreTests
{
    [Fact]
    public void ReloadedProjectWithSameId_ReplacesStaleSelectedInstance()
    {
        var selected = CreatePlaylist("Selected");
        var reloaded = CreatePlaylist("Selected reloaded", selected.Id);
        var other = CreatePlaylist("Other");

        var restored = ProjectListViewModel.FindRestoredSelection(
            new[] { other, reloaded }, selected, CreateAllTracks());

        Assert.Same(reloaded, restored);
        Assert.NotSame(selected, restored);
    }

    [Fact]
    public void AllTracksSelection_SurvivesProjectReload()
    {
        var previousAllTracks = CreateAllTracks();
        var currentAllTracks = CreateAllTracks();

        var restored = ProjectListViewModel.FindRestoredSelection(
            new[] { CreatePlaylist("Playlist") }, previousAllTracks, currentAllTracks);

        Assert.Same(currentAllTracks, restored);
    }

    [Fact]
    public void DeletedSelection_FallsBackToFirstAvailableProject()
    {
        var deleted = CreatePlaylist("Deleted");
        var first = CreatePlaylist("First");
        var second = CreatePlaylist("Second");

        var restored = ProjectListViewModel.FindRestoredSelection(
            new[] { first, second }, deleted, CreateAllTracks());

        Assert.Same(first, restored);
    }

    [Fact]
    public void NoCurrentSelection_SelectsFirstAvailableProject()
    {
        var first = CreatePlaylist("First");

        var restored = ProjectListViewModel.FindRestoredSelection(
            new[] { first, CreatePlaylist("Second") }, null, CreateAllTracks());

        Assert.Same(first, restored);
    }

    private static PlaylistJob CreatePlaylist(string title, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        SourceTitle = title,
        SourceType = "Test"
    };

    private static PlaylistJob CreateAllTracks() => new()
    {
        Id = Guid.Empty,
        SourceTitle = "All Tracks",
        SourceType = "Global Library"
    };
}
