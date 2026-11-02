using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Sets;

/// <summary>A library track as shown in set pickers.</summary>
public sealed record SetTrackPickerItem(string TrackUniqueHash, string Title, string Artist);

/// <summary>Track-pool filters for the Sets page. Null / empty means "don't filter on this".</summary>
public sealed record SetPoolQuery(
    string? Text = null,
    bool AnalysedOnly = false,
    double? BpmMin = null,
    double? BpmMax = null,
    IReadOnlyCollection<string>? CamelotKeys = null,
    int Limit = 300);

/// <summary>A library track in the Sets track pool, with whatever analysis exists.</summary>
public sealed record SetPoolTrack(string TrackUniqueHash, string Title, string Artist, double? Bpm, string? CamelotKey);

/// <summary>Result of seeding a set from a playlist.</summary>
public sealed record PlaylistAppendResult(IReadOnlyList<SetTrackEntity> Added, int SkippedNotInLibrary);

/// <summary>A set as listed in the Sets rail.</summary>
public sealed record SetSummary(Guid Id, string Name, SetProjectStatus Status, int SlotCount, DateTime LastModifiedAt);

/// <summary>
/// One slot of a set, joined with the library and analysis data the Sets page shows.
/// <see cref="Bpm"/>/<see cref="CamelotKey"/> are null when the track hasn't been analysed.
/// </summary>
public sealed record SetSlotInfo(
    Guid SlotId,
    int Position,
    string TrackUniqueHash,
    string Title,
    string Artist,
    bool IsInLibrary,
    SetSlotRole Role,
    double? EnergyTarget,
    string? Notes,
    double? Bpm,
    string? CamelotKey,
    double? Energy = null,
    IReadOnlyList<SetAlternateInfo>? Alternates = null)
{
    /// <summary>Plan B tracks for this slot, first choice first.</summary>
    public IReadOnlyList<SetAlternateInfo> Alternates { get; init; } = Alternates ?? Array.Empty<SetAlternateInfo>();
}

/// <summary>A "Plan B" track for a slot, joined with library and analysis data.</summary>
public sealed record SetAlternateInfo(
    Guid Id,
    string TrackUniqueHash,
    string Title,
    string Artist,
    double? Bpm,
    string? CamelotKey,
    string? ConditionText);

/// <summary>A removed slot and its alternates, detached, so undo can put both back.</summary>
public sealed record SetSlotSnapshot(SetTrackEntity Slot, IReadOnlyList<SetSlotAlternateEntity> Alternates);

/// <summary>What the knowledge graph knows about one A→B transition (by track hash).</summary>
public sealed record TransitionKnowledgeInfo(
    string SourceTrackHash,
    string TargetTrackHash,
    double CompatibilityScore,
    int EvidenceCount,
    bool Verified);

/// <summary>
/// The single write path for SetProjects (<see cref="SetListEntity"/>) and their slots
/// (<see cref="SetTrackEntity"/>). Every call persists immediately, so UI edits can't be lost by a
/// later reload. Slot positions are kept contiguous (0..n-1) after every insert, move and remove.
/// See DOCS/workstation/SETS_AND_TRANSITIONS_REBUILD_PLAN.md (P0.1 / P2.1).
/// </summary>
public interface ISetProjectService
{
    /// <summary>All sets, most recently modified first.</summary>
    Task<IReadOnlyList<SetSummary>> GetSetsAsync(bool includeArchived = true);

    /// <summary>A set's slots in spine order, with labels and analysis data. Empty for an unknown set.</summary>
    Task<IReadOnlyList<SetSlotInfo>> GetSlotsAsync(Guid setId);

    /// <summary>Creates a Draft set. A blank name gets a timestamped default.</summary>
    Task<SetListEntity> CreateSetAsync(string? name = null);

    /// <summary>Renames a set. Returns false when it no longer exists; blank names are rejected.</summary>
    Task<bool> RenameSetAsync(Guid setId, string name);

    /// <summary>
    /// Copies a set (name, event profile, flow weights, every slot and story edge) as a new Draft
    /// named "&lt;name&gt; (copy)". Returns null when the source no longer exists.
    /// </summary>
    Task<SetListEntity?> DuplicateSetAsync(Guid setId);

    /// <summary>Deletes a set with its slots and story edges. Returns false when it no longer exists.</summary>
    Task<bool> DeleteSetAsync(Guid setId);

    /// <summary>Appends a track as the last slot of a set. Returns null when the set no longer exists.</summary>
    Task<SetTrackEntity?> AppendTrackAsync(Guid setId, string trackUniqueHash);

    /// <summary>
    /// Inserts a track at <paramref name="index"/> (clamped to 0..count), shifting later slots.
    /// Returns null when the set no longer exists.
    /// </summary>
    Task<SetTrackEntity?> InsertTrackAsync(Guid setId, string trackUniqueHash, int index);

    /// <summary>Moves a slot to <paramref name="newIndex"/> (clamped). Returns false when it no longer exists.</summary>
    Task<bool> MoveSlotAsync(Guid slotId, int newIndex);

    /// <summary>
    /// Removes a slot, its alternates and any story edge touching it. Returns a detached snapshot
    /// (for undo via <see cref="RestoreSlotAsync"/>), or null when it no longer exists.
    /// </summary>
    Task<SetSlotSnapshot?> RemoveSlotAsync(Guid slotId);

    /// <summary>
    /// Puts a previously removed slot (and its alternates) back with its original id, role,
    /// energy and notes at its old position (clamped). Returns false when its set no longer
    /// exists or the id is in use.
    /// </summary>
    Task<bool> RestoreSlotAsync(SetSlotSnapshot snapshot);

    /// <summary>Adds a Plan B track for a slot (last in its list). Null when the slot no longer exists.</summary>
    Task<SetSlotAlternateEntity?> AddAlternateAsync(Guid slotId, string trackUniqueHash);

    /// <summary>Removes an alternate; returns a detached copy for undo, or null when it no longer exists.</summary>
    Task<SetSlotAlternateEntity?> RemoveAlternateAsync(Guid alternateId);

    /// <summary>Puts a removed alternate back. False when its slot is gone or the id is in use.</summary>
    Task<bool> RestoreAlternateAsync(SetSlotAlternateEntity snapshot);

    /// <summary>
    /// Swaps an alternate into its slot: the slot plays the alternate's track and the planned
    /// track becomes the alternate. Role, energy target and notes stay with the slot. Swapping
    /// again undoes it. False when the alternate no longer exists.
    /// </summary>
    Task<bool> SwapAlternateAsync(Guid alternateId);

    /// <summary>Knowledge-graph edges that exist for the given A→B track-hash pairs.</summary>
    Task<IReadOnlyList<TransitionKnowledgeInfo>> GetKnowledgeAsync(IEnumerable<(string From, string To)> pairs);

    /// <summary>Library tracks for the Sets track pool, filtered and ordered by title.</summary>
    Task<IReadOnlyList<SetPoolTrack>> SearchPoolAsync(SetPoolQuery query);

    /// <summary>
    /// Appends a playlist's tracks (in playlist order) to a set. Tracks that aren't in the library
    /// are skipped and counted. Returns null when the set no longer exists.
    /// </summary>
    Task<PlaylistAppendResult?> AppendPlaylistAsync(Guid setId, Guid playlistId);

    /// <summary>Writes a slot's role and energy target. Returns false when the slot no longer exists.</summary>
    Task<bool> UpdateSlotAsync(Guid slotId, SetSlotRole role, double? energyTarget);

    /// <summary>Writes a slot's DJ notes (blank clears them). Returns false when the slot no longer exists.</summary>
    Task<bool> UpdateSlotNotesAsync(Guid slotId, string? notes);

    /// <summary>Display labels ("Artist – Title") for the given track hashes; unknown hashes are omitted.</summary>
    Task<IReadOnlyDictionary<string, string>> GetTrackLabelsAsync(IEnumerable<string> trackUniqueHashes);

    /// <summary>
    /// Library tracks whose title or artist contains <paramref name="query"/> (case-insensitive),
    /// ordered by title. An empty query returns the first <paramref name="limit"/> tracks.
    /// </summary>
    Task<IReadOnlyList<SetTrackPickerItem>> SearchLibraryAsync(string? query, int limit = 200);
}

public sealed class SetProjectService : ISetProjectService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public SetProjectService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<SetSummary>> GetSetsAsync(bool includeArchived = true)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var sets = db.SetLists.AsNoTracking();
        if (!includeArchived)
            sets = sets.Where(s => s.Status != SetProjectStatus.Archived);

        var rows = await sets
            .OrderByDescending(s => s.LastModifiedAt)
            .Select(s => new { s.Id, s.Name, s.Status, s.LastModifiedAt, SlotCount = s.Tracks.Count })
            .ToListAsync()
            .ConfigureAwait(false);

        return rows.Select(r => new SetSummary(r.Id, r.Name, r.Status, r.SlotCount, r.LastModifiedAt)).ToList();
    }

    public async Task<IReadOnlyList<SetSlotInfo>> GetSlotsAsync(Guid setId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var slots = await db.SetTracks
            .AsNoTracking()
            .Where(t => t.SetListId == setId)
            .ToListAsync()
            .ConfigureAwait(false);
        if (slots.Count == 0)
            return Array.Empty<SetSlotInfo>();

        var slotIds = slots.Select(s => s.Id).ToList();
        var alternates = await db.SetSlotAlternates
            .AsNoTracking()
            .Where(a => slotIds.Contains(a.SlotId))
            .ToListAsync()
            .ConfigureAwait(false);

        var hashes = slots.Select(s => s.TrackUniqueHash)
            .Concat(alternates.Select(a => a.TrackUniqueHash))
            .Distinct()
            .ToList();

        var library = await db.LibraryEntries
            .AsNoTracking()
            .Where(e => hashes.Contains(e.UniqueHash))
            .Select(e => new { e.UniqueHash, e.Title, e.Artist })
            .ToListAsync()
            .ConfigureAwait(false);
        var libraryByHash = library.ToDictionary(e => e.UniqueHash, StringComparer.Ordinal);

        var features = await db.AudioFeatures
            .AsNoTracking()
            .Where(f => hashes.Contains(f.TrackUniqueHash))
            .Select(f => new { f.TrackUniqueHash, f.Bpm, f.CamelotKey, f.Energy })
            .ToListAsync()
            .ConfigureAwait(false);
        var featuresByHash = features
            .GroupBy(f => f.TrackUniqueHash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var alternatesBySlot = alternates
            .GroupBy(a => a.SlotId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<SetAlternateInfo>)g.OrderBy(a => a.Position).ThenBy(a => a.CreatedAtUtc)
                    .Select(a =>
                    {
                        libraryByHash.TryGetValue(a.TrackUniqueHash, out var altEntry);
                        featuresByHash.TryGetValue(a.TrackUniqueHash, out var altFeature);
                        return new SetAlternateInfo(
                            a.Id,
                            a.TrackUniqueHash,
                            altEntry?.Title ?? "Unknown track",
                            altEntry?.Artist ?? string.Empty,
                            altFeature is { Bpm: > 0 } ? altFeature.Bpm : null,
                            string.IsNullOrWhiteSpace(altFeature?.CamelotKey) ? null : altFeature!.CamelotKey,
                            a.ConditionText);
                    })
                    .ToList());

        return Ordered(slots)
            .Select(slot =>
            {
                libraryByHash.TryGetValue(slot.TrackUniqueHash, out var entry);
                featuresByHash.TryGetValue(slot.TrackUniqueHash, out var feature);
                alternatesBySlot.TryGetValue(slot.Id, out var slotAlternates);
                return new SetSlotInfo(
                    slot.Id,
                    slot.Position,
                    slot.TrackUniqueHash,
                    entry?.Title ?? "Unknown track",
                    entry?.Artist ?? string.Empty,
                    entry is not null,
                    slot.SlotRole,
                    slot.EnergyTarget,
                    slot.DjNotes,
                    feature is { Bpm: > 0 } ? (double?)feature.Bpm : null,
                    string.IsNullOrWhiteSpace(feature?.CamelotKey) ? null : feature!.CamelotKey,
                    // Energy is stored 0-1 with a 0.5 default, so only trust it when analysis ran.
                    feature is null ? null : Math.Round(Math.Clamp(feature.Energy, 0f, 1f) * 100.0),
                    slotAlternates);
            })
            .ToList();
    }

    public async Task<SetListEntity> CreateSetAsync(string? name = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = new SetListEntity
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Set {DateTime.Now:MMM d HH:mm}" : name.Trim(),
            Status = SetProjectStatus.Draft,
        };
        db.SetLists.Add(set);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return set;
    }

    public async Task<bool> RenameSetAsync(Guid setId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await db.SetLists.FirstOrDefaultAsync(s => s.Id == setId).ConfigureAwait(false);
        if (set is null)
            return false;

        set.Name = name.Trim();
        set.LastModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<SetListEntity?> DuplicateSetAsync(Guid setId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var source = await db.SetLists
            .AsNoTracking()
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setId)
            .ConfigureAwait(false);
        if (source is null)
            return null;

        var copy = new SetListEntity
        {
            Name = $"{source.Name} (copy)",
            Status = SetProjectStatus.Draft,
            EventProfileId = source.EventProfileId,
            FlowHealth = source.FlowHealth,
            FlowWeightsJson = source.FlowWeightsJson,
        };
        db.SetLists.Add(copy);

        var slotIdMap = new Dictionary<Guid, Guid>();
        foreach (var slot in Ordered(source.Tracks))
        {
            var cloned = CloneSlot(slot, Guid.NewGuid(), copy.Id);
            slotIdMap[slot.Id] = cloned.Id;
            db.SetTracks.Add(cloned);
        }

        var sourceSlotIds = slotIdMap.Keys.ToList();
        var alternates = await db.SetSlotAlternates
            .AsNoTracking()
            .Where(a => sourceSlotIds.Contains(a.SlotId))
            .ToListAsync()
            .ConfigureAwait(false);
        foreach (var alternate in alternates)
            db.SetSlotAlternates.Add(CloneAlternate(alternate, Guid.NewGuid(), slotIdMap[alternate.SlotId]));

        var edges = await db.SetStoryEdges
            .AsNoTracking()
            .Where(e => e.SetProjectId == setId)
            .ToListAsync()
            .ConfigureAwait(false);
        foreach (var edge in edges)
        {
            if (!slotIdMap.TryGetValue(edge.FromSlotId, out var from) || !slotIdMap.TryGetValue(edge.ToSlotId, out var to))
                continue; // dangling edge in the source; don't copy the corruption
            db.SetStoryEdges.Add(new SetStoryEdgeEntity
            {
                SetProjectId = copy.Id,
                FromSlotId = from,
                ToSlotId = to,
                TransitionKnowledgeId = edge.TransitionKnowledgeId,
                Probability = edge.Probability,
                ConditionText = edge.ConditionText,
            });
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return copy;
    }

    public async Task<bool> DeleteSetAsync(Guid setId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await db.SetLists.FirstOrDefaultAsync(s => s.Id == setId).ConfigureAwait(false);
        if (set is null)
            return false;

        // Explicit child deletes: these tables were created by raw SQL, so an ON DELETE CASCADE
        // in the EF model isn't guaranteed to exist in an older database.
        var slots = await db.SetTracks.Where(t => t.SetListId == setId).ToListAsync().ConfigureAwait(false);
        var slotIds = slots.Select(t => t.Id).ToList();
        db.SetSlotAlternates.RemoveRange(
            await db.SetSlotAlternates.Where(a => slotIds.Contains(a.SlotId)).ToListAsync().ConfigureAwait(false));
        db.SetStoryEdges.RemoveRange(
            await db.SetStoryEdges.Where(e => e.SetProjectId == setId).ToListAsync().ConfigureAwait(false));
        db.SetTracks.RemoveRange(slots);
        db.SetLists.Remove(set);

        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<SetTrackEntity?> AppendTrackAsync(Guid setId, string trackUniqueHash)
        => await InsertTrackAsync(setId, trackUniqueHash, int.MaxValue).ConfigureAwait(false);

    public async Task<SetTrackEntity?> InsertTrackAsync(Guid setId, string trackUniqueHash, int index)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setId)
            .ConfigureAwait(false);
        if (set is null)
            return null;

        var ordered = Ordered(set.Tracks).ToList();
        var slot = new SetTrackEntity { SetListId = setId, TrackUniqueHash = trackUniqueHash };
        ordered.Insert(Math.Clamp(index, 0, ordered.Count), slot);
        Renumber(ordered);

        db.SetTracks.Add(slot);
        set.LastModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return slot;
    }

    public async Task<bool> MoveSlotAsync(Guid slotId, int newIndex)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await LoadSetOfSlotAsync(db, slotId).ConfigureAwait(false);
        if (set is null)
            return false;

        var ordered = Ordered(set.Tracks).ToList();
        var slot = ordered.First(t => t.Id == slotId);
        ordered.Remove(slot);
        ordered.Insert(Math.Clamp(newIndex, 0, ordered.Count), slot);
        Renumber(ordered);

        set.LastModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<SetSlotSnapshot?> RemoveSlotAsync(Guid slotId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await LoadSetOfSlotAsync(db, slotId).ConfigureAwait(false);
        if (set is null)
            return null;

        var slot = set.Tracks.First(t => t.Id == slotId);
        var alternates = await db.SetSlotAlternates.Where(a => a.SlotId == slotId).ToListAsync().ConfigureAwait(false);
        var snapshot = new SetSlotSnapshot(
            CloneSlot(slot, slot.Id, slot.SetListId),
            alternates.Select(CloneAlternate).ToList());

        db.SetStoryEdges.RemoveRange(await db.SetStoryEdges
            .Where(e => e.FromSlotId == slotId || e.ToSlotId == slotId)
            .ToListAsync()
            .ConfigureAwait(false));
        db.SetSlotAlternates.RemoveRange(alternates);
        db.SetTracks.Remove(slot);

        Renumber(Ordered(set.Tracks.Where(t => t.Id != slotId)).ToList());
        set.LastModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return snapshot;
    }

    public async Task<bool> RestoreSlotAsync(SetSlotSnapshot snapshot)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var slotSnapshot = snapshot.Slot;
        if (await db.SetTracks.AnyAsync(t => t.Id == slotSnapshot.Id).ConfigureAwait(false))
            return false;

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == slotSnapshot.SetListId)
            .ConfigureAwait(false);
        if (set is null)
            return false;

        var restored = CloneSlot(slotSnapshot, slotSnapshot.Id, set.Id);
        var ordered = Ordered(set.Tracks).ToList();
        ordered.Insert(Math.Clamp(slotSnapshot.Position, 0, ordered.Count), restored);
        Renumber(ordered);

        db.SetTracks.Add(restored);
        db.SetSlotAlternates.AddRange(snapshot.Alternates.Select(CloneAlternate));
        set.LastModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<SetSlotAlternateEntity?> AddAlternateAsync(Guid slotId, string trackUniqueHash)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var slot = await db.SetTracks.FirstOrDefaultAsync(t => t.Id == slotId).ConfigureAwait(false);
        if (slot is null)
            return null;

        var count = await db.SetSlotAlternates.CountAsync(a => a.SlotId == slotId).ConfigureAwait(false);
        var alternate = new SetSlotAlternateEntity { SlotId = slotId, TrackUniqueHash = trackUniqueHash, Position = count };
        db.SetSlotAlternates.Add(alternate);
        await TouchSetAsync(db, slot.SetListId).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return CloneAlternate(alternate);
    }

    public async Task<SetSlotAlternateEntity?> RemoveAlternateAsync(Guid alternateId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var alternate = await db.SetSlotAlternates.FirstOrDefaultAsync(a => a.Id == alternateId).ConfigureAwait(false);
        if (alternate is null)
            return null;

        var snapshot = CloneAlternate(alternate);
        db.SetSlotAlternates.Remove(alternate);
        var setId = await db.SetTracks.Where(t => t.Id == alternate.SlotId).Select(t => (Guid?)t.SetListId)
            .FirstOrDefaultAsync().ConfigureAwait(false);
        if (setId is Guid id)
            await TouchSetAsync(db, id).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return snapshot;
    }

    public async Task<bool> RestoreAlternateAsync(SetSlotAlternateEntity snapshot)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        if (await db.SetSlotAlternates.AnyAsync(a => a.Id == snapshot.Id).ConfigureAwait(false))
            return false;
        var slot = await db.SetTracks.FirstOrDefaultAsync(t => t.Id == snapshot.SlotId).ConfigureAwait(false);
        if (slot is null)
            return false;

        db.SetSlotAlternates.Add(CloneAlternate(snapshot));
        await TouchSetAsync(db, slot.SetListId).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SwapAlternateAsync(Guid alternateId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var alternate = await db.SetSlotAlternates.FirstOrDefaultAsync(a => a.Id == alternateId).ConfigureAwait(false);
        if (alternate is null)
            return false;
        var slot = await db.SetTracks.FirstOrDefaultAsync(t => t.Id == alternate.SlotId).ConfigureAwait(false);
        if (slot is null)
            return false;

        (slot.TrackUniqueHash, alternate.TrackUniqueHash) = (alternate.TrackUniqueHash, slot.TrackUniqueHash);
        slot.LibraryId = null; // pointed at the old track's library row; the hash is the identity
        await TouchSetAsync(db, slot.SetListId).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<TransitionKnowledgeInfo>> GetKnowledgeAsync(IEnumerable<(string From, string To)> pairs)
    {
        var wanted = pairs.Where(p => !string.IsNullOrEmpty(p.From) && !string.IsNullOrEmpty(p.To)).ToHashSet();
        if (wanted.Count == 0)
            return Array.Empty<TransitionKnowledgeInfo>();

        var sources = wanted.Select(p => p.From).Distinct().ToList();

        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var edges = await db.TransitionKnowledge
            .AsNoTracking()
            .Where(e => sources.Contains(e.SourceTrackId))
            .Select(e => new { e.SourceTrackId, e.TargetTrackId, e.CompatibilityScore, e.EvidenceCount, e.Verified })
            .ToListAsync()
            .ConfigureAwait(false);

        // One edge per pair may exist per transition type; surface the strongest.
        return edges
            .Where(e => wanted.Contains((e.SourceTrackId, e.TargetTrackId)))
            .GroupBy(e => (e.SourceTrackId, e.TargetTrackId))
            .Select(g => new TransitionKnowledgeInfo(
                g.Key.SourceTrackId,
                g.Key.TargetTrackId,
                g.Max(e => e.CompatibilityScore),
                g.Sum(e => e.EvidenceCount),
                g.Any(e => e.Verified)))
            .ToList();
    }

    public async Task<bool> UpdateSlotAsync(Guid slotId, SetSlotRole role, double? energyTarget)
        => await UpdateSlotCoreAsync(slotId, slot =>
        {
            slot.SlotRole = role;
            slot.EnergyTarget = energyTarget;
        }).ConfigureAwait(false);

    public async Task<bool> UpdateSlotNotesAsync(Guid slotId, string? notes)
        => await UpdateSlotCoreAsync(slotId, slot =>
            slot.DjNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, string>> GetTrackLabelsAsync(IEnumerable<string> trackUniqueHashes)
    {
        var hashes = trackUniqueHashes.Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList();
        if (hashes.Count == 0)
            return new Dictionary<string, string>();

        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var rows = await db.LibraryEntries
            .Where(e => hashes.Contains(e.UniqueHash))
            .Select(e => new { e.UniqueHash, e.Artist, e.Title })
            .ToListAsync()
            .ConfigureAwait(false);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
            labels[row.UniqueHash] = string.IsNullOrWhiteSpace(row.Artist) ? row.Title : $"{row.Artist} – {row.Title}";
        return labels;
    }

    public async Task<IReadOnlyList<SetTrackPickerItem>> SearchLibraryAsync(string? query, int limit = 200)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var entries = db.LibraryEntries.AsNoTracking();
        var text = query?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            // SQLite LIKE is case-insensitive for ASCII; escape the wildcards the user may type.
            var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            entries = entries.Where(e =>
                EF.Functions.Like(e.Title, pattern, "\\") || EF.Functions.Like(e.Artist, pattern, "\\"));
        }

        var rows = await entries
            .OrderBy(e => e.Title)
            .Take(limit)
            .Select(e => new { e.UniqueHash, e.Title, e.Artist })
            .ToListAsync()
            .ConfigureAwait(false);

        return rows.Select(r => new SetTrackPickerItem(r.UniqueHash, r.Title, r.Artist)).ToList();
    }

    public async Task<IReadOnlyList<SetPoolTrack>> SearchPoolAsync(SetPoolQuery query)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var rows =
            from e in db.LibraryEntries.AsNoTracking()
            join f in db.AudioFeatures.AsNoTracking() on e.UniqueHash equals f.TrackUniqueHash into features
            from f in features.DefaultIfEmpty()
            select new { e.UniqueHash, e.Title, e.Artist, Bpm = (float?)f.Bpm, CamelotKey = f.CamelotKey };

        var text = query.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            rows = rows.Where(r => EF.Functions.Like(r.Title, pattern, "\\") || EF.Functions.Like(r.Artist, pattern, "\\"));
        }
        if (query.AnalysedOnly)
            rows = rows.Where(r => r.Bpm != null && r.Bpm > 0);
        if (query.BpmMin is double min)
            rows = rows.Where(r => r.Bpm >= (float)min);
        if (query.BpmMax is double max)
            rows = rows.Where(r => r.Bpm <= (float)max);
        if (query.CamelotKeys is { Count: > 0 } keys)
        {
            var keyList = keys.ToList();
            rows = rows.Where(r => r.CamelotKey != null && keyList.Contains(r.CamelotKey));
        }

        var found = await rows
            .OrderBy(r => r.Title)
            .Take(query.Limit * 2) // headroom: duplicate analysis rows are collapsed below
            .ToListAsync()
            .ConfigureAwait(false);

        return found
            .GroupBy(r => r.UniqueHash, StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(query.Limit)
            .Select(r => new SetPoolTrack(
                r.UniqueHash,
                r.Title,
                r.Artist,
                r.Bpm is > 0 ? r.Bpm : null,
                string.IsNullOrWhiteSpace(r.CamelotKey) ? null : r.CamelotKey))
            .ToList();
    }

    public async Task<PlaylistAppendResult?> AppendPlaylistAsync(Guid setId, Guid playlistId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setId)
            .ConfigureAwait(false);
        if (set is null)
            return null;

        var playlistHashes = (await db.PlaylistTracks
                .AsNoTracking()
                .Where(t => t.PlaylistId == playlistId)
                .Select(t => new { t.TrackUniqueHash, t.SortOrder, t.TrackNumber })
                .ToListAsync()
                .ConfigureAwait(false))
            .OrderBy(t => t.SortOrder).ThenBy(t => t.TrackNumber)
            .Select(t => t.TrackUniqueHash)
            .Where(h => !string.IsNullOrEmpty(h))
            .ToList();

        var inLibrary = (await db.LibraryEntries
                .AsNoTracking()
                .Where(e => playlistHashes.Contains(e.UniqueHash))
                .Select(e => e.UniqueHash)
                .ToListAsync()
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var ordered = Ordered(set.Tracks).ToList();
        var added = new List<SetTrackEntity>();
        foreach (var hash in playlistHashes.Where(inLibrary.Contains))
        {
            var slot = new SetTrackEntity { SetListId = setId, TrackUniqueHash = hash };
            ordered.Add(slot);
            added.Add(slot);
            db.SetTracks.Add(slot);
        }
        Renumber(ordered);

        if (added.Count > 0)
        {
            set.LastModifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        return new PlaylistAppendResult(added, playlistHashes.Count - added.Count);
    }

    private async Task<bool> UpdateSlotCoreAsync(Guid slotId, Action<SetTrackEntity> apply)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var slot = await db.SetTracks.FirstOrDefaultAsync(t => t.Id == slotId).ConfigureAwait(false);
        if (slot is null)
            return false;

        apply(slot);

        var set = await db.SetLists.FirstOrDefaultAsync(s => s.Id == slot.SetListId).ConfigureAwait(false);
        if (set is not null)
            set.LastModifiedAt = DateTime.UtcNow;

        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    private static async Task<SetListEntity?> LoadSetOfSlotAsync(AppDbContext db, Guid slotId)
    {
        var setId = await db.SetTracks
            .Where(t => t.Id == slotId)
            .Select(t => (Guid?)t.SetListId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        if (setId is null)
            return null;

        return await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setId)
            .ConfigureAwait(false);
    }

    private static async Task TouchSetAsync(AppDbContext db, Guid setId)
    {
        var set = await db.SetLists.FirstOrDefaultAsync(s => s.Id == setId).ConfigureAwait(false);
        if (set is not null)
            set.LastModifiedAt = DateTime.UtcNow;
    }

    private static SetSlotAlternateEntity CloneAlternate(SetSlotAlternateEntity a) => CloneAlternate(a, a.Id, a.SlotId);

    private static SetSlotAlternateEntity CloneAlternate(SetSlotAlternateEntity a, Guid id, Guid slotId) => new()
    {
        Id = id,
        SlotId = slotId,
        TrackUniqueHash = a.TrackUniqueHash,
        Position = a.Position,
        ConditionText = a.ConditionText,
        CreatedAtUtc = a.CreatedAtUtc,
    };

    /// <summary>A detached copy of every slot field, under the given id and set.</summary>
    private static SetTrackEntity CloneSlot(SetTrackEntity slot, Guid id, Guid setId) => new()
    {
        Id = id,
        SetListId = setId,
        TrackUniqueHash = slot.TrackUniqueHash,
        LibraryId = slot.LibraryId,
        Position = slot.Position,
        SlotRole = slot.SlotRole,
        EnergyTarget = slot.EnergyTarget,
        TransitionType = slot.TransitionType,
        ManualOffset = slot.ManualOffset,
        TransitionReasoning = slot.TransitionReasoning,
        DjNotes = slot.DjNotes,
        IsRescueTrack = slot.IsRescueTrack,
        RescueReason = slot.RescueReason,
    };

    /// <summary>Spine order; ties (legacy duplicate positions) break deterministically by id.</summary>
    private static IEnumerable<SetTrackEntity> Ordered(IEnumerable<SetTrackEntity> slots)
        => slots.OrderBy(t => t.Position).ThenBy(t => t.Id);

    private static void Renumber(IList<SetTrackEntity> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Position = i;
    }
}
