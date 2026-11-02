using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Sets;
using Xunit;

namespace SLSKDONET.Tests.Services.Sets;

/// <summary>
/// <see cref="SetProjectService"/> against a real SQLite file next to the isolated test library
/// (ORBIT_DB_PATH, set by TestDatabaseSetup) — never the user's live library.db.
/// </summary>
public sealed class SetProjectServiceTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("set-project-service");
    private SetProjectService Service => new(_db.Factory);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateSetAsync_BlankName_GetsTimestampedDraft()
    {
        var set = await Service.CreateSetAsync("  ");

        Assert.StartsWith("Set ", set.Name);
        Assert.Equal(SetProjectStatus.Draft, set.Status);
        using var db = _db.Factory.CreateDbContext();
        Assert.True(await db.SetLists.AnyAsync(s => s.Id == set.Id));
    }

    [Fact]
    public async Task AppendTrackAsync_AssignsIncreasingPositions()
    {
        var set = await Service.CreateSetAsync("Friday");

        var a = await Service.AppendTrackAsync(set.Id, "hash-a");
        var b = await Service.AppendTrackAsync(set.Id, "hash-b");

        Assert.Equal(0, a!.Position);
        Assert.Equal(1, b!.Position);
    }

    [Fact]
    public async Task AppendTrackAsync_MissingSet_ReturnsNull()
    {
        Assert.Null(await Service.AppendTrackAsync(Guid.NewGuid(), "hash-a"));
    }

    [Fact]
    public async Task UpdateSlotAsync_PersistsRoleAndEnergy()
    {
        var set = await Service.CreateSetAsync("Friday");
        var slot = await Service.AppendTrackAsync(set.Id, "hash-a");

        Assert.True(await Service.UpdateSlotAsync(slot!.Id, SetSlotRole.Peak, 85));

        using var db = _db.Factory.CreateDbContext();
        var stored = await db.SetTracks.SingleAsync(t => t.Id == slot.Id);
        Assert.Equal(SetSlotRole.Peak, stored.SlotRole);
        Assert.Equal(85, stored.EnergyTarget);
    }

    [Fact]
    public async Task UpdateSlotAsync_MissingSlot_ReturnsFalse()
    {
        Assert.False(await Service.UpdateSlotAsync(Guid.NewGuid(), SetSlotRole.Peak, 50));
    }

    [Fact]
    public async Task SearchLibraryAsync_MatchesTitleOrArtistCaseInsensitively()
    {
        _db.SeedLibrary(("h1", "Noisia", "Machine Gun"), ("h2", "Calibre", "Second Sun"), ("h3", "Sub Focus", "Tidal Wave"));

        var byArtist = await Service.SearchLibraryAsync("calibre");
        var byTitle = await Service.SearchLibraryAsync("WAVE");

        Assert.Equal(new[] { "h2" }, byArtist.Select(r => r.TrackUniqueHash));
        Assert.Equal(new[] { "h3" }, byTitle.Select(r => r.TrackUniqueHash));
    }

    [Fact]
    public async Task SearchLibraryAsync_TreatsWildcardsLiterally()
    {
        _db.SeedLibrary(("h1", "A", "100% Pure"), ("h2", "B", "1000 Pure"));

        var results = await Service.SearchLibraryAsync("100%");

        Assert.Equal(new[] { "h1" }, results.Select(r => r.TrackUniqueHash));
    }

    [Fact]
    public async Task GetSetsAsync_ListsNewestFirst_WithSlotCounts_AndCanHideArchived()
    {
        var older = await Service.CreateSetAsync("Older");
        await Service.AppendTrackAsync(older.Id, "a");
        await Service.AppendTrackAsync(older.Id, "b");
        var archived = await Service.CreateSetAsync("Archived");
        using (var db = _db.Factory.CreateDbContext())
        {
            var row = await db.SetLists.SingleAsync(s => s.Id == archived.Id);
            row.Status = SetProjectStatus.Archived;
            row.LastModifiedAt = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }
        var newer = await Service.CreateSetAsync("Newer");

        var all = await Service.GetSetsAsync();
        var active = await Service.GetSetsAsync(includeArchived: false);

        Assert.Equal(new[] { newer.Id, older.Id, archived.Id }, all.Select(s => s.Id));
        Assert.Equal(2, all.Single(s => s.Id == older.Id).SlotCount);
        Assert.DoesNotContain(active, s => s.Id == archived.Id);
    }

    [Fact]
    public async Task GetSlotsAsync_JoinsLibraryAndAnalysis_InSpineOrder()
    {
        _db.SeedLibrary(("h1", "Noisia", "Machine Gun"));
        _db.SeedFeatures(("h1", 172.5f, "8A"));
        var set = await Service.CreateSetAsync("Friday");
        await Service.AppendTrackAsync(set.Id, "h1");
        await Service.AppendTrackAsync(set.Id, "not-in-library");

        var slots = await Service.GetSlotsAsync(set.Id);

        Assert.Equal(2, slots.Count);
        Assert.Equal(("Machine Gun", "Noisia", true, 172.5, "8A"),
            (slots[0].Title, slots[0].Artist, slots[0].IsInLibrary, slots[0].Bpm, slots[0].CamelotKey));
        Assert.False(slots[1].IsInLibrary);
        Assert.Null(slots[1].Bpm); // not analysed: no made-up 120 BPM
        Assert.Null(slots[1].CamelotKey);
    }

    [Fact]
    public async Task RenameSetAsync_TrimsAndRejectsBlank()
    {
        var set = await Service.CreateSetAsync("Friday");

        Assert.True(await Service.RenameSetAsync(set.Id, "  Club X  "));
        Assert.False(await Service.RenameSetAsync(set.Id, "   "));
        Assert.False(await Service.RenameSetAsync(Guid.NewGuid(), "Nope"));

        Assert.Equal("Club X", (await Service.GetSetsAsync()).Single().Name);
    }

    [Fact]
    public async Task InsertTrackAsync_ShiftsLaterSlots()
    {
        var set = await Service.CreateSetAsync("Friday");
        await Service.AppendTrackAsync(set.Id, "a");
        await Service.AppendTrackAsync(set.Id, "c");

        await Service.InsertTrackAsync(set.Id, "b", 1);
        await Service.InsertTrackAsync(set.Id, "z", 99); // clamped to the end

        var slots = await Service.GetSlotsAsync(set.Id);
        Assert.Equal(new[] { "a", "b", "c", "z" }, slots.Select(s => s.TrackUniqueHash));
        Assert.Equal(new[] { 0, 1, 2, 3 }, slots.Select(s => s.Position));
    }

    [Fact]
    public async Task MoveSlotAsync_ReordersAndRenumbers()
    {
        var set = await Service.CreateSetAsync("Friday");
        var a = await Service.AppendTrackAsync(set.Id, "a");
        await Service.AppendTrackAsync(set.Id, "b");
        await Service.AppendTrackAsync(set.Id, "c");

        Assert.True(await Service.MoveSlotAsync(a!.Id, 2));
        Assert.False(await Service.MoveSlotAsync(Guid.NewGuid(), 0));

        var slots = await Service.GetSlotsAsync(set.Id);
        Assert.Equal(new[] { "b", "c", "a" }, slots.Select(s => s.TrackUniqueHash));
        Assert.Equal(new[] { 0, 1, 2 }, slots.Select(s => s.Position));
    }

    [Fact]
    public async Task RemoveSlotAsync_RenumbersAndDropsTouchingEdges()
    {
        var set = await Service.CreateSetAsync("Friday");
        var a = await Service.AppendTrackAsync(set.Id, "a");
        var b = await Service.AppendTrackAsync(set.Id, "b");
        var c = await Service.AppendTrackAsync(set.Id, "c");
        _db.SeedEdge(set.Id, a!.Id, b!.Id);
        _db.SeedEdge(set.Id, a.Id, c!.Id);

        Assert.True(await Service.RemoveSlotAsync(b.Id));

        var slots = await Service.GetSlotsAsync(set.Id);
        Assert.Equal(new[] { "a", "c" }, slots.Select(s => s.TrackUniqueHash));
        Assert.Equal(new[] { 0, 1 }, slots.Select(s => s.Position));
        using var db = _db.Factory.CreateDbContext();
        var edge = await db.SetStoryEdges.SingleAsync();
        Assert.Equal((a.Id, c.Id), (edge.FromSlotId, edge.ToSlotId));
    }

    [Fact]
    public async Task UpdateSlotNotesAsync_TrimsAndClears()
    {
        var set = await Service.CreateSetAsync("Friday");
        var slot = await Service.AppendTrackAsync(set.Id, "a");

        await Service.UpdateSlotNotesAsync(slot!.Id, "  loop the intro  ");
        Assert.Equal("loop the intro", (await Service.GetSlotsAsync(set.Id)).Single().Notes);

        await Service.UpdateSlotNotesAsync(slot.Id, " ");
        Assert.Null((await Service.GetSlotsAsync(set.Id)).Single().Notes);
    }

    [Fact]
    public async Task DuplicateSetAsync_CopiesSlotsAndRemapsEdges_AsNewDraft()
    {
        var set = await Service.CreateSetAsync("Friday");
        var a = await Service.AppendTrackAsync(set.Id, "a");
        var b = await Service.AppendTrackAsync(set.Id, "b");
        await Service.UpdateSlotAsync(b!.Id, SetSlotRole.Peak, 90);
        await Service.UpdateSlotNotesAsync(b.Id, "drop at 1:30");
        _db.SeedEdge(set.Id, a!.Id, b.Id);
        using (var db = _db.Factory.CreateDbContext())
        {
            (await db.SetLists.SingleAsync(s => s.Id == set.Id)).Status = SetProjectStatus.Ready;
            await db.SaveChangesAsync();
        }

        var copy = await Service.DuplicateSetAsync(set.Id);

        Assert.NotNull(copy);
        Assert.Equal("Friday (copy)", copy!.Name);
        Assert.Equal(SetProjectStatus.Draft, copy.Status);
        var copied = await Service.GetSlotsAsync(copy.Id);
        Assert.Equal(new[] { "a", "b" }, copied.Select(s => s.TrackUniqueHash));
        Assert.Equal((SetSlotRole.Peak, 90.0, "drop at 1:30"), (copied[1].Role, copied[1].EnergyTarget, copied[1].Notes));
        Assert.DoesNotContain(copied, s => s.SlotId == a.Id || s.SlotId == b.Id);

        using var check = _db.Factory.CreateDbContext();
        var edge = await check.SetStoryEdges.SingleAsync(e => e.SetProjectId == copy.Id);
        Assert.Equal((copied[0].SlotId, copied[1].SlotId), (edge.FromSlotId, edge.ToSlotId));
        Assert.Null(await Service.DuplicateSetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteSetAsync_RemovesSlotsAndEdges_LeavesOtherSets()
    {
        var doomed = await Service.CreateSetAsync("Doomed");
        var a = await Service.AppendTrackAsync(doomed.Id, "a");
        var b = await Service.AppendTrackAsync(doomed.Id, "b");
        _db.SeedEdge(doomed.Id, a!.Id, b!.Id);
        var kept = await Service.CreateSetAsync("Kept");
        await Service.AppendTrackAsync(kept.Id, "x");

        Assert.True(await Service.DeleteSetAsync(doomed.Id));
        Assert.False(await Service.DeleteSetAsync(doomed.Id));

        using var db = _db.Factory.CreateDbContext();
        Assert.False(await db.SetLists.AnyAsync(s => s.Id == doomed.Id));
        Assert.False(await db.SetTracks.AnyAsync(t => t.SetListId == doomed.Id));
        Assert.False(await db.SetStoryEdges.AnyAsync(e => e.SetProjectId == doomed.Id));
        Assert.Equal(1, await db.SetTracks.CountAsync(t => t.SetListId == kept.Id));
    }

    [Fact]
    public async Task GetTrackLabelsAsync_FormatsArtistAndTitle_SkipsUnknown()
    {
        _db.SeedLibrary(("h1", "Noisia", "Machine Gun"), ("h2", "", "Untitled"));

        var labels = await Service.GetTrackLabelsAsync(new[] { "h1", "h2", "missing" });

        Assert.Equal("Noisia – Machine Gun", labels["h1"]);
        Assert.Equal("Untitled", labels["h2"]);
        Assert.False(labels.ContainsKey("missing"));
    }
}

/// <summary>A throwaway SQLite file inside the isolated test-library folder.</summary>
internal sealed class SetsTestDatabase : IDisposable
{
    private readonly string _dir;

    public IDbContextFactory<AppDbContext> Factory { get; }

    public SetsTestDatabase(string name)
    {
        var isolatedRoot = Path.GetDirectoryName(OrbitPaths.LibraryDbPath)!;
        Assert.Contains("orbit-tests", isolatedRoot); // never the user's AppData library
        _dir = Path.Combine(isolatedRoot, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "library.db")};Pooling=False")
            .Options;
        Factory = new OptionsFactory(options);
        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void SeedLibrary(params (string Hash, string Artist, string Title)[] tracks)
    {
        using var db = Factory.CreateDbContext();
        foreach (var (hash, artist, title) in tracks)
        {
            db.LibraryEntries.Add(new LibraryEntryEntity
            {
                UniqueHash = hash,
                Artist = artist,
                Title = title,
                FilePath = $@"C:\music\{hash}.flac",
            });
        }
        db.SaveChanges();
    }

    public void SeedFeatures(params (string Hash, float Bpm, string Camelot)[] tracks)
    {
        using var db = Factory.CreateDbContext();
        foreach (var (hash, bpm, camelot) in tracks)
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = hash, Bpm = bpm, CamelotKey = camelot });
        db.SaveChanges();
    }

    public void SeedEdge(Guid setId, Guid fromSlot, Guid toSlot)
    {
        using var db = Factory.CreateDbContext();
        db.SetStoryEdges.Add(new SetStoryEdgeEntity { SetProjectId = setId, FromSlotId = fromSlot, ToSlotId = toSlot });
        db.SaveChanges();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException ex) { Console.WriteLine($"SetsTestDatabase cleanup: {ex.Message}"); }
    }

    private sealed class OptionsFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public OptionsFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }
}
