using System;
using System.Collections.ObjectModel;
using System.Linq;
using DynamicData;
using DynamicData.Binding;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels.Downloads;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class DownloadGroupViewModelTests
{
    [Fact]
    public void DownloadGroupViewModel_UsesSourcePlaylistMetadataWhenAvailable()
    {
        var track = CreateTrack(
            globalId: "source-track-hash",
            playlistId: Guid.NewGuid(),
            sourcePlaylistId: Guid.NewGuid(),
            sourcePlaylistName: "Northbound Set",
            artist: "DJ Atlas",
            title: "Intro",
            albumArtUrl: "https://example.com/northbound.jpg");

        using var group = CreateGroupedViewModel(track);

        Assert.Equal("Northbound Set", group.Title);
        Assert.Equal("By DJ Atlas", group.Subtitle);
        Assert.Equal("https://example.com/northbound.jpg", group.ArtworkUrl);
    }

    [Fact]
    public void DownloadGroupViewModel_FallsBackToProjectSelectionWhenSourcePlaylistMissing()
    {
        var track = CreateTrack(
            globalId: "project-track-hash",
            playlistId: Guid.NewGuid(),
            artist: "Project Artist",
            title: "Project Cut",
            albumArtUrl: "https://example.com/project.jpg");

        using var group = CreateGroupedViewModel(track);

        Assert.Equal("Project Selection", group.Title);
        Assert.Equal("Project Artist", group.Subtitle);
        Assert.Equal("https://example.com/project.jpg", group.ArtworkUrl);
    }

    private static DownloadGroupViewModel CreateGroupedViewModel(PlaylistTrack model)
    {
        var source = new SourceCache<UnifiedTrackViewModel, string>(track => track.GlobalId);
        ReadOnlyObservableCollection<DownloadGroupViewModel>? groups = null;

        var downloadManager = CreateUninitializedDownloadManager();
        var libraryService = Mock.Of<ILibraryService>();

        using var changes = source.Connect()
            .Group(track => track.Model.SourcePlaylistId ?? track.Model.PlaylistId)
            .Transform(group => new DownloadGroupViewModel(group, downloadManager, libraryService))
            .Bind(out groups)
            .Subscribe();

        source.Edit(updater => updater.AddOrUpdate(CreateTrackViewModel(model)));

        return groups?.Single() ?? throw new InvalidOperationException("Download group was not created.");
    }

    private static UnifiedTrackViewModel CreateTrackViewModel(PlaylistTrack model)
    {
        var downloadManager = CreateUninitializedDownloadManager();
        var eventBus = new EventBusService();
        var artworkCache = (ArtworkCacheService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ArtworkCacheService));
        var libraryService = Mock.Of<ILibraryService>();
        var databaseService = (DatabaseService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DatabaseService));
        var config = new AppConfig();

        return new UnifiedTrackViewModel(
            model,
            downloadManager,
            eventBus,
            artworkCache,
            libraryService,
            databaseService,
            config);
    }

    private static PlaylistTrack CreateTrack(
        string globalId,
        Guid playlistId,
        string artist = "Various Artists",
        string title = "Untitled",
        Guid? sourcePlaylistId = null,
        string? sourcePlaylistName = null,
        string? albumArtUrl = null)
    {
        return new PlaylistTrack
        {
            Id = Guid.NewGuid(),
            PlaylistId = playlistId,
            TrackUniqueHash = globalId,
            Artist = artist,
            Title = title,
            AlbumArtUrl = albumArtUrl,
            SourcePlaylistId = sourcePlaylistId,
            SourcePlaylistName = sourcePlaylistName,
            Status = TrackStatus.Pending
        };
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
        var field = instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field not found: {name}");
        field.SetValue(instance, value);
    }
}