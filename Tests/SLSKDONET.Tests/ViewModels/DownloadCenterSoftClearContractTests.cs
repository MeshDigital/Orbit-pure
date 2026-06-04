using System;
using System.Reflection;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels.Downloads;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class DownloadCenterSoftClearContractTests
{
    [Fact]
    public void UnifiedTrackViewModel_HydratesSoftClearFromModel()
    {
        var model = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            TrackUniqueHash = "soft-clear-hash",
            Status = TrackStatus.Downloaded,
            IsClearedFromDownloadCenter = true
        };

        var downloadManager = (DownloadManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DownloadManager));
        var eventBus = new EventBusService();
        var artworkCache = (ArtworkCacheService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ArtworkCacheService));
        var libraryService = Mock.Of<ILibraryService>();
        var databaseService = (DatabaseService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DatabaseService));
        var config = new AppConfig();

        using var sut = new UnifiedTrackViewModel(
            model,
            downloadManager,
            eventBus,
            artworkCache,
            libraryService,
            databaseService,
            config);

        Assert.True(sut.IsClearedFromDownloadCenter);
    }

    [Fact]
    public void EntityToPlaylistTrack_MapsSoftClearFlag()
    {
        var sut = (LibraryService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryService));
        var entity = new PlaylistTrackEntity
        {
            Id = Guid.NewGuid(),
            PlaylistId = Guid.NewGuid(),
            Artist = "Artist",
            Title = "Title",
            TrackUniqueHash = "hash",
            Status = TrackStatus.Downloaded,
            IsClearedFromDownloadCenter = true
        };

        var result = (PlaylistTrack)InvokePrivate(sut, "EntityToPlaylistTrack", entity);

        Assert.True(result.IsClearedFromDownloadCenter);
    }

    [Fact]
    public void PlaylistTrackToEntity_MapsSoftClearFlag()
    {
        var sut = (LibraryService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryService));
        var model = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            PlaylistId = Guid.NewGuid(),
            Artist = "Artist",
            Title = "Title",
            TrackUniqueHash = "hash",
            Status = TrackStatus.Downloaded,
            IsClearedFromDownloadCenter = true
        };

        var result = (PlaylistTrackEntity)InvokePrivate(sut, "PlaylistTrackToEntity", model);

        Assert.True(result.IsClearedFromDownloadCenter);
    }

    private static object InvokePrivate(object instance, string methodName, object arg)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method not found: {methodName}");

        return method.Invoke(instance, new[] { arg })
            ?? throw new InvalidOperationException($"Method returned null: {methodName}");
    }
}