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
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Intake;
using Xunit;

namespace SLSKDONET.Tests.Intake;

public sealed class IntakeScanServiceTests : IDisposable
{
    private readonly string _source = Path.Combine(Path.GetTempPath(), $"orbit-intake-{Guid.NewGuid():N}");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Factory _factory;
    private readonly FakeInspector _inspector = new();
    private IntakeLibraryIndex _library = new(Array.Empty<IntakeLibraryTrack>());

    public IntakeScanServiceTests()
    {
        Directory.CreateDirectory(_source);
        _options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"intake-{Guid.NewGuid():N}").Options;
        _factory = new Factory(_options);
    }

    private IntakeScanService Service() =>
        new(_factory, _inspector, NullLogger<IntakeScanService>.Instance, _ => Task.FromResult(_library)) { MaxParallelism = 2 };

    private string File(string relative, string? artist = "Artist", string? title = "Title", int seconds = 300, int kbps = 900)
    {
        var path = Path.GetFullPath(Path.Combine(_source, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        _inspector.Facts[path] = new AudioFileFacts(artist, title, "Album", TimeSpan.FromSeconds(seconds), kbps, 44100, 16, null, null, null);
        return path;
    }

    [Fact]
    public async Task CreateSession_ListsAudioFiles_AndCountsJunk()
    {
        File("a.flac");
        File("sub/b.mp3", kbps: 320);
        System.IO.File.WriteAllText(Path.Combine(_source, "cover.jpg"), "x");
        System.IO.File.WriteAllText(Path.Combine(_source, "._a.flac"), "x");

        var session = await Service().CreateSessionAsync(_source, deepDecodeCheck: true);

        Assert.Equal(2, session.JunkCount);
        await using var db = new AppDbContext(_options);
        var items = await db.IntakeItems.Where(i => i.SessionId == session.Id).OrderBy(i => i.RelativePath).ToListAsync();
        Assert.Equal(new[] { "a.flac", "sub/b.mp3" }, items.Select(i => i.RelativePath));
        Assert.All(items, i => Assert.Equal(IntakeItemStatus.Pending, i.Status));
    }

    [Fact]
    public void ListAudioFiles_NeverEntersAHiddenTrashFolder()
    {
        File("keep.flac");
        var trash = Directory.CreateDirectory(Path.Combine(_source, ".Trashes"));
        if (OperatingSystem.IsWindows()) trash.Attributes |= FileAttributes.Hidden; // dot-folders are hidden on macOS/Linux already
        System.IO.File.WriteAllText(Path.Combine(trash.FullName, "deleted.flac"), "x");

        var (files, _) = IntakeScanService.ListAudioFiles(_source, CancellationToken.None);

        Assert.Equal(new[] { "keep.flac" }, files.Select(f => Path.GetFileName(f.Path)));
    }

    [Fact]
    public async Task Scan_PutsEachFileInItsLane_AndNeverTouchesTheSource()
    {
        var good = File("good.flac", "New Artist", "New Track");
        var owned = File("owned.flac", "Owned Artist", "Owned Track");
        var bad = File("broken.flac", "X", "Broken");
        _inspector.DecodeErrors[bad] = 40;
        var before = Directory.GetFiles(_source, "*", SearchOption.AllDirectories).OrderBy(p => p).ToArray();

        _library = new IntakeLibraryIndex(new[]
        {
            new IntakeLibraryTrack("lib1", owned + ".lib", "Owned Artist", "Owned Track", "mp3", 192, 300, null),
        }, _ => true);

        var service = Service();
        var session = await service.CreateSessionAsync(_source, deepDecodeCheck: true);
        await service.ScanAsync(session.Id);

        await using var db = new AppDbContext(_options);
        var byName = await db.IntakeItems.ToDictionaryAsync(i => i.RelativePath);
        Assert.Equal(IntakeLane.Good, byName["good.flac"].Lane);
        Assert.Equal(IntakeLane.BetterCopy, byName["owned.flac"].Lane);
        Assert.Equal("lib1", byName["owned.flac"].MatchedTrackHash);
        Assert.Equal(IntakeLane.Reject, byName["broken.flac"].Lane);
        Assert.Equal(IntakeSessionStatus.Ready, (await db.IntakeSessions.SingleAsync()).Status);
        Assert.Equal(before, Directory.GetFiles(_source, "*", SearchOption.AllDirectories).OrderBy(p => p).ToArray());

        var summary = await service.GetSummaryAsync(session.Id);
        Assert.Equal((3, 1, 1, 1), (summary.Total, summary.Good, summary.BetterCopy, summary.Reject));
    }

    [Fact]
    public async Task Scan_SameTrackTwiceOnTheDrive_KeepsOnlyTheBestCopy()
    {
        File("flac/track.flac", "Artist", "Track", 300, 900);
        File("mp3/track.mp3", "Artist", "Track", 301, 320);
        File("radio/track.mp3", "Artist", "Track", 190, 320); // a different edit: kept separately

        var service = Service();
        var session = await service.CreateSessionAsync(_source, deepDecodeCheck: false);
        await service.ScanAsync(session.Id);

        await using var db = new AppDbContext(_options);
        var byName = await db.IntakeItems.ToDictionaryAsync(i => i.RelativePath);
        Assert.Equal(IntakeAction.Keep, byName["flac/track.flac"].Action);
        Assert.Equal(IntakeLane.Duplicate, byName["mp3/track.mp3"].Lane);
        Assert.Contains("flac/track.flac", byName["mp3/track.mp3"].Reason);
        Assert.Equal(IntakeAction.Keep, byName["radio/track.mp3"].Action);
        Assert.All(_inspector.DeepFlags, Assert.False);
    }

    [Fact]
    public async Task Scan_CancelledHalfway_ResumesWithOnlyTheUncheckedFiles()
    {
        for (int i = 0; i < 6; i++) File($"t{i}.flac", "Artist", $"Track {i}");
        var service = Service();
        var session = await service.CreateSessionAsync(_source, deepDecodeCheck: true);

        using var cts = new CancellationTokenSource();
        _inspector.OnInspect = () => { if (_inspector.Calls >= 3) cts.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanAsync(session.Id, null, cts.Token));

        int checkedFirst;
        await using (var db = new AppDbContext(_options))
            checkedFirst = await db.IntakeItems.CountAsync(i => i.Status == IntakeItemStatus.Scanned);
        Assert.InRange(checkedFirst, 1, 5);

        _inspector.OnInspect = null;
        int callsBefore = _inspector.Calls;
        await service.ScanAsync(session.Id);

        await using var after = new AppDbContext(_options);
        Assert.Equal(6, await after.IntakeItems.CountAsync(i => i.Status == IntakeItemStatus.Scanned));
        Assert.Equal(6 - checkedFirst, _inspector.Calls - callsBefore);
    }

    [Fact]
    public async Task Scan_InspectorThrows_MarksOnlyThatFileFailed()
    {
        var boom = File("boom.flac", "A", "Boom");
        File("fine.flac", "A", "Fine");
        _inspector.Throws.Add(boom);

        var service = Service();
        var session = await service.CreateSessionAsync(_source, deepDecodeCheck: true);
        await service.ScanAsync(session.Id);

        await using var db = new AppDbContext(_options);
        var byName = await db.IntakeItems.ToDictionaryAsync(i => i.RelativePath);
        Assert.Equal(IntakeItemStatus.Failed, byName["boom.flac"].Status);
        Assert.Equal("disk read error", byName["boom.flac"].Error);
        Assert.Equal(IntakeLane.Good, byName["fine.flac"].Lane);
    }

    [Fact]
    public async Task CreateSession_MissingFolder_Throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            Service().CreateSessionAsync(Path.Combine(_source, "nope"), true));
    }

    public void Dispose()
    {
        try { Directory.Delete(_source, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeInspector : IIntakeFileInspector
    {
        public readonly Dictionary<string, AudioFileFacts> Facts = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> DecodeErrors = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Throws = new(StringComparer.OrdinalIgnoreCase);
        public readonly System.Collections.Concurrent.ConcurrentBag<bool> DeepFlags = new();
        public Action? OnInspect;
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<IntakeInspection> InspectAsync(string path, bool deepDecodeCheck, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            DeepFlags.Add(deepDecodeCheck);
            OnInspect?.Invoke();
            if (Throws.Contains(path)) throw new IOException("disk read error");
            var spectral = new SpectralIntegrityResult { Verdict = AudioAuthenticityVerdict.GenuineLossless, Confidence = 0.9, SpectralCutoffHz = 22000 };
            return Task.FromResult(new IntakeInspection(Facts[path], null, null, DecodeErrors.GetValueOrDefault(path), null, spectral));
        }
    }

    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public Factory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
