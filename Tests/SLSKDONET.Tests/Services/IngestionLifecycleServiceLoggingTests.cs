using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.AutoDownload;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Repositories;
using Xunit;

namespace SLSKDONET.Tests.Services;

public class IngestionLifecycleServiceLoggingTests
{
    [Fact]
    public async Task DownloadManager_LogIngestionLifecycleAsync_PersistsExpectedActivityLog()
    {
        EnsureDatabaseCreated();

        var playlistId = Guid.NewGuid();
        await EnsurePlaylistExistsAsync(playlistId);
        const string action = "ingestion_queued";
        var details = new
        {
            trackHash = "download-track-hash",
            source = "DownloadManagerTest"
        };

        var service = CreateDownloadManager(CreateDatabaseService());

        await InvokePrivateAsync(service, "LogIngestionLifecycleAsync", playlistId, action, details);

        await using var context = new AppDbContext();
        var row = await context.ActivityLogs
            .Where(log => log.PlaylistId == playlistId && log.Action == action)
            .OrderByDescending(log => log.Timestamp)
            .FirstOrDefaultAsync();

        Assert.NotNull(row);
        using var payload = JsonDocument.Parse(row!.Details);
        Assert.Equal("download-track-hash", payload.RootElement.GetProperty("trackHash").GetString());
        Assert.Equal("DownloadManagerTest", payload.RootElement.GetProperty("source").GetString());

        context.ActivityLogs.Remove(row);
        await context.SaveChangesAsync();
        await RemovePlaylistIfExistsAsync(playlistId);
    }

    [Fact]
    public async Task LibraryService_LogIngestionLifecycleAsync_PersistsExpectedActivityLog()
    {
        EnsureDatabaseCreated();

        var playlistId = Guid.NewGuid();
        await EnsurePlaylistExistsAsync(playlistId);
        const string action = "ingestion_started";
        var details = new
        {
            trackHash = "library-track-hash",
            source = "LibraryServiceTest"
        };

        var service = CreateLibraryService(CreateDatabaseService());

        await InvokePrivateAsync(service, "LogIngestionLifecycleAsync", playlistId, action, details);

        await using var context = new AppDbContext();
        var row = await context.ActivityLogs
            .Where(log => log.PlaylistId == playlistId && log.Action == action)
            .OrderByDescending(log => log.Timestamp)
            .FirstOrDefaultAsync();

        Assert.NotNull(row);
        using var payload = JsonDocument.Parse(row!.Details);
        Assert.Equal("library-track-hash", payload.RootElement.GetProperty("trackHash").GetString());
        Assert.Equal("LibraryServiceTest", payload.RootElement.GetProperty("source").GetString());

        context.ActivityLogs.Remove(row);
        await context.SaveChangesAsync();
        await RemovePlaylistIfExistsAsync(playlistId);
    }

    private static void EnsureDatabaseCreated()
    {
        using var context = new AppDbContext();
        context.Database.EnsureCreated();
    }

    private static async Task EnsurePlaylistExistsAsync(Guid playlistId)
    {
        await using var context = new AppDbContext();
        var exists = await context.Projects.AnyAsync(project => project.Id == playlistId);
        if (exists)
        {
            return;
        }

        context.Projects.Add(new PlaylistJobEntity
        {
            Id = playlistId,
            SourceTitle = "Ingestion Lifecycle Test",
            SourceType = "UnitTest",
            DestinationFolder = "Tests",
            CreatedAt = DateTime.UtcNow,
            DateUpdated = DateTime.UtcNow,
        });

        await context.SaveChangesAsync();
    }

    private static async Task RemovePlaylistIfExistsAsync(Guid playlistId)
    {
        await using var context = new AppDbContext();
        var playlist = await context.Projects.FirstOrDefaultAsync(project => project.Id == playlistId);
        if (playlist is null)
        {
            return;
        }

        context.Projects.Remove(playlist);
        await context.SaveChangesAsync();
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

    private static LibraryService CreateLibraryService(DatabaseService databaseService)
    {
        var cache = new LibraryCacheService();
        var config = new AppConfig();
        var eventBus = new EventBusService();

        return new LibraryService(
            NullLogger<LibraryService>.Instance,
            databaseService,
            config,
            eventBus,
            cache);
    }

    private static DownloadManager CreateDownloadManager(DatabaseService databaseService)
    {
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

    private static T CreateUninitialized<T>() where T : class
        => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static async Task InvokePrivateAsync(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method not found: {methodName}");

        var task = method.Invoke(target, args) as Task
            ?? throw new InvalidOperationException($"Method did not return Task: {methodName}");
        await task.ConfigureAwait(false);
    }
}
