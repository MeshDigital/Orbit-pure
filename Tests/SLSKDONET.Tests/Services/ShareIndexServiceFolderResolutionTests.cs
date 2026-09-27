using System;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services;

/// <summary>
/// Sharing previously only ever read AppConfig.SharedFolderPath/DownloadDirectory — completely
/// disconnected from Library Sources (the multi-folder library the user actually imports into),
/// so a user whose library lives entirely in Library Sources folders shared nothing. These tests
/// pin the fixed behavior: enabled Library Sources are the primary share set, with the legacy
/// single folder and the download folder as additive extras.
/// </summary>
public sealed class ShareIndexServiceFolderResolutionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_ShareIndexTest_{Guid.NewGuid():N}");

    public ShareIndexServiceFolderResolutionTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CreateFolder(string name)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static IDbContextFactory<AppDbContext> CreateInMemoryFactory()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ShareIndexTest_{Guid.NewGuid():N}")
            .Options;
        return new TestDbContextFactory(options);
    }

    [Fact]
    public void ResolveShareFolders_IncludesEnabledLibrarySources()
    {
        var libraryFolder = CreateFolder("Library1");
        var dbFactory = CreateInMemoryFactory();
        using (var context = dbFactory.CreateDbContext())
        {
            context.LibraryFolders.Add(new LibraryFolderEntity { FolderPath = libraryFolder, IsEnabled = true });
            context.SaveChanges();
        }

        var service = new ShareIndexService(new AppConfig(), dbFactory, NullLogger<ShareIndexService>.Instance);

        var folders = service.ResolveShareFolders();

        Assert.Contains(libraryFolder, folders, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveShareFolders_ExcludesDisabledLibrarySources()
    {
        var enabledFolder = CreateFolder("EnabledLib");
        var disabledFolder = CreateFolder("DisabledLib");
        var dbFactory = CreateInMemoryFactory();
        using (var context = dbFactory.CreateDbContext())
        {
            context.LibraryFolders.Add(new LibraryFolderEntity { FolderPath = enabledFolder, IsEnabled = true });
            context.LibraryFolders.Add(new LibraryFolderEntity { FolderPath = disabledFolder, IsEnabled = false });
            context.SaveChanges();
        }

        var service = new ShareIndexService(new AppConfig(), dbFactory, NullLogger<ShareIndexService>.Instance);

        var folders = service.ResolveShareFolders();

        Assert.Contains(enabledFolder, folders, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(disabledFolder, folders, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveShareFolders_IncludesLegacySharedFolderAndDownloadDirectory_AsAdditiveExtras()
    {
        var libraryFolder = CreateFolder("Library1");
        var legacySharedFolder = CreateFolder("LegacyShared");
        var downloadFolder = CreateFolder("Downloads");
        var dbFactory = CreateInMemoryFactory();
        using (var context = dbFactory.CreateDbContext())
        {
            context.LibraryFolders.Add(new LibraryFolderEntity { FolderPath = libraryFolder, IsEnabled = true });
            context.SaveChanges();
        }

        var config = new AppConfig { SharedFolderPath = legacySharedFolder, DownloadDirectory = downloadFolder };
        var service = new ShareIndexService(config, dbFactory, NullLogger<ShareIndexService>.Instance);

        var folders = service.ResolveShareFolders();

        Assert.Contains(libraryFolder, folders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(legacySharedFolder, folders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(downloadFolder, folders, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveShareFolders_SkipsNonexistentLibrarySourceFolder_WithoutThrowing()
    {
        var missingFolder = Path.Combine(_tempDir, "DoesNotExist");
        var dbFactory = CreateInMemoryFactory();
        using (var context = dbFactory.CreateDbContext())
        {
            context.LibraryFolders.Add(new LibraryFolderEntity { FolderPath = missingFolder, IsEnabled = true });
            context.SaveChanges();
        }

        var service = new ShareIndexService(new AppConfig(), dbFactory, NullLogger<ShareIndexService>.Instance);

        var folders = service.ResolveShareFolders();

        Assert.DoesNotContain(missingFolder, folders, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveShareFolders_NoLibrarySourcesConfigured_FallsBackToLegacyFoldersOnly()
    {
        var legacySharedFolder = CreateFolder("LegacyOnly");
        var dbFactory = CreateInMemoryFactory();

        var config = new AppConfig { SharedFolderPath = legacySharedFolder };
        var service = new ShareIndexService(config, dbFactory, NullLogger<ShareIndexService>.Instance);

        var folders = service.ResolveShareFolders();

        Assert.Single(folders);
        Assert.Equal(legacySharedFolder, folders[0], ignoreCase: true);
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
