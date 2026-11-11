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
    public async Task GetTrackLabelsAsync_FormatsArtistAndTitle_SkipsUnknown()
    {
        _db.SeedLibrary(("h1", "Noisia", "Machine Gun"), ("h2", "", "Untitled"));

        var labels = await Service.GetTrackLabelsAsync(new[] { "h1", "h2", "missing" });

        Assert.Equal("Noisia – Machine Gun", labels["h1"]);
        Assert.Equal("Untitled", labels["h2"]);
        Assert.False(labels.ContainsKey("missing"));
    }

    [Fact]
    public async Task RenameSetAsync_PersistsNewName()
    {
        var set = await Service.CreateSetAsync("Friday");

        Assert.True(await Service.RenameSetAsync(set.Id, "Saturday"));

        using var db = _db.Factory.CreateDbContext();
        Assert.Equal("Saturday", (await db.SetLists.SingleAsync(s => s.Id == set.Id)).Name);
    }

    [Fact]
    public async Task RenameSetAsync_BlankName_ReturnsFalse()
    {
        var set = await Service.CreateSetAsync("Friday");
        Assert.False(await Service.RenameSetAsync(set.Id, "   "));
    }

    [Fact]
    public async Task DeleteSetAsync_RemovesSetSlotsAndEdges()
    {
        var set = await Service.CreateSetAsync("Friday");
        var a = await Service.AppendTrackAsync(set.Id, "hash-a");
        var b = await Service.AppendTrackAsync(set.Id, "hash-b");
        await Service.AddAlternateAsync(set.Id, a!.Id, b!.Id);

        Assert.True(await Service.DeleteSetAsync(set.Id));

        using var db = _db.Factory.CreateDbContext();
        Assert.False(await db.SetLists.AnyAsync(s => s.Id == set.Id));
        Assert.False(await db.SetTracks.AnyAsync(t => t.SetListId == set.Id));
        Assert.False(await db.SetStoryEdges.AnyAsync(e => e.SetProjectId == set.Id));
    }

    [Fact]
    public async Task DeleteSetAsync_MissingSet_ReturnsFalse()
    {
        Assert.False(await Service.DeleteSetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DuplicateSetAsync_CopiesSlotsAndEdges()
    {
        var set = await Service.CreateSetAsync("Friday");
        var a = await Service.AppendTrackAsync(set.Id, "hash-a");
        var b = await Service.AppendTrackAsync(set.Id, "hash-b");
        await Service.AddAlternateAsync(set.Id, a!.Id, b!.Id);

        var copy = await Service.DuplicateSetAsync(set.Id);

        Assert.NotNull(copy);
        Assert.NotEqual(set.Id, copy!.Id);
        Assert.Equal("Friday (Copy)", copy.Name);
        using var db = _db.Factory.CreateDbContext();
        var copyTracks = await db.SetTracks.Where(t => t.SetListId == copy.Id).OrderBy(t => t.Position).ToListAsync();
        Assert.Equal(new[] { "hash-a", "hash-b" }, copyTracks.Select(t => t.TrackUniqueHash));
        Assert.Equal(1, await db.SetStoryEdges.CountAsync(e => e.SetProjectId == copy.Id));
        Assert.Equal(2, await db.SetTracks.CountAsync(t => t.SetListId == set.Id));
    }

    [Fact]
    public async Task InsertTrackAsync_InsertsAtPositionAndShifts()
    {
        var set = await Service.CreateSetAsync("Friday");
        await Service.AppendTrackAsync(set.Id, "hash-a");
        await Service.AppendTrackAsync(set.Id, "hash-c");

        var inserted = await Service.InsertTrackAsync(set.Id, "hash-b", 1);

        Assert.Equal(1, inserted!.Position);
        using var db = _db.Factory.CreateDbContext();
        var ordered = await db.SetTracks.Where(t => t.SetListId == set.Id).OrderBy(t => t.Position).ToListAsync();
        Assert.Equal(new[] { "hash-a", "hash-b", "hash-c" }, ordered.Select(t => t.TrackUniqueHash));
    }

    [Fact]
    public async Task MoveTrackAsync_ReindexesNeighbours()
    {
        var set = await Service.CreateSetAsync("Friday");
        var a = await Service.AppendTrackAsync(set.Id, "hash-a");
        await Service.AppendTrackAsync(set.Id, "hash-b");
        await Service.AppendTrackAsync(set.Id, "hash-c");

        Assert.True(await Service.MoveTrackAsync(a!.Id, 2));

        using var db = _db.Factory.CreateDbContext();
        var ordered = await db.SetTracks.Where(t => t.SetListId == set.Id).OrderBy(t => t.Position).ToListAsync();
        Assert.Equal(new[] { "hash-b", "hash-c", "hash-a" }, ordered.Select(t => t.TrackUniqueHash));
    }

    [Fact]
    public async Task RemoveTrackAsync_CompactsAndDeletesEdges()
    {
        var set = await Service.CreateSetAsync("Friday");
        await Service.AppendTrackAsync(set.Id, "hash-a");
        var b = await Service.AppendTrackAsync(set.Id, "hash-b");
        var c = await Service.AppendTrackAsync(set.Id, "hash-c");
        await Service.AddAlternateAsync(set.Id, b!.Id, c!.Id);

        Assert.True(await Service.RemoveTrackAsync(b.Id));

        using var db = _db.Factory.CreateDbContext();
        var ordered = await db.SetTracks.Where(t => t.SetListId == set.Id).OrderBy(t => t.Position).ToListAsync();
        Assert.Equal(new[] { "hash-a", "hash-c" }, ordered.Select(t => t.TrackUniqueHash));
        Assert.Equal(new[] { 0, 1 }, ordered.Select(t => t.Position));
        Assert.False(await db.SetStoryEdges.AnyAsync(e => e.FromSlotId == b.Id || e.ToSlotId == b.Id));
    }

    [Fact]
    public async Task UpdateSlotNotesAsync_Persists()
    {
        var set = await Service.CreateSetAsync("Friday");
        var slot = await Service.AppendTrackAsync(set.Id, "hash-a");

        Assert.True(await Service.UpdateSlotNotesAsync(slot!.Id, "bring the filter in at 32"));

        using var db = _db.Factory.CreateDbContext();
        Assert.Equal("bring the filter in at 32", (await db.SetTracks.SingleAsync(t => t.Id == slot.Id)).DjNotes);
    }

    [Fact]
    public async Task AddAlternateAsync_CreatesEdge_RejectsForeignSlots()
    {
        var set = await Service.CreateSetAsync("Friday");
        var other = await Service.CreateSetAsync("Other");
        var a = await Service.AppendTrackAsync(set.Id, "hash-a");
        var b = await Service.AppendTrackAsync(set.Id, "hash-b");
        var foreign = await Service.AppendTrackAsync(other.Id, "hash-x");

        var edge = await Service.AddAlternateAsync(set.Id, a!.Id, b!.Id, 0.8, "if flat, reset");

        Assert.NotNull(edge);
        Assert.Equal(0.8, edge!.Probability);
        Assert.Equal("if flat, reset", edge.ConditionText);
        Assert.Null(await Service.AddAlternateAsync(set.Id, a.Id, foreign!.Id));
    }

    [Fact]
    public async Task RemoveAlternateAsync_RemovesEdge()
    {
        var set = await Service.CreateSetAsync("Friday");
        var a = await Service.AppendTrackAsync(set.Id, "hash-a");
        var b = await Service.AppendTrackAsync(set.Id, "hash-b");
        var edge = await Service.AddAlternateAsync(set.Id, a!.Id, b!.Id);

        Assert.True(await Service.RemoveAlternateAsync(edge!.Id));

        using var db = _db.Factory.CreateDbContext();
        Assert.False(await db.SetStoryEdges.AnyAsync(e => e.Id == edge.Id));
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
