using System;
using System.Linq;
using SLSKDONET.Models;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class ProjectListViewModelReorderTests
{
    [Fact]
    public void ReorderProjects_InsertAfter_MovesDownAndNormalizesOrder()
    {
        var a = CreatePlaylist("A", manualSortOrder: 40);
        var b = CreatePlaylist("B", manualSortOrder: 30);
        var c = CreatePlaylist("C", manualSortOrder: 20);
        var d = CreatePlaylist("D", manualSortOrder: 10);

        var result = ProjectListViewModel.ReorderProjects(new[] { a, b, c, d }, a.Id, c.Id, insertAfter: true);

        Assert.Equal(new[] { b.Id, c.Id, a.Id, d.Id }, result.Select(project => project.Id));
        Assert.Equal(new[] { 0, 1, 2, 3 }, result.Select(project => project.ManualSortOrder));
    }

    [Fact]
    public void ReorderProjects_InsertBefore_MovesUp()
    {
        var a = CreatePlaylist("A");
        var b = CreatePlaylist("B");
        var c = CreatePlaylist("C");
        var d = CreatePlaylist("D");

        var result = ProjectListViewModel.ReorderProjects(new[] { a, b, c, d }, d.Id, b.Id, insertAfter: false);

        Assert.Equal(new[] { a.Id, d.Id, b.Id, c.Id }, result.Select(project => project.Id));
    }

    [Fact]
    public void ReorderProjects_CrossFolderInsertion_AdoptsTargetFolder()
    {
        var folderId = Guid.NewGuid();
        var dragged = CreatePlaylist("Root");
        var target = CreatePlaylist("In folder", folderId);
        var other = CreatePlaylist("Also in folder", folderId);

        var result = ProjectListViewModel.ReorderProjects(
            new[] { dragged, other, target }, dragged.Id, target.Id, insertAfter: false);

        Assert.Equal(new[] { other.Id, dragged.Id, target.Id }, result.Select(project => project.Id));
        Assert.Equal(folderId, dragged.FolderId);
        Assert.Equal(folderId, target.FolderId);
    }

    [Fact]
    public void ReorderProjects_SelfDrop_IsExactNoOp()
    {
        var folderId = Guid.NewGuid();
        var dragged = CreatePlaylist("A", folderId, manualSortOrder: 12);
        var other = CreatePlaylist("B", manualSortOrder: 30);

        var result = ProjectListViewModel.ReorderProjects(new[] { dragged, other }, dragged.Id, dragged.Id, insertAfter: true);

        Assert.Equal(new[] { dragged.Id, other.Id }, result.Select(project => project.Id));
        Assert.Equal(12, dragged.ManualSortOrder);
        Assert.Equal(30, other.ManualSortOrder);
        Assert.Equal(folderId, dragged.FolderId);
    }

    [Fact]
    public void ReorderProjects_UnknownTarget_IsNoOp()
    {
        var dragged = CreatePlaylist("A", manualSortOrder: 8);
        var other = CreatePlaylist("B", manualSortOrder: 9);

        var result = ProjectListViewModel.ReorderProjects(
            new[] { dragged, other }, dragged.Id, Guid.NewGuid(), insertAfter: false);

        Assert.Equal(new[] { dragged.Id, other.Id }, result.Select(project => project.Id));
        Assert.Equal(8, dragged.ManualSortOrder);
        Assert.Equal(9, other.ManualSortOrder);
    }

    private static PlaylistJob CreatePlaylist(
        string title,
        Guid? folderId = null,
        int manualSortOrder = 0) => new()
    {
        Id = Guid.NewGuid(),
        SourceTitle = title,
        SourceType = "Test",
        FolderId = folderId,
        ManualSortOrder = manualSortOrder
    };
}
