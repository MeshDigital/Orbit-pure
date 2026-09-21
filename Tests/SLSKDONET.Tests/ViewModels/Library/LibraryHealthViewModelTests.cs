using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.ViewModels.Library;
using Xunit;

namespace SLSKDONET.Tests.ViewModels.Library;

/// <summary>
/// Exercises LibraryHealthViewModel's three report sections against a real (in-memory) AppDbContext
/// and real temp files — this backs a previously-nonexistent report (disk usage, unplayed tracks,
/// untracked-on-disk files), so there's no prior behavior to preserve, just correctness to pin.
/// </summary>
public class LibraryHealthViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ORBIT_HealthTest_{Guid.NewGuid():N}");

    public LibraryHealthViewModelTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static IDbContextFactory<AppDbContext> CreateFactory(out AppDbContext seedContext)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HealthTest_{Guid.NewGuid():N}")
            .Options;
        var factory = new TestDbContextFactory(options);
        seedContext = factory.CreateDbContext();
        return factory;
    }

    private LibraryHealthViewModel CreateViewModel(IDbContextFactory<AppDbContext> factory) =>
        new(NullLogger<LibraryHealthViewModel>.Instance, factory, null!, new NullServiceProvider());

    private string CreateFile(string name, int sizeBytes)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    [Theory]
    [InlineData(0, "0.0 B")]
    [InlineData(512, "512.0 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1024 * 1024, "1.0 MB")]
    [InlineData(1024L * 1024 * 1024, "1.0 GB")]
    public void FormatBytes_RendersHumanReadableSize(long bytes, string expected)
    {
        Assert.Equal(expected, LibraryHealthViewModel.FormatBytes(bytes));
    }

    [Fact]
    public async Task RefreshDiskUsage_ComputesTotalAndPerFormatBreakdown()
    {
        var mp3Path = CreateFile("a.mp3", 1000);
        var flacPath = CreateFile("b.flac", 3000);
        var factory = CreateFactory(out var seed);

        seed.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = "h1", Artist = "A", Title = "T1", FilePath = mp3Path, Format = "mp3" });
        seed.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = "h2", Artist = "A", Title = "T2", FilePath = flacPath, Format = "flac" });
        await seed.SaveChangesAsync();

        var vm = CreateViewModel(factory);
        await InvokeRefreshDiskUsage(vm);

        Assert.Equal(4000, vm.TotalLibraryBytes);
        Assert.Equal(2, vm.FormatBreakdown.Count);
        Assert.Contains(vm.FormatBreakdown, r => r.Format == "MP3" && r.TotalBytes == 1000);
        Assert.Contains(vm.FormatBreakdown, r => r.Format == "FLAC" && r.TotalBytes == 3000);
        Assert.Equal("T2", vm.LargestFiles.First().Title); // 3000 bytes > 1000 bytes
    }

    [Fact]
    public async Task RefreshDiskUsage_SkipsEntriesWhoseFileNoLongerExists()
    {
        var realPath = CreateFile("real.mp3", 500);
        var factory = CreateFactory(out var seed);

        seed.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = "h1", Artist = "A", Title = "Real", FilePath = realPath, Format = "mp3" });
        seed.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = "h2", Artist = "A", Title = "Missing", FilePath = Path.Combine(_tempDir, "gone.mp3"), Format = "mp3" });
        await seed.SaveChangesAsync();

        var vm = CreateViewModel(factory);
        await InvokeRefreshDiskUsage(vm);

        Assert.Equal(500, vm.TotalLibraryBytes);
        Assert.Single(vm.LargestFiles);
    }

    [Fact]
    public async Task RefreshUnplayed_IncludesNeverPlayedAndStaleTracks_ExcludesRecentlyPlayed()
    {
        var factory = CreateFactory(out var seed);
        seed.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = "never", Artist = "A", Title = "Never Played", FilePath = "x", PlayCount = 0, LastPlayedAt = null });
        seed.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = "stale", Artist = "A", Title = "Stale", FilePath = "x", PlayCount = 5, LastPlayedAt = DateTime.UtcNow.AddDays(-200) });
        seed.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = "recent", Artist = "A", Title = "Recent", FilePath = "x", PlayCount = 5, LastPlayedAt = DateTime.UtcNow.AddDays(-1) });
        await seed.SaveChangesAsync();

        var vm = CreateViewModel(factory);
        vm.UnplayedThresholdDays = 90;
        await InvokeRefreshUnplayed(vm);

        var titles = vm.UnplayedTracks.Select(t => t.Title).ToList();
        Assert.Contains("Never Played", titles);
        Assert.Contains("Stale", titles);
        Assert.DoesNotContain("Recent", titles);
    }

    [Fact]
    public async Task RefreshUntracked_FindsFilesOnDiskNotInLibrary()
    {
        var trackedPath = CreateFile("tracked.mp3", 100);
        var untrackedPath = CreateFile("untracked.mp3", 100);
        var factory = CreateFactory(out var seed);

        var folderId = Guid.NewGuid();
        seed.LibraryFolders.Add(new LibraryFolderEntity { Id = folderId, FolderPath = _tempDir, IsEnabled = true });
        seed.LibraryEntries.Add(new LibraryEntryEntity { UniqueHash = "h1", Artist = "A", Title = "Tracked", FilePath = trackedPath });
        await seed.SaveChangesAsync();

        var vm = CreateViewModel(factory);
        await InvokeRefreshUntracked(vm);

        Assert.Single(vm.UntrackedFiles);
        Assert.Equal(untrackedPath, vm.UntrackedFiles.Single().FilePath);
    }

    [Fact]
    public async Task RefreshUntracked_IgnoresDisabledFolders()
    {
        CreateFile("shouldnotappear.mp3", 100);
        var factory = CreateFactory(out var seed);
        seed.LibraryFolders.Add(new LibraryFolderEntity { Id = Guid.NewGuid(), FolderPath = _tempDir, IsEnabled = false });
        await seed.SaveChangesAsync();

        var vm = CreateViewModel(factory);
        await InvokeRefreshUntracked(vm);

        Assert.Empty(vm.UntrackedFiles);
    }

    // AsyncRelayCommand exposes a real ExecuteAsync(T) alongside the fire-and-forget
    // ICommand.Execute(object) — use that to await completion deterministically instead of
    // polling a loading flag.
    private static Task InvokeRefreshDiskUsage(LibraryHealthViewModel vm) =>
        ((SLSKDONET.Views.AsyncRelayCommand<object>)vm.RefreshDiskUsageCommand).ExecuteAsync(null);

    private static Task InvokeRefreshUnplayed(LibraryHealthViewModel vm) =>
        ((SLSKDONET.Views.AsyncRelayCommand<object>)vm.RefreshUnplayedCommand).ExecuteAsync(null);

    private static Task InvokeRefreshUntracked(LibraryHealthViewModel vm) =>
        ((SLSKDONET.Views.AsyncRelayCommand<object>)vm.RefreshUntrackedCommand).ExecuteAsync(null);

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
