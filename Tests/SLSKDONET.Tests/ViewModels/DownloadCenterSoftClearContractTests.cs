using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DynamicData;
using Moq;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Models;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Downloads;
using SLSKDONET.ViewModels.Library;
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

        var downloadManager = CreateUninitializedDownloadManager();
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

    [Fact]
    public async Task CancelPlaylist_RemovesFromDownloadCenter_PreservesInLibraryDatabase()
    {
        // Arrange
        var model = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            TrackUniqueHash = "cancel-hash",
            Status = TrackStatus.Missing,
            IsClearedFromDownloadCenter = false
        };

        var downloadManager = CreateUninitializedDownloadManager();
        typeof(DownloadManager).GetField("_downloads", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(downloadManager, new List<DownloadContext>());
        typeof(DownloadManager).GetField("_collectionLock", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(downloadManager, new object());

        var libraryServiceMock = new Mock<ILibraryService>();
        libraryServiceMock.Setup(x => x.UpdatePlaylistTrackAsync(It.IsAny<PlaylistTrack>()))
            .Returns(Task.CompletedTask);

        var trackVm = new UnifiedTrackViewModel(
            model,
            downloadManager,
            new EventBusService(),
            (ArtworkCacheService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ArtworkCacheService)),
            libraryServiceMock.Object,
            (DatabaseService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DatabaseService)),
            new AppConfig());

        var source = new SourceCache<UnifiedTrackViewModel, string>(track => track.GlobalId);
        ReadOnlyObservableCollection<DownloadGroupViewModel>? groups = null;
        using var changes = source.Connect()
            .Group(track => track.Model.PlaylistId)
            .Transform(g => new DownloadGroupViewModel(g, downloadManager, libraryServiceMock.Object))
            .Bind(out groups)
            .Subscribe();

        source.AddOrUpdate(trackVm);
        var groupVm = groups.Single();

        // Act
        ((System.Windows.Input.ICommand)groupVm.CancelCommand).Execute(null);
        await Task.Delay(100);

        // Assert
        Assert.True(trackVm.IsClearedFromDownloadCenter);
        Assert.True(trackVm.Model.IsClearedFromDownloadCenter);
        libraryServiceMock.Verify(x => x.UpdatePlaylistTrackAsync(trackVm.Model), Times.Once);
    }

    [Fact]
    public void SoftClearedTracks_DoNotReappear_OnDownloadCenterRestart()
    {
        // Arrange
        var downloadManager = CreateUninitializedDownloadManager();
        typeof(DownloadManager).GetField("_downloads", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(downloadManager, new List<DownloadContext>());
        typeof(DownloadManager).GetField("_collectionLock", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(downloadManager, new object());

        var eventBus = new EventBusService();
        var artworkCache = (ArtworkCacheService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ArtworkCacheService));
        var libraryService = Mock.Of<ILibraryService>();
        var databaseService = (DatabaseService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DatabaseService));
        var dbFactory = Mock.Of<IDbContextFactory<AppDbContext>>();
        var dialogService = Mock.Of<IDialogService>();
        var config = new AppConfig();

        using var sut = new DownloadCenterViewModel(
            downloadManager,
            eventBus,
            config,
            artworkCache,
            libraryService,
            databaseService,
            dbFactory,
            dialogService);

        var clearedTrack = new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            TrackUniqueHash = "cleared-hash",
            Status = TrackStatus.Downloaded,
            IsClearedFromDownloadCenter = true
        };

        // Act
        eventBus.Publish(new TrackAddedEvent(clearedTrack, PlaylistTrackState.Completed));

        // Assert
        Assert.Empty(sut.ActiveDownloads);
        Assert.Empty(sut.CompletedDownloads);
        Assert.Empty(sut.FailedDownloads);
    }

    [Fact]
    public void DownloadMissingCommand_IgnoresDownloaded_AndIgnoresOnHoldTracks()
    {
        // Arrange
        var eventBus = new EventBusService();
        var downloadManager = CreateUninitializedDownloadManager();
        typeof(DownloadManager).GetField("_downloads", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(downloadManager, new List<DownloadContext>());
        typeof(DownloadManager).GetField("_collectionLock", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(downloadManager, new object());
        typeof(DownloadManager).GetField("_eventBus", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(downloadManager, eventBus);
        typeof(DownloadManager).GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(downloadManager, new AppConfig { EnableAutoAcquireOnImport = true });
        typeof(DownloadManager).GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(downloadManager, Microsoft.Extensions.Logging.Abstractions.NullLogger<DownloadManager>.Instance);

        var albumNode = new AlbumNode("Test Album", "Test Artist", downloadManager, null, eventBus);

        var downloadedTrack = new PlaylistTrack { Status = TrackStatus.Downloaded, Artist = "Artist", Title = "Downloaded", TrackUniqueHash = "h1" };
        var onHoldTrack = new PlaylistTrack { Status = TrackStatus.OnHold, Artist = "Artist", Title = "OnHold", TrackUniqueHash = "h2" };
        var missingTrack = new PlaylistTrack { Status = TrackStatus.Missing, Artist = "Artist", Title = "Missing", TrackUniqueHash = "h3" };

        var libraryService = Mock.Of<ILibraryService>();
        var databaseService = (DatabaseService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DatabaseService));

        albumNode.Tracks.Add(new PlaylistTrackViewModel(downloadedTrack, eventBus, libraryService));
        albumNode.Tracks.Add(new PlaylistTrackViewModel(onHoldTrack, eventBus, libraryService));
        albumNode.Tracks.Add(new PlaylistTrackViewModel(missingTrack, eventBus, libraryService));

        var queuedTracks = new List<PlaylistTrack>();
        eventBus.GetEvent<TrackAddedEvent>().Subscribe(e => queuedTracks.Add(e.TrackModel));

        // Act
        albumNode.DownloadMissingCommand.Execute(null);

        // Assert
        Assert.Single(queuedTracks);
        Assert.Equal("Missing", queuedTracks.Single().Title);
    }

    private static object InvokePrivate(object instance, string methodName, object arg)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method not found: {methodName}");

        return method.Invoke(instance, new[] { arg })
            ?? throw new InvalidOperationException($"Method returned null: {methodName}");
    }

    /// <summary>
    /// GetUninitializedObject bypasses field initializers, so DownloadManager's readonly
    /// ConcurrentDictionary fields (used by GetJobPriority/GetJobFocused, which
    /// DownloadGroupViewModel's constructor calls) are null rather than empty — initialize them
    /// so tests that build a DownloadGroupViewModel don't NRE on an uninitialized dictionary.
    /// </summary>
    private static DownloadManager CreateUninitializedDownloadManager()
    {
        var instance = (DownloadManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DownloadManager));
        SetField(instance, "_jobEffectivePriority", new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>());
        SetField(instance, "_jobBasePriority", new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>());
        SetField(instance, "_jobFocused", new System.Collections.Concurrent.ConcurrentDictionary<Guid, bool>());
        return instance;
    }

    private static void SetField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }
}