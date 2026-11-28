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
using SLSKDONET.Services.Intake;
using Xunit;

namespace SLSKDONET.Tests.Intake;

public sealed class IntakeApplyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orbit-intake-apply-{Guid.NewGuid():N}");
    private readonly string _source;
    private readonly string _library;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly FakeWriter _writer;
    private readonly IntakeApplyService _service;
    private readonly string _sessionId = Guid.NewGuid().ToString();

    public IntakeApplyServiceTests()
    {
        _source = Path.Combine(_root, "ssd");
        _library = Path.Combine(_root, "library");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_library);
        _options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"intake-apply-{Guid.NewGuid():N}").Options;
        _writer = new FakeWriter(_library);
        _service = new IntakeApplyService(new Factory(_options), _writer, NullLogger<IntakeApplyService>.Instance);
        using var db = new AppDbContext(_options);
        db.IntakeSessions.Add(new IntakeSessionEntity { Id = _sessionId, SourceRoot = _source, Label = "SSD", Status = IntakeSessionStatus.Ready });
        db.SaveChanges();
    }

    private IntakeItemEntity Item(string name, IntakeAction action, string artist = "Artist", string? title = null,
        IntakeMatchKind match = IntakeMatchKind.None, int size = 4)
    {
        var path = Path.Combine(_source, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)7, size).ToArray());
        var item = new IntakeItemEntity
        {
            SessionId = _sessionId, SourcePath = path, RelativePath = name, SizeBytes = size,
            Extension = Path.GetExtension(name), Status = IntakeItemStatus.Scanned, Lane = IntakeLane.Good,
            Action = action, Artist = artist, Title = title ?? Path.GetFileNameWithoutExtension(name),
            MatchKind = match, MatchedTrackHash = match == IntakeMatchKind.None ? null : "lib1",
            MatchedFilePath = match == IntakeMatchKind.None ? null : Path.Combine(_library, "old.mp3"),
        };
        using var db = new AppDbContext(_options);
        db.IntakeItems.Add(item);
        db.SaveChanges();
        return item;
    }

    private Dictionary<string, IntakeItemEntity> Items()
    {
        using var db = new AppDbContext(_options);
        return db.IntakeItems.AsNoTracking().ToDictionary(i => i.RelativePath);
    }

    [Fact]
    public async Task Apply_KeepCopiesIntoTheLibrary_SkipLeavesIt_AndTheSourceIsUntouched()
    {
        Item("keep.flac", IntakeAction.Keep);
        Item("skip.flac", IntakeAction.Skip);
        var before = Directory.GetFiles(_source).OrderBy(p => p).ToArray();

        var result = await _service.ApplyAsync(_sessionId);

        Assert.Equal((1, 0, 1, 0, 0), (result.Kept, result.Replaced, result.Skipped, result.UpgradesQueued, result.Failed));
        var items = Items();
        Assert.Equal(IntakeItemStatus.Applied, items["keep.flac"].Status);
        Assert.True(File.Exists(items["keep.flac"].AppliedPath));
        Assert.Equal(new[] { items["keep.flac"].AppliedPath }, _writer.Imported);
        Assert.Equal(IntakeItemStatus.Skipped, items["skip.flac"].Status);
        Assert.Equal(before, Directory.GetFiles(_source).OrderBy(p => p).ToArray());
        Assert.Empty(Directory.GetFiles(_library, "*.orbit-intake-part", SearchOption.AllDirectories));

        await using var db = new AppDbContext(_options);
        Assert.Equal(IntakeSessionStatus.Done, (await db.IntakeSessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Apply_NeverOverwritesADifferentFileAtTheTarget()
    {
        Item("track.flac", IntakeAction.Keep, size: 10);
        var target = Path.Combine(_library, "Artist", "track.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, new byte[] { 1 });

        await _service.ApplyAsync(_sessionId);

        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(target));
        Assert.Equal(Path.Combine(_library, "Artist", "track (2).flac"), Items()["track.flac"].AppliedPath);
    }

    [Fact]
    public async Task Apply_ReplaceStagesACopy_AndCleansUpWhenTheSwapRefuses()
    {
        Item("better.flac", IntakeAction.Replace, match: IntakeMatchKind.Metadata);
        Item("locked.flac", IntakeAction.Replace, match: IntakeMatchKind.Metadata);
        _writer.RefuseReplace.Add("locked.flac");

        var result = await _service.ApplyAsync(_sessionId);

        Assert.Equal((1, 1), (result.Replaced, result.Failed));
        var items = Items();
        Assert.Equal(IntakeItemStatus.Applied, items["better.flac"].Status);
        Assert.Equal(IntakeItemStatus.Failed, items["locked.flac"].Status);
        Assert.Contains("path is locked", items["locked.flac"].Error);
        Assert.Empty(Directory.GetFiles(Path.Combine(_library, ".orbit-intake")));
        Assert.True(File.Exists(Path.Combine(_source, "locked.flac")));

        await using var db = new AppDbContext(_options);
        Assert.Equal(IntakeSessionStatus.Ready, (await db.IntakeSessions.SingleAsync()).Status); // a failure stays open
    }

    [Fact]
    public async Task Apply_UpgradesAreQueuedAsOneProjectNamedForTheDrive()
    {
        Item("a.mp3", IntakeAction.Upgrade, title: "A");
        Item("b.mp3", IntakeAction.Upgrade, title: "B");

        var result = await _service.ApplyAsync(_sessionId);

        Assert.Equal(2, result.UpgradesQueued);
        Assert.Equal("Intake upgrades: SSD", _writer.UpgradeTitle);
        Assert.Equal(new[] { "A", "B" }, _writer.Upgrades.Select(i => i.Title).OrderBy(t => t));
        Assert.All(Items().Values, i => Assert.Equal(IntakeItemStatus.Applied, i.Status));
    }

    [Fact]
    public async Task Apply_ImportFailure_KeepsTheCopiesAndSaysWhere()
    {
        Item("keep.flac", IntakeAction.Keep);
        _writer.ImportThrows = true;

        var result = await _service.ApplyAsync(_sessionId);

        Assert.Equal(0, result.Kept);
        var item = Items()["keep.flac"];
        Assert.Equal(IntakeItemStatus.Failed, item.Status);
        Assert.Contains("not added to the library", item.Error);
        Assert.True(File.Exists(item.AppliedPath));
    }

    [Fact]
    public async Task Apply_SecondRunAfterSuccess_CopiesNothingAgain()
    {
        Item("keep.flac", IntakeAction.Keep);
        await _service.ApplyAsync(_sessionId);
        var again = await _service.ApplyAsync(_sessionId);
        Assert.Equal("Nothing to do", again.Summary);
        Assert.Single(_writer.Imported);
    }

    [Fact]
    public async Task SetAction_RefusesReplaceWithoutAMatch()
    {
        var item = Item("x.flac", IntakeAction.Keep);
        Assert.Equal(0, await _service.SetActionAsync(new[] { item.Id }, IntakeAction.Replace));
        Assert.Equal(1, await _service.SetActionAsync(new[] { item.Id }, IntakeAction.Skip));
        Assert.Equal(IntakeAction.Skip, Items()["x.flac"].Action);
    }

    [Fact]
    public async Task Apply_DriveUnplugged_Throws_AndChangesNothing()
    {
        Item("keep.flac", IntakeAction.Keep);
        Directory.Delete(_source, recursive: true);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _service.ApplyAsync(_sessionId));
        Assert.Equal(IntakeItemStatus.Scanned, Items()["keep.flac"].Status);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeWriter : IIntakeLibraryWriter
    {
        public FakeWriter(string root) => LibraryRoot = root;
        public string LibraryRoot { get; }
        public readonly List<string> Imported = new();
        public readonly HashSet<string> RefuseReplace = new();
        public readonly List<IntakeItemEntity> Upgrades = new();
        public string? UpgradeTitle;
        public bool ImportThrows;

        public string TargetPathFor(IntakeItemEntity item) => Path.Combine(LibraryRoot, item.Artist ?? "Unknown", Path.GetFileName(item.SourcePath));

        public Task<int> ImportFilesAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            if (ImportThrows) throw new IOException("database is locked");
            Imported.AddRange(paths);
            return Task.FromResult(paths.Count);
        }

        public Task<string> ReplaceAsync(IntakeItemEntity item, string newFile, CancellationToken ct)
        {
            if (RefuseReplace.Contains(item.RelativePath))
                throw new InvalidOperationException("Track lib1 path is locked (exported/protected); unlock before upgrading.");
            var target = Path.Combine(LibraryRoot, "replaced-" + item.RelativePath);
            File.Move(newFile, target);
            return Task.FromResult(target);
        }

        public Task<int> QueueUpgradesAsync(string title, IReadOnlyList<IntakeItemEntity> items, CancellationToken ct)
        {
            UpgradeTitle = title;
            Upgrades.AddRange(items);
            return Task.FromResult(items.Count);
        }
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public Factory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
