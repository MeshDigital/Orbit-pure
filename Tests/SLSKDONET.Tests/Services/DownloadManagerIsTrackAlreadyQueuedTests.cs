using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AutoDownload;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Models;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// GhostAcquisitionOrchestrator's background sweep is the only caller of IsTrackAlreadyQueued.
/// An OnHold track hydrates to PlaylistTrackState.Paused at startup (EnableMp3Fallback=true, the
/// default) — before the fix under test, that Paused state made IsTrackAlreadyQueued return true
/// forever, so the sweep never even attempted discovery for it. These tests pin the corrected
/// contract directly, with no DB/network dependency.
/// </summary>
public class DownloadManagerIsTrackAlreadyQueuedTests
{
    [Fact]
    public void IsTrackAlreadyQueued_HydratedOnHoldTrack_NotUserPaused_ReturnsFalse()
    {
        var manager = CreateDownloadManager();
        var track = CreateTrack(spotifyTrackId: "spotify-1", artist: "Artist", title: "Title", isUserPaused: false);
        SeedDownloads(manager, new DownloadContext(track) { State = PlaylistTrackState.Paused });

        var result = manager.IsTrackAlreadyQueued("spotify-1", "Artist", "Title");

        Assert.False(result);
    }

    [Fact]
    public void IsTrackAlreadyQueued_GenuineUserPause_ReturnsTrue()
    {
        var manager = CreateDownloadManager();
        var track = CreateTrack(spotifyTrackId: "spotify-2", artist: "Artist", title: "Title", isUserPaused: true);
        SeedDownloads(manager, new DownloadContext(track) { State = PlaylistTrackState.Paused });

        var result = manager.IsTrackAlreadyQueued("spotify-2", "Artist", "Title");

        Assert.True(result);
    }

    [Fact]
    public void IsTrackAlreadyQueued_ActivelyDownloading_StillReturnsTrue()
    {
        // Regression guard: the Paused-and-not-user-paused carve-out must not accidentally
        // widen to any other in-flight state.
        var manager = CreateDownloadManager();
        var track = CreateTrack(spotifyTrackId: "spotify-3", artist: "Artist", title: "Title", isUserPaused: false);
        SeedDownloads(manager, new DownloadContext(track) { State = PlaylistTrackState.Downloading });

        var result = manager.IsTrackAlreadyQueued("spotify-3", "Artist", "Title");

        Assert.True(result);
    }

    [Fact]
    public void IsTrackAlreadyQueued_MatchesByArtistAndTitle_WhenNoSpotifyId()
    {
        var manager = CreateDownloadManager();
        var track = CreateTrack(spotifyTrackId: null, artist: "Some Artist", title: "Some Title", isUserPaused: true);
        SeedDownloads(manager, new DownloadContext(track) { State = PlaylistTrackState.Paused });

        Assert.True(manager.IsTrackAlreadyQueued(null, "Some Artist", "Some Title"));
        Assert.False(manager.IsTrackAlreadyQueued(null, "Different Artist", "Different Title"));
    }

    private static PlaylistTrack CreateTrack(string? spotifyTrackId, string artist, string title, bool isUserPaused)
    {
        return new PlaylistTrack
        {
            Id = System.Guid.NewGuid(),
            Artist = artist,
            Title = title,
            SpotifyTrackId = spotifyTrackId,
            TrackUniqueHash = System.Guid.NewGuid().ToString("N"),
            IsUserPaused = isUserPaused,
        };
    }

    private static void SeedDownloads(DownloadManager manager, params DownloadContext[] contexts)
    {
        var field = typeof(DownloadManager).GetField("_downloads", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new System.InvalidOperationException("_downloads field not found");
        var list = (List<DownloadContext>)field.GetValue(manager)!;
        list.AddRange(contexts);
    }

    private static DownloadManager CreateDownloadManager()
    {
        var databaseService = CreateDatabaseService();
        var config = new AppConfig();
        var configManager = new ConfigManager();
        var formatter = new FileNameFormatter();
        var eventBus = new EventBusService();
        var fileWrite = new Mock<IFileWriteService>();
        var library = new Mock<ILibraryService>();
        var soulseek = new Mock<ISoulseekAdapter>();
        var networkHealth = new Mock<INetworkHealthService>();
        var prefetchVerifier = new PrefetchVerifier(
            NullLogger<PrefetchVerifier>.Instance,
            config,
            databaseService);

        var pathProvider = new PathProviderService(config, formatter, NullLogger<PathProviderService>.Instance);
        var crashJournal = new CrashRecoveryJournal(NullLogger<CrashRecoveryJournal>.Instance);
        var peerReliability = new PeerReliabilityService(databaseService);

        return new DownloadManager(
            NullLogger<DownloadManager>.Instance,
            config,
            configManager,
            soulseek.Object,
            formatter,
            databaseService,
            library.Object,
            eventBus,
            CreateUninitialized<DownloadDiscoveryService>(),
            CreateUninitialized<AutoSearchService>(),
            pathProvider,
            fileWrite.Object,
            prefetchVerifier,
            crashJournal,
            peerReliability,
            networkHealth.Object,
            new Mock<SLSKDONET.Services.Diagnostics.ITrackAuditLogger>().Object);
    }

    private static DatabaseService CreateDatabaseService()
    {
        var schemaMigrator = new SchemaMigratorService(NullLogger<SchemaMigratorService>.Instance);
        var trackRepository = new TrackRepository(NullLogger<TrackRepository>.Instance);
        var fileWrite = new Mock<IFileWriteService>();

        return new DatabaseService(
            NullLogger<DatabaseService>.Instance,
            schemaMigrator,
            trackRepository,
            fileWrite.Object);
    }

    private static T CreateUninitialized<T>() where T : class
        => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
