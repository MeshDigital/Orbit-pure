using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Library.Maintenance;
using SLSKDONET.ViewModels.Maintenance;
using Xunit;

namespace SLSKDONET.Tests.Maintenance;

public sealed class ExactDuplicatesViewModelTests : IDisposable
{
    private readonly EventBusService _bus = new();
    private readonly Factory _factory = new();
    private readonly RecordingRecycler _recycler = new();
    private readonly string _dir;

    public ExactDuplicatesViewModelTests()
    {
        var isolatedRoot = Path.GetDirectoryName(OrbitPaths.LibraryDbPath)!;
        Assert.Contains("orbit-tests", isolatedRoot);
        _dir = Path.Combine(isolatedRoot, "exact-dup-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _bus.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* intentional: temp dir under the isolated test root */ }
    }

    [Fact]
    public void Shell_RaisesOpenStateChanges_SoTheOverlayActuallyShows()
    {
        // Regression: the shell was a plain class, so MainWindow's IsOpen binding never updated.
        using var shell = new MaintenanceShellViewModel(
            () => throw new InvalidOperationException("wizard not used here"),
            NullLogger<MaintenanceShellViewModel>.Instance,
            _bus, a => a(), duplicatesFactory: NewViewModel);
        var raised = new List<string?>();
        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        shell.OpenDuplicates();
        Assert.True(shell.IsDuplicatesOpen);
        Assert.Contains(nameof(MaintenanceShellViewModel.IsDuplicatesOpen), raised);
        Assert.Contains(nameof(MaintenanceShellViewModel.IsAnyOpen), raised);

        raised.Clear();
        shell.Duplicates!.CloseCommand.Execute(null);
        Assert.False(shell.IsAnyOpen);
        Assert.Contains(nameof(MaintenanceShellViewModel.IsAnyOpen), raised);
    }

    [Fact]
    public async Task Merge_WithRecycleOptIn_AsksFirst_AndOnlyMovesFilesAfterConfirming()
    {
        var (keeper, duplicate, duplicatePath) = await SeedPairAsync();
        using var vm = NewViewModel();
        await vm.LoadAsync();
        var group = Assert.Single(vm.Groups, g => g.Entries.Any(e => e.UniqueHash == duplicate));
        foreach (var other in vm.Groups.Where(g => !ReferenceEquals(g, group))) other.IsIncluded = false;
        group.Entries.Single(e => e.UniqueHash == keeper).KeepCommand.Execute(null);
        Assert.Equal(keeper, group.KeeperHash);

        vm.MoveFilesToRecycleBin = true;
        vm.MergeCommand.Execute(null);
        Assert.True(vm.IsConfirmingRecycle);
        Assert.Empty(_recycler.Calls);
        await using (var db = new AppDbContext())
            Assert.True(await db.LibraryEntries.AnyAsync(e => e.UniqueHash == duplicate), "nothing is merged before confirming");

        await vm.ExecuteMergeAsync(moveFilesToRecycleBin: true);

        Assert.Equal(new[] { duplicatePath }, _recycler.Calls);
        Assert.False(vm.ResultHasErrors);
        Assert.Contains("removed 1 entry", vm.ResultText);
        await using (var db = new AppDbContext())
            Assert.False(await db.LibraryEntries.AnyAsync(e => e.UniqueHash == duplicate));
    }

    private ExactDuplicatesViewModel NewViewModel()
    {
        var backfill = new LibraryContentHashBackfillService(_factory, new AudioContentHasher(NullLogger<AudioContentHasher>.Instance),
            NullLogger<LibraryContentHashBackfillService>.Instance, _bus);
        var resolver = new ExactDuplicateResolver(
            new DuplicateTrackCleanupService(_factory, _bus, NullLogger<DuplicateTrackCleanupService>.Instance),
            _factory, _recycler, NullLogger<ExactDuplicateResolver>.Instance);
        return new ExactDuplicatesViewModel(backfill, resolver, _bus, NullLogger<ExactDuplicatesViewModel>.Instance, postToUi: a => a());
    }

    private async Task<(string Keeper, string Duplicate, string DuplicatePath)> SeedPairAsync()
    {
        var content = "pcm-md5:" + Guid.NewGuid().ToString("N");
        var keeper = "vm-keep-" + Guid.NewGuid().ToString("N");
        var duplicate = "vm-dup-" + Guid.NewGuid().ToString("N");
        var keeperPath = Path.Combine(_dir, "keeper.mp3");
        var duplicatePath = Path.Combine(_dir, "duplicate.flac");
        await File.WriteAllTextAsync(keeperPath, "k");
        await File.WriteAllTextAsync(duplicatePath, "d");

        await using var db = new AppDbContext();
        db.LibraryEntries.AddRange(
            new LibraryEntryEntity { UniqueHash = keeper, Artist = "A", Title = "T", FilePath = keeperPath, Format = "mp3", Bitrate = 320, AudioContentHash = content, AddedAt = DateTime.UtcNow },
            new LibraryEntryEntity { UniqueHash = duplicate, Artist = "A", Title = "T", FilePath = duplicatePath, Format = "flac", Bitrate = 900, AudioContentHash = content, AddedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (keeper, duplicate, duplicatePath);
    }

    private sealed class RecordingRecycler : IFileRecycler
    {
        public List<string> Calls { get; } = new();
        public bool IsAvailable => true;
        public void MoveToRecycleBin(string path) => Calls.Add(path);
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AppDbContext());
    }
}
