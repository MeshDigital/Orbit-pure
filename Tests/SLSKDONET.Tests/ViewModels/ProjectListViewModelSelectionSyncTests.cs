using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class ProjectListViewModelSelectionSyncTests
{
    [Fact]
    public void SelectedProjectCard_UpdatesSelectedProject()
    {
        var sut = CreateUninitializedVm();
        var playlist = CreatePlaylist("Card Selected");
        var card = new LibraryPlaylistCardViewModel(playlist);

        sut.FilteredProjectCards.Add(card);

        sut.SelectedProjectCard = card;

        Assert.Same(card, sut.SelectedProjectCard);
        Assert.Same(playlist, sut.SelectedProject);
    }

    [Fact]
    public void SelectedProject_UpdatesSelectedProjectCard()
    {
        var sut = CreateUninitializedVm();
        var playlistA = CreatePlaylist("A");
        var playlistB = CreatePlaylist("B");
        var cardA = new LibraryPlaylistCardViewModel(playlistA);
        var cardB = new LibraryPlaylistCardViewModel(playlistB);

        sut.FilteredProjectCards.Add(cardA);
        sut.FilteredProjectCards.Add(cardB);

        sut.SelectedProject = playlistB;

        Assert.Same(playlistB, sut.SelectedProject);
        Assert.Same(cardB, sut.SelectedProjectCard);
    }

    [Fact]
    public void SourceGuard_OnPlaylistAdded_InsertsProjectCard()
    {
        var source = ReadProjectListViewModelSource();

        Assert.Contains("FilteredProjectCards.Insert(0, new LibraryPlaylistCardViewModel(job, _artworkCacheService, _mosaicService));", source);
    }

    [Fact]
    public void SourceGuard_OnProjectDeleted_RemovesProjectCard()
    {
        var source = ReadProjectListViewModelSource();

        Assert.Contains("var cardToRemove = FilteredProjectCards.FirstOrDefault(c => c.Model.Id == projectId);", source);
        Assert.Contains("FilteredProjectCards.Remove(cardToRemove);", source);
    }

    private static ProjectListViewModel CreateUninitializedVm()
    {
        var vm = (ProjectListViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ProjectListViewModel));

        SetField(vm, "_logger", NullLogger<ProjectListViewModel>.Instance);
        SetField(vm, "_filteredProjectCards", new ObservableCollection<LibraryPlaylistCardViewModel>());
        SetField(vm, "_filteredProjects", new ObservableCollection<PlaylistJob>());
        SetField(vm, "_allProjects", new ObservableCollection<PlaylistJob>());

        return vm;
    }

    private static PlaylistJob CreatePlaylist(string title)
        => new()
        {
            Id = Guid.NewGuid(),
            SourceTitle = title,
            SourceType = "Test"
        };

    private static void SetField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static string ReadProjectListViewModelSource()
    {
        var root = FindSourceRoot();
        var filePath = Path.Combine(root, "ViewModels", "Library", "ProjectListViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected source file at {filePath}");
        return File.ReadAllText(filePath);
    }

    private static string FindSourceRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (File.Exists(Path.Combine(dir, "SLSKDONET.csproj")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root for tests.");
    }
}
