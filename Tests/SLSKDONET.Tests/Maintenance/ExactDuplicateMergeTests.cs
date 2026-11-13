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
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Library.Maintenance;
using Xunit;

namespace SLSKDONET.Tests.Maintenance;

/// <summary>Issue #238: exact-duplicate review, merge and the opt-in Recycle Bin step (isolated DB).</summary>
public sealed class ExactDuplicateMergeTests : IDisposable
{
    private readonly string _dir;
    private readonly TestDbFactory _factory = new();
    private readonly EventBusService _bus = new();

    public ExactDuplicateMergeTests()
    {
        var isolatedRoot = Path.GetDirectoryName(OrbitPaths.LibraryDbPath)!;
        Assert.Contains("orbit-tests", isolatedRoot); // never the user's AppData library
        _dir = Path.Combine(isolatedRoot, "exact-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _bus.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* temp dir under the isolated test root; the run's cleanup removes it */ }
    }

    [Fact]
    public async Task GroupDetails_ThreeEntriesTwoShareAHash_OneGroupOfTwo_WithTheLosslessCopySuggested()
    {
        var content = "pcm-md5:" + Guid.NewGuid().ToString("N");
        var mp3 = NewHash("mp3");
        var flac = NewHash("flac");
        var other = NewHash("other");
        await using (var db = new AppDbContext())
        {
            db.LibraryEntries.AddRange(
                Entry(mp3, File("a.mp3"), "mp3", 320, content, DateTime.UtcNow.AddDays(-30)),
                Entry(flac, File("b.flac"), "flac", 900, content, DateTime.UtcNow),
                Entry(other, File("c.flac"), "flac", 900, "pcm-md5:" + Guid.NewGuid().ToString("N"), DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var groups = await Backfill().LoadGroupDetailsAsync();

        var group = Assert.Single(groups, g => g.AudioContentHash == content);
        Assert.Equal(2, group.Entries.Count);
        Assert.Equal(flac, group.SuggestedKeeperHash); // quality beats age
        Assert.DoesNotContain(groups, g => g.Entries.Any(e => e.UniqueHash == other));
    }

    [Fact]
    public void Keeper_PrefersFileOnDisk_ThenQuality_ThenCues_ThenPlaylists_ThenOldest()
    {
        var old = new DateTime(2020, 1, 1);
        var young = new DateTime(2024, 1, 1);
        ExactDuplicateEntry E(string h, string fmt, int kbps, int cues, int lists, DateTime added, bool exists = true) =>
            new(h, "A", "T", "", h, fmt, kbps, added, cues, lists, cues > 0, exists);

        Assert.Equal("on-disk", ExactDuplicateKeeper.Suggest(new[] { E("missing", "flac", 900, 9, 9, old, exists: false), E("on-disk", "mp3", 128, 0, 0, young) }).UniqueHash);
        Assert.Equal("flac", ExactDuplicateKeeper.Suggest(new[] { E("mp3", "mp3", 320, 9, 9, old), E("flac", "flac", 900, 0, 0, young) }).UniqueHash);
        Assert.Equal("cues", ExactDuplicateKeeper.Suggest(new[] { E("plain", "mp3", 320, 0, 9, old), E("cues", "mp3", 320, 4, 0, young) }).UniqueHash);
        Assert.Equal("lists", ExactDuplicateKeeper.Suggest(new[] { E("solo", "mp3", 320, 2, 0, old), E("lists", "mp3", 320, 2, 3, young) }).UniqueHash);
        Assert.Equal("old", ExactDuplicateKeeper.Suggest(new[] { E("young", "mp3", 320, 2, 1, young), E("old", "mp3", 320, 2, 1, old) }).UniqueHash);
    }

    [Fact]
    public async Task Merge_KeeperGetsTheUnionOfPlaylistsAndCues_AfterASafetyBackup()
    {
        var (keeper, duplicate, keeperPath, duplicatePath, p1, p2) = await SeedPairAsync();

        var result = await Cleanup().MergeIntoKeepersAsync(new[] { new ExactDuplicateMergePlan(keeper, new[] { duplicate }) });

        Assert.False(result.Aborted);
        Assert.Empty(result.Errors);
        Assert.True(System.IO.File.Exists(result.BackupPath), "a safety backup is taken before anything changes");
        Assert.Equal(1, result.GroupsMerged);
        Assert.Equal(1, result.EntriesRemoved);

        await using var db = new AppDbContext();
        Assert.False(await db.LibraryEntries.AnyAsync(e => e.UniqueHash == duplicate));

        var memberships = await db.PlaylistTracks.Where(t => t.PlaylistId == p1 || t.PlaylistId == p2).ToListAsync();
        Assert.Equal(new[] { p1, p2 }.OrderBy(x => x), memberships.Select(m => m.PlaylistId).OrderBy(x => x));
        Assert.All(memberships, m =>
        {
            Assert.Equal(keeper, m.TrackUniqueHash);
            Assert.Equal(keeperPath, m.ResolvedFilePath); // the row follows the kept file, not the removed copy
        });

        // Keeper had a cue at 10.01 s; the duplicate had 10.00 s (same cue) and 30 s (new) on pad 0, which the keeper uses.
        var cues = await db.CuePoints.Where(c => c.TrackUniqueHash == keeper).OrderBy(c => c.TimestampInSeconds).ToListAsync();
        Assert.Equal(2, cues.Count);
        Assert.Equal(10.01, cues[0].TimestampInSeconds, 6);
        Assert.Equal(30.0, cues[1].TimestampInSeconds, 6);
        Assert.Equal(-1, cues[1].SlotIndex); // pad taken on the keeper → memory cue
        Assert.False(await db.CuePoints.AnyAsync(c => c.TrackUniqueHash == duplicate));

        // Analysis only the duplicate had is kept.
        var kept = await db.LibraryEntries.SingleAsync(e => e.UniqueHash == keeper);
        Assert.Equal(174.0, kept.BPM);
        Assert.Equal("8A", kept.MusicalKey);

        Assert.True(System.IO.File.Exists(duplicatePath), "merging alone never touches files");
    }

    [Fact]
    public async Task Resolve_WithoutOptIn_DeletesNothingOnDisk()
    {
        var (keeper, duplicate, keeperPath, duplicatePath, _, _) = await SeedPairAsync();
        var recycler = new RecordingRecycler();

        var resolution = await Resolver(recycler).ResolveAsync(
            new[] { new ExactDuplicateMergePlan(keeper, new[] { duplicate }) }, moveFilesToRecycleBin: false);

        Assert.Equal(1, resolution.Merge.EntriesRemoved);
        Assert.Equal(0, resolution.FilesRecycled);
        Assert.Empty(recycler.Calls);
        Assert.True(System.IO.File.Exists(keeperPath));
        Assert.True(System.IO.File.Exists(duplicatePath));
    }

    [Fact]
    public async Task Resolve_WithOptIn_RecyclesOnlyTheRemovedCopy()
    {
        var (keeper, duplicate, keeperPath, duplicatePath, _, _) = await SeedPairAsync();
        var recycler = new RecordingRecycler();

        var resolution = await Resolver(recycler).ResolveAsync(
            new[] { new ExactDuplicateMergePlan(keeper, new[] { duplicate }) }, moveFilesToRecycleBin: true);

        Assert.Equal(1, resolution.FilesRecycled);
        Assert.Equal(new[] { duplicatePath }, recycler.Calls);
        Assert.True(System.IO.File.Exists(keeperPath));
        Assert.Contains("removed 1 entry", resolution.Summary(recycleRequested: true));
        Assert.Contains("0 errors", resolution.Summary(recycleRequested: true));
    }

    [Fact]
    public async Task Backfill_HashesUnhashedEntries_AndResumesWhereACancelledRunStopped()
    {
        var payload = new byte[4096];
        new Random(7).NextBytes(payload);
        var hashes = Enumerable.Range(0, 3).Select(i => NewHash("bf" + i)).ToList();
        await using (var db = new AppDbContext())
        {
            for (var i = 0; i < hashes.Count; i++)
            {
                var path = File($"bf{i}.mp3");
                await System.IO.File.WriteAllBytesAsync(path, payload);
                db.LibraryEntries.Add(Entry(hashes[i], path, "mp3", 320, null, DateTime.UtcNow.AddMinutes(i)));
            }
            await db.SaveChangesAsync();
        }

        var service = Backfill();

        // Cancel straight away: nothing (or only part) is hashed, and nothing is lost.
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.BackfillAsync(null, cts.Token));
        }

        // The next run picks up the rest; the same audio gives the same hash, so they group.
        await service.BackfillAsync(null);
        await using (var db = new AppDbContext())
        {
            var stored = await db.LibraryEntries.Where(e => hashes.Contains(e.UniqueHash)).Select(e => e.AudioContentHash).ToListAsync();
            Assert.All(stored, h => Assert.False(string.IsNullOrEmpty(h)));
            Assert.Single(stored.Distinct());
        }
        var group = Assert.Single(await service.FindDuplicateGroupsAsync(), g => g.TrackHashes.Contains(hashes[0]));
        Assert.Equal(3, group.TrackHashes.Count);
    }

    [Fact]
    public async Task TryStart_PublishesProgressAndAFinalEvent()
    {
        var events = new List<ContentHashBackfillProgressEvent>();
        using var sub = _bus.GetEvent<ContentHashBackfillProgressEvent>().Subscribe(e => { lock (events) events.Add(e); });
        var service = Backfill();

        Assert.True(service.TryStart());
        await service.WaitForRunAsync();

        lock (events)
        {
            Assert.True(events.First().IsRunning);
            Assert.False(events.Last().IsRunning);
            Assert.False(events.Last().WasCancelled);
            Assert.Null(events.Last().Error);
        }
        Assert.False(service.IsRunning);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(string Keeper, string Duplicate, string KeeperPath, string DuplicatePath, Guid P1, Guid P2)> SeedPairAsync()
    {
        var content = "pcm-md5:" + Guid.NewGuid().ToString("N");
        var keeper = NewHash("keep");
        var duplicate = NewHash("dup");
        var keeperPath = File("keeper.flac");
        var duplicatePath = File("duplicate.flac");
        await System.IO.File.WriteAllTextAsync(keeperPath, "keeper");
        await System.IO.File.WriteAllTextAsync(duplicatePath, "duplicate");
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();

        await using var db = new AppDbContext();
        db.Projects.AddRange(
            new PlaylistJobEntity { Id = p1, SourceTitle = "Keeper's list", CreatedAt = DateTime.UtcNow },
            new PlaylistJobEntity { Id = p2, SourceTitle = "Duplicate's list", CreatedAt = DateTime.UtcNow });
        db.LibraryEntries.AddRange(
            Entry(keeper, keeperPath, "flac", 900, content, DateTime.UtcNow),
            Entry(duplicate, duplicatePath, "flac", 900, content, DateTime.UtcNow, bpm: 174.0, key: "8A"));
        db.PlaylistTracks.AddRange(
            new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = p1, TrackUniqueHash = keeper, Artist = "A", Title = "T", ResolvedFilePath = keeperPath, AddedAt = DateTime.UtcNow },
            new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = p2, TrackUniqueHash = duplicate, Artist = "A", Title = "T", ResolvedFilePath = duplicatePath, AddedAt = DateTime.UtcNow });
        db.CuePoints.AddRange(
            new CuePointEntity { TrackUniqueHash = keeper, TimestampInSeconds = 10.01, Label = "Intro", SlotIndex = 0 },
            new CuePointEntity { TrackUniqueHash = duplicate, TimestampInSeconds = 10.00, Label = "Intro", SlotIndex = 1 },
            new CuePointEntity { TrackUniqueHash = duplicate, TimestampInSeconds = 30.0, Label = "Drop", SlotIndex = 0 });
        await db.SaveChangesAsync();
        return (keeper, duplicate, keeperPath, duplicatePath, p1, p2);
    }

    private static LibraryEntryEntity Entry(string hash, string path, string format, int bitrate, string? content, DateTime added,
        double? bpm = null, string? key = null) =>
        new()
        {
            UniqueHash = hash, Artist = "Artist", Title = "Title", FilePath = path, Format = format, Bitrate = bitrate,
            AudioContentHash = content, AddedAt = added, BPM = bpm, MusicalKey = key,
        };

    private static string NewHash(string tag) => $"exact-{tag}-{Guid.NewGuid():N}";

    private string File(string name) => Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8] + "-" + name);

    private LibraryContentHashBackfillService Backfill() =>
        new(_factory, new AudioContentHasher(NullLogger<AudioContentHasher>.Instance), NullLogger<LibraryContentHashBackfillService>.Instance, _bus);

    private DuplicateTrackCleanupService Cleanup() =>
        new(_factory, _bus, NullLogger<DuplicateTrackCleanupService>.Instance);

    private ExactDuplicateResolver Resolver(IFileRecycler recycler) =>
        new(Cleanup(), _factory, recycler, NullLogger<ExactDuplicateResolver>.Instance);

    private sealed class RecordingRecycler : IFileRecycler
    {
        public List<string> Calls { get; } = new();
        public bool IsAvailable => true;
        public void MoveToRecycleBin(string path) => Calls.Add(path);
    }

    private sealed class TestDbFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new();
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AppDbContext());
    }
}
