using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Models;
using SLSKDONET.ViewModels.Downloads;
using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

/// <summary>
/// The Download Center's single filter strip (All | Active | Queued | Failed | Completed + search)
/// drives which ledger sections show and which empty state appears. These pin that contract.
/// </summary>
[Collection(NonParallelCollection.Name)]
public class DownloadCenterFilterStripTests
{
    [Fact]
    public void FreshCenter_ShowsFirstRunEmptyState_NotFilterEmptyState()
    {
        using var sut = CreateSut(out _);

        Assert.True(sut.FilterAll);
        Assert.False(sut.IsFilterActive);
        Assert.True(sut.IsEmptyStateVisible);
        Assert.False(sut.IsFilterEmptyStateVisible);
    }

    [Fact]
    public void SelectingASegment_IsAFilter_AndClearResetsIt()
    {
        using var sut = CreateSut(out _);

        sut.FilterFailed = true;

        Assert.Equal(DownloadStatusFilter.Failed, sut.StatusFilter);
        Assert.False(sut.FilterAll);
        Assert.True(sut.IsFilterActive);
        Assert.False(sut.IsEmptyStateVisible);
        Assert.True(sut.IsFilterEmptyStateVisible);
        Assert.Equal("Nothing failed", sut.FilterEmptyTitle);

        sut.SearchText = "boards";
        Assert.Contains("boards", sut.FilterEmptyTitle);

        ((ICommand)sut.ClearFiltersCommand).Execute(null);

        Assert.Equal(DownloadStatusFilter.All, sut.StatusFilter);
        Assert.Equal(string.Empty, sut.SearchText);
        Assert.False(sut.IsFilterActive);
    }

    [Fact]
    public void RadioAdapter_IgnoresUncheck()
    {
        using var sut = CreateSut(out _);
        sut.FilterQueued = true;

        sut.FilterActive = false; // RadioButton un-checking a sibling must not change the filter

        Assert.Equal(DownloadStatusFilter.Queued, sut.StatusFilter);
    }

    [Fact]
    public async Task OnHoldTracks_ShowUnderAllAndFailed_ButNotUnderCompleted()
    {
        using var sut = CreateSut(out _);
        sut.UnfindableTracks.Add(new UnfindableTrackViewModel(
            Guid.NewGuid(), Guid.NewGuid(), "Artist", "Title", "Playlist", 3, _ => Task.CompletedTask));

        // AttentionRows is rebuilt by the throttled SearchText subscription.
        sut.SearchText = " ";
        await Task.Delay(400);

        Assert.Single(sut.AttentionRows);
        Assert.Single(sut.VisibleUnfindableTracks);
        Assert.Equal(1, sut.AllFilterCount);
        Assert.True(sut.IsAttentionSectionVisible);
        Assert.False(sut.IsEmptyStateVisible);

        sut.StatusFilter = DownloadStatusFilter.Failed;
        Assert.True(sut.IsAttentionSectionVisible);

        sut.StatusFilter = DownloadStatusFilter.Completed;
        Assert.False(sut.IsAttentionSectionVisible);
        Assert.True(sut.IsFilterEmptyStateVisible);
    }

    [Fact]
    public void PeerLanes_StayHidden_WhenNoPeersAreSending()
    {
        using var sut = CreateSut(out _);

        sut.ShowPeerLanes = true;

        Assert.False(sut.IsPeerLanesAvailable);
        Assert.False(sut.IsPeerLanesVisible);
    }

    [Fact]
    public void EngineLogMenuLabel_FollowsToggle()
    {
        using var sut = CreateSut(out _);
        Assert.Equal("Show engine log", sut.EngineLogMenuLabel);

        ((ICommand)sut.ToggleLogsCommand).Execute(null);

        Assert.True(sut.ShowEngineLogs);
        Assert.Equal("Hide engine log", sut.EngineLogMenuLabel);
    }

    [Fact]
    public void EmptyStateCtas_NavigateToSearchAndImport()
    {
        using var sut = CreateSut(out var eventBus);
        var pages = new List<string>();
        using var sub = eventBus.GetEvent<NavigateToPageEvent>().Subscribe(e => pages.Add(e.PageName));

        ((ICommand)sut.NavigateToSearchCommand).Execute(null);
        ((ICommand)sut.NavigateToImportCommand).Execute(null);

        Assert.Equal(new[] { "Search", "Import" }, pages);
    }

    [Fact]
    public void ClearCompleted_WithNothingToClear_SaysSo()
    {
        var notifications = new Mock<INotificationService>();
        using var sut = CreateSut(out _, notifications.Object);

        ((ICommand)sut.ClearCompletedCommand).Execute(null);

        notifications.Verify(n => n.Show("Downloads", "Nothing completed to clear", NotificationType.Information, It.IsAny<TimeSpan?>(), It.IsAny<string?>()), Times.Once);
    }

    private static DownloadCenterViewModel CreateSut(out EventBusService eventBus, INotificationService? notifications = null)
    {
        var downloadManager = (DownloadManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DownloadManager));
        SetField(downloadManager, "_downloads", new List<DownloadContext>());
        SetField(downloadManager, "_collectionLock", new object());

        eventBus = new EventBusService();
        return new DownloadCenterViewModel(
            downloadManager,
            eventBus,
            new AppConfig(),
            (ArtworkCacheService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ArtworkCacheService)),
            Mock.Of<ILibraryService>(),
            (DatabaseService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DatabaseService)),
            Mock.Of<IDbContextFactory<AppDbContext>>(),
            Mock.Of<IDialogService>(),
            notifications ?? Mock.Of<INotificationService>());
    }

    private static void SetField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }
}
