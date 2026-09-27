using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
/// Regression coverage for the "sync spams a download-complete notification for tracks that were
/// already downloaded" bug: DownloadManager.ProcessTrackAsync has three "file already exists,
/// just reuse it" fast paths that used to publish the exact same TrackStateChangedEvent(Completed)
/// as a genuine fresh transfer, so NotificationCenterService (and the post-download
/// spectral-scan/duration-probe services) couldn't tell them apart from a real download finishing
/// — every re-sync that re-queued an already-downloaded track (common: a duplicate row, or one
/// reset from Failed/OnHold) fired a spurious success toast and redid analysis work on an
/// unchanged file. These tests invoke the private ProcessTrackAsync directly (via reflection, same
/// convention as DownloadManagerIsTrackAlreadyQueuedTests) and assert the published event's new
/// WasAlreadyPresent flag distinguishes the two cases.
/// </summary>
public class DownloadManagerWasAlreadyPresentTests
{
    [Fact]
    public async Task ProcessTrackAsync_FileAlreadyExistsAtResolvedPath_PublishesCompletedWithWasAlreadyPresentTrue()
    {
        var eventBus = new EventBusService();
        var manager = CreateDownloadManager(eventBus);
        var tempFile = Path.GetTempFileName();
        try
        {
            var track = new PlaylistTrack
            {
                Id = Guid.NewGuid(),
                Artist = "Artist",
                Title = "Title",
                TrackUniqueHash = Guid.NewGuid().ToString("N"),
                ResolvedFilePath = tempFile,
                Status = TrackStatus.Missing,
            };
            var ctx = new DownloadContext(track);

            TrackStateChangedEvent? captured = null;
            using var sub = eventBus.GetEvent<TrackStateChangedEvent>()
                .Subscribe(e => { if (e.State == PlaylistTrackState.Completed) captured = e; });

            await InvokeProcessTrackAsync(manager, ctx, CancellationToken.None);

            Assert.NotNull(captured);
            Assert.True(captured!.WasAlreadyPresent);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ProcessTrackAsync_AlreadyDownloadedStatusWithExistingFile_PublishesCompletedWithWasAlreadyPresentTrue()
    {
        var eventBus = new EventBusService();
        var manager = CreateDownloadManager(eventBus);
        var tempFile = Path.GetTempFileName();
        try
        {
            var track = new PlaylistTrack
            {
                Id = Guid.NewGuid(),
                Artist = "Artist",
                Title = "Title",
                TrackUniqueHash = Guid.NewGuid().ToString("N"),
                ResolvedFilePath = tempFile,
                Status = TrackStatus.Downloaded,
            };
            var ctx = new DownloadContext(track) { State = PlaylistTrackState.Searching };

            TrackStateChangedEvent? captured = null;
            using var sub = eventBus.GetEvent<TrackStateChangedEvent>()
                .Subscribe(e => { if (e.State == PlaylistTrackState.Completed) captured = e; });

            await InvokeProcessTrackAsync(manager, ctx, CancellationToken.None);

            Assert.NotNull(captured);
            Assert.True(captured!.WasAlreadyPresent);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static async Task InvokeProcessTrackAsync(DownloadManager manager, DownloadContext ctx, CancellationToken ct)
    {
        var method = typeof(DownloadManager).GetMethod("ProcessTrackAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ProcessTrackAsync not found");
        var task = (Task)method.Invoke(manager, new object[] { ctx, ct })!;
        await task;
    }

    private static DownloadManager CreateDownloadManager(EventBusService eventBus)
    {
        var databaseService = CreateDatabaseService();
        var config = new AppConfig();
        var configManager = new ConfigManager();
        var formatter = new FileNameFormatter();
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
