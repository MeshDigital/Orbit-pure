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

    /// <summary>
    /// Older rows can hold "2026-11-02T10:00:00" (T separator) while EF writes "2026-11-02 16:00:00".
    /// Ordered as SQLite text, every T-row sorts above every space-row whatever the time — the live
    /// rail showed a stale set first. Ordering must happen on parsed DateTimes.
    /// </summary>
    [Fact]
    public async Task GetSetsAsync_OrdersByRealTime_AcrossStoredDateFormats()
    {
        // Same day as "now", but earlier: only the separator decides a text comparison.
        var earlierToday = DateTime.UtcNow.Date.ToString("yyyy-MM-dd'T'00:00:01", System.Globalization.CultureInfo.InvariantCulture);
        using (var db = _db.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO SetLists (Id, Name, CreatedAt, LastModifiedAt, Status, FlowHealth) " +
                "VALUES ('6a2b7a9e-0000-0000-0000-000000000001', 'Old format', {0}, {0}, 0, 0)", earlierToday);
        }
        var fresh = await Service.CreateSetAsync("Fresh");

        var sets = await Service.GetSetsAsync();

        Assert.Equal(new[] { fresh.Id.ToString(), "6a2b7a9e-0000-0000-0000-000000000001" },
            sets.Select(s => s.Id.ToString()));
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
        Assert.NotNull(slots[0].Energy);
        Assert.False(slots[1].IsInLibrary);
        Assert.Null(slots[1].Bpm); // not analysed: no made-up 120 BPM
        Assert.Null(slots[1].CamelotKey);
        Assert.Null(slots[1].Energy); // no analysis row: not the 0.5 column default
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

        Assert.NotNull(await Service.RemoveSlotAsync(b.Id));
        Assert.Null(await Service.RemoveSlotAsync(b.Id));

        var slots = await Service.GetSlotsAsync(set.Id);
        Assert.Equal(new[] { "a", "c" }, slots.Select(s => s.TrackUniqueHash));
        Assert.Equal(new[] { 0, 1 }, slots.Select(s => s.Position));
        using var db = _db.Factory.CreateDbContext();
        var edge = await db.SetStoryEdges.SingleAsync();
        Assert.Equal((a.Id, c.Id), (edge.FromSlotId, edge.ToSlotId));
    }

    [Fact]
    public async Task RestoreSlotAsync_PutsSlotBackWithItsIdFieldsAndPosition()
    {
        var set = await Service.CreateSetAsync("Friday");
        await Service.AppendTrackAsync(set.Id, "a");
        var b = await Service.AppendTrackAsync(set.Id, "b");
        await Service.AppendTrackAsync(set.Id, "c");
        await Service.UpdateSlotAsync(b!.Id, SetSlotRole.Peak, 88);
        await Service.UpdateSlotNotesAsync(b.Id, "long blend");

        var snapshot = await Service.RemoveSlotAsync(b.Id);
        Assert.True(await Service.RestoreSlotAsync(snapshot!));
        Assert.False(await Service.RestoreSlotAsync(snapshot!)); // id already back in use

        var slots = await Service.GetSlotsAsync(set.Id);
        Assert.Equal(new[] { "a", "b", "c" }, slots.Select(s => s.TrackUniqueHash));
        Assert.Equal((b.Id, SetSlotRole.Peak, 88.0, "long blend"),
            (slots[1].SlotId, slots[1].Role, slots[1].EnergyTarget, slots[1].Notes));
    }

    [Fact]
    public async Task GetKnowledgeAsync_ReturnsOnlyRequestedPairs_StrongestEdge()
    {
        using (var db = _db.Factory.CreateDbContext())
        {
            db.TransitionKnowledge.Add(new TransitionKnowledgeEntity { SourceTrackId = "a", TargetTrackId = "b", CompatibilityScore = 70, EvidenceCount = 1 });
            db.TransitionKnowledge.Add(new TransitionKnowledgeEntity { SourceTrackId = "a", TargetTrackId = "b", TransitionType = KnowledgeTransitionType.Cut, CompatibilityScore = 85, EvidenceCount = 2, Verified = true });
            db.TransitionKnowledge.Add(new TransitionKnowledgeEntity { SourceTrackId = "a", TargetTrackId = "z", CompatibilityScore = 99 });
            await db.SaveChangesAsync();
        }

        var known = await Service.GetKnowledgeAsync(new[] { ("a", "b"), ("b", "c") });

        var edge = Assert.Single(known);
        Assert.Equal(("a", "b", 85.0, 3, true), (edge.SourceTrackHash, edge.TargetTrackHash, edge.CompatibilityScore, edge.EvidenceCount, edge.Verified));
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
    public async Task SearchPoolAsync_FiltersByTextAnalysisBpmAndKey()
    {
        _db.SeedLibrary(("a", "Noisia", "Alpha"), ("b", "Calibre", "Bravo"), ("c", "Sub Focus", "Charlie"), ("d", "Phace", "Delta"));
        _db.SeedFeatures(("a", 174f, "8A"), ("b", 172f, "9A"), ("c", 128f, "8A"));

        Assert.Equal(4, (await Service.SearchPoolAsync(new SetPoolQuery())).Count);
        Assert.Equal(new[] { "a", "b", "c" }, (await Service.SearchPoolAsync(new SetPoolQuery(AnalysedOnly: true))).Select(t => t.TrackUniqueHash));
        Assert.Equal(new[] { "a", "b" }, (await Service.SearchPoolAsync(new SetPoolQuery(BpmMin: 167, BpmMax: 181))).Select(t => t.TrackUniqueHash));
        Assert.Equal(new[] { "a", "c" }, (await Service.SearchPoolAsync(new SetPoolQuery(CamelotKeys: new[] { "8A" }))).Select(t => t.TrackUniqueHash));
        var unanalysed = (await Service.SearchPoolAsync(new SetPoolQuery(Text: "phace"))).Single();
        Assert.Equal(("d", (double?)null, (string?)null), (unanalysed.TrackUniqueHash, unanalysed.Bpm, unanalysed.CamelotKey));
    }

    [Fact]
    public async Task AppendPlaylistAsync_AddsLibraryTracksInPlaylistOrder_CountsSkipped()
    {
        _db.SeedLibrary(("a", "X", "A"), ("b", "Y", "B"));
        var playlistId = _db.SeedPlaylist(("b", 0), ("missing", 1), ("a", 2));
        var set = await Service.CreateSetAsync("Friday");
        await Service.AppendTrackAsync(set.Id, "first");

        var result = await Service.AppendPlaylistAsync(set.Id, playlistId);

        Assert.Equal(2, result!.Added.Count);
        Assert.Equal(1, result.SkippedNotInLibrary);
        var slots = await Service.GetSlotsAsync(set.Id);
        Assert.Equal(new[] { "first", "b", "a" }, slots.Select(s => s.TrackUniqueHash));
        Assert.Equal(new[] { 0, 1, 2 }, slots.Select(s => s.Position));
        Assert.Null(await Service.AppendPlaylistAsync(Guid.NewGuid(), playlistId));
    }

    [Fact]
    public async Task Alternates_AreListedPerSlot_AndStayOutOfTheSpine()
    {
        _db.SeedLibrary(("a", "X", "Alpha"), ("b", "Y", "Bravo"), ("alt", "Z", "Plan"));
        var set = await Service.CreateSetAsync("Friday");
        var a = await Service.AppendTrackAsync(set.Id, "a");
        await Service.AppendTrackAsync(set.Id, "b");

        var alt = await Service.AddAlternateAsync(a!.Id, "alt");
        Assert.Null(await Service.AddAlternateAsync(Guid.NewGuid(), "alt"));

        var slots = await Service.GetSlotsAsync(set.Id);
        Assert.Equal(new[] { "a", "b" }, slots.Select(s => s.TrackUniqueHash)); // not a third slot
        var listed = Assert.Single(slots[0].Alternates);
        Assert.Equal((alt!.Id, "Plan"), (listed.Id, listed.Title));
        Assert.Equal(2, (await Service.GetSetsAsync()).Single().SlotCount);
    }

    [Fact]
    public async Task SwapAlternate_KeepsSlotRoleEnergyNotes_AndUndoesBySwappingBack()
    {
        var set = await Service.CreateSetAsync("Friday");
        var slot = await Service.AppendTrackAsync(set.Id, "planned");
        await Service.UpdateSlotAsync(slot!.Id, SetSlotRole.Peak, 95);
        await Service.UpdateSlotNotesAsync(slot.Id, "big moment");
        var alt = await Service.AddAlternateAsync(slot.Id, "backup");

        Assert.True(await Service.SwapAlternateAsync(alt!.Id));
        var swapped = (await Service.GetSlotsAsync(set.Id)).Single();
        Assert.Equal(("backup", SetSlotRole.Peak, 95.0, "big moment"), (swapped.TrackUniqueHash, swapped.Role, swapped.EnergyTarget, swapped.Notes));
        Assert.Equal("planned", swapped.Alternates.Single().TrackUniqueHash);

        Assert.True(await Service.SwapAlternateAsync(alt.Id));
        Assert.Equal("planned", (await Service.GetSlotsAsync(set.Id)).Single().TrackUniqueHash);
    }

    [Fact]
    public async Task RemovingSlot_TakesAlternatesAlong_AndRestoreBringsThemBack()
    {
        var set = await Service.CreateSetAsync("Friday");
        var slot = await Service.AppendTrackAsync(set.Id, "a");
        await Service.AddAlternateAsync(slot!.Id, "alt1");
        await Service.AddAlternateAsync(slot.Id, "alt2");

        var snapshot = await Service.RemoveSlotAsync(slot.Id);
        using (var db = _db.Factory.CreateDbContext())
            Assert.False(await db.SetSlotAlternates.AnyAsync());

        Assert.True(await Service.RestoreSlotAsync(snapshot!));
        Assert.Equal(new[] { "alt1", "alt2" }, (await Service.GetSlotsAsync(set.Id)).Single().Alternates.Select(x => x.TrackUniqueHash));
    }

    [Fact]
    public async Task DuplicateAndDelete_HandleAlternates()
    {
        var set = await Service.CreateSetAsync("Friday");
        var slot = await Service.AppendTrackAsync(set.Id, "a");
        await Service.AddAlternateAsync(slot!.Id, "alt");

        var copy = await Service.DuplicateSetAsync(set.Id);
        var copiedSlot = (await Service.GetSlotsAsync(copy!.Id)).Single();
        Assert.Equal("alt", copiedSlot.Alternates.Single().TrackUniqueHash);

        await Service.DeleteSetAsync(set.Id);
        using var db = _db.Factory.CreateDbContext();
        Assert.Equal(1, await db.SetSlotAlternates.CountAsync()); // only the copy's alternate is left
        Assert.Equal(copiedSlot.SlotId, (await db.SetSlotAlternates.SingleAsync()).SlotId);
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

    public void SeedFeatures(params (string Hash, float Bpm, string Camelot)[] tracks)
    {
        using var db = Factory.CreateDbContext();
        foreach (var (hash, bpm, camelot) in tracks)
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = hash, Bpm = bpm, CamelotKey = camelot });
        db.SaveChanges();
    }

    /// <summary>Creates a playlist whose tracks have the given hashes and sort orders; returns its id.</summary>
    public Guid SeedPlaylist(params (string Hash, int SortOrder)[] tracks)
    {
        var playlistId = Guid.NewGuid();
        using var db = Factory.CreateDbContext();
        // A playlist routinely lists tracks that aren't downloaded yet. The production schema
        // (raw-SQL tables) doesn't enforce the EF model's PlaylistTracks FKs, but EnsureCreated
        // does, so seed with enforcement off on this connection to model that real state.
        db.Database.OpenConnection();
        db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        db.Projects.Add(new PlaylistJobEntity { Id = playlistId, SourceTitle = "Playlist", CreatedAt = DateTime.UtcNow });
        foreach (var (hash, sortOrder) in tracks)
            db.PlaylistTracks.Add(new PlaylistTrackEntity { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackUniqueHash = hash, SortOrder = sortOrder });
        db.SaveChanges();
        return playlistId;
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
