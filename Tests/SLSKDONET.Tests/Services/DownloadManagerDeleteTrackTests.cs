using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AutoDownload;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Regression coverage for a real, live-reported bug: right-click "Remove from Library and Disk"
/// silently did nothing for a normal library track. Root cause was
/// DeleteTrackFromDiskAndHistoryAsync bailing out immediately whenever the track wasn't present in
/// the in-memory _downloads collection (only actively-downloading/recently-hydrated tracks are —
/// most already-imported library tracks never are), so the file was never deleted, the library
/// index row was never removed, and TrackRemovedEvent (which drives the Library page's live
/// refresh) never fired. These tests pin the corrected contract directly, with no UI dependency.
/// </summary>
public class DownloadManagerDeleteTrackTests
{
    [Fact]
    public async Task DeleteTrack_NotInActiveDownloads_StillDeletesFileAndNotifies()
    {
        // The track is NOT seeded into _downloads — this is the common case the old code
        // silently no-op'd on.
        var globalId = Guid.NewGuid().ToString("N");
        var tempFile = Path.Combine(Path.GetTempPath(), $"orbit-delete-test-{globalId}.flac");
        await File.WriteAllTextAsync(tempFile, "not a real audio file, just needs to exist");

        var library = new Mock<ILibraryService>();
        library.Setup(l => l.FindLibraryEntryAsync(globalId))
            .ReturnsAsync(new LibraryEntry { UniqueHash = globalId, FilePath = tempFile });

        var eventBus = new EventBusService();
        var manager = CreateDownloadManager(library, eventBus);

        TrackRemovedEvent? received = null;
        using var sub = eventBus.GetEvent<TrackRemovedEvent>().Subscribe(e => received = e);

        try
        {
            await manager.DeleteTrackFromDiskAndHistoryAsync(globalId);

            Assert.False(File.Exists(tempFile), "the physical file should have been deleted");
            library.Verify(l => l.RemoveTrackFromLibraryAsync(globalId), Times.Once,
                "the library index row (LibraryEntries) must be removed or the track keeps showing up in the Library view");
            library.Verify(l => l.RemoveTrackFromAllPlaylistsAsync(globalId), Times.Once,
                "a permanent delete must actually remove the playlist row(s), not mark them Missing " +
                "(Missing means 'queue for search' — it both keeps the track visible in the playlist " +
                "and triggers GhostAcquisitionOrchestrator to re-download the very file just deleted)");
            Assert.NotNull(received);
            Assert.Equal(globalId, received!.TrackGlobalId);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DeleteTrack_NoLibraryEntryEither_DoesNotThrow_AndStillPublishesRemoval()
    {
        // Edge case: neither an active DownloadContext nor a library entry exist for this hash
        // (e.g. already partially cleaned up). Must degrade gracefully, not throw, and still
        // finish the DB/event cleanup steps.
        var globalId = Guid.NewGuid().ToString("N");
        var library = new Mock<ILibraryService>();
        library.Setup(l => l.FindLibraryEntryAsync(globalId)).ReturnsAsync((LibraryEntry?)null);

        var eventBus = new EventBusService();
        var manager = CreateDownloadManager(library, eventBus);

        TrackRemovedEvent? received = null;
        using var sub = eventBus.GetEvent<TrackRemovedEvent>().Subscribe(e => received = e);

        var ex = await Record.ExceptionAsync(() => manager.DeleteTrackFromDiskAndHistoryAsync(globalId));

        Assert.Null(ex);
        library.Verify(l => l.RemoveTrackFromLibraryAsync(globalId), Times.Once);
        Assert.NotNull(received);
    }

    private static DownloadManager CreateDownloadManager(Mock<ILibraryService> library, EventBusService eventBus)
    {
        var databaseService = CreateDatabaseService();
        var config = new AppConfig();
        var configManager = new ConfigManager();
        var formatter = new FileNameFormatter();
        var fileWrite = new Mock<IFileWriteService>();
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
        => (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
}
