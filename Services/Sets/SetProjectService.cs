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

/// <summary>A set's summary row for the set-list rail.</summary>
public sealed record SetSummary(Guid Id, string Name, SetProjectStatus Status, int TrackCount, DateTime LastModifiedAt);

/// <summary>
/// The single write path for SetProjects (<see cref="SetListEntity"/>) and their slots
/// (<see cref="SetTrackEntity"/>). Every call persists immediately, so UI edits can't be lost by a
/// later reload. See DOCS/workstation/SETS_AND_TRANSITIONS_REBUILD_PLAN.md (P0.1 / P2.1).
/// </summary>
public interface ISetProjectService
{
    /// <summary>Creates a Draft set. A blank name gets a timestamped default.</summary>
    Task<SetListEntity> CreateSetAsync(string? name = null);

    /// <summary>All sets, newest first, as summaries for the set-list rail.</summary>
    Task<IReadOnlyList<SetSummary>> GetSetsAsync();

    /// <summary>A set's slots, ordered by position.</summary>
    Task<IReadOnlyList<SetTrackEntity>> GetSlotsAsync(Guid setId);

    /// <summary>Renames a set. Returns false when the set no longer exists or the name is blank.</summary>
    Task<bool> RenameSetAsync(Guid setId, string name);

    /// <summary>Deletes a set and its slots + branch edges. Returns false when the set no longer exists.</summary>
    Task<bool> DeleteSetAsync(Guid setId);

    /// <summary>Copies a set (slots + branch edges) into a new Draft set. Returns null when the source is gone.</summary>
    Task<SetListEntity?> DuplicateSetAsync(Guid setId, string? name = null);

    /// <summary>Appends a track as the last slot of a set. Returns null when the set no longer exists.</summary>
    Task<SetTrackEntity?> AppendTrackAsync(Guid setId, string trackUniqueHash);

    /// <summary>Inserts a track at <paramref name="position"/>, shifting later slots down. Returns null when the set is gone.</summary>
    Task<SetTrackEntity?> InsertTrackAsync(Guid setId, string trackUniqueHash, int position);

    /// <summary>Moves a slot to <paramref name="newPosition"/> within its set, reindexing the neighbours.</summary>
    Task<bool> MoveTrackAsync(Guid slotId, int newPosition);

    /// <summary>Removes a slot (and its branch edges), compacting the remaining positions.</summary>
    Task<bool> RemoveTrackAsync(Guid slotId);

    /// <summary>Writes a slot's role and energy target. Returns false when the slot no longer exists.</summary>
    Task<bool> UpdateSlotAsync(Guid slotId, SetSlotRole role, double? energyTarget);

    /// <summary>Writes a slot's DJ notes. Returns false when the slot no longer exists.</summary>
    Task<bool> UpdateSlotNotesAsync(Guid slotId, string? notes);

    /// <summary>Adds a Plan B alternate (a branch edge from <paramref name="fromSlotId"/> to <paramref name="toSlotId"/>).</summary>
    Task<SetStoryEdgeEntity?> AddAlternateAsync(Guid setId, Guid fromSlotId, Guid toSlotId, double probability = 0.5, string? conditionText = null);

    /// <summary>Removes a branch edge (Plan B alternate). Returns false when the edge no longer exists.</summary>
    Task<bool> RemoveAlternateAsync(Guid edgeId);

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

    public async Task<IReadOnlyList<SetSummary>> GetSetsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await db.SetLists
            .OrderByDescending(s => s.LastModifiedAt)
            .Select(s => new SetSummary(s.Id, s.Name, s.Status, s.Tracks.Count, s.LastModifiedAt))
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SetTrackEntity>> GetSlotsAsync(Guid setId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await db.SetTracks
            .Where(t => t.SetListId == setId)
            .OrderBy(t => t.Position)
            .ToListAsync()
            .ConfigureAwait(false);
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

    public async Task<bool> DeleteSetAsync(Guid setId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setId)
            .ConfigureAwait(false);
        if (set is null)
            return false;

        var edges = await db.SetStoryEdges
            .Where(e => e.SetProjectId == setId)
            .ToListAsync()
            .ConfigureAwait(false);

        db.SetStoryEdges.RemoveRange(edges);
        db.SetTracks.RemoveRange(set.Tracks);
        db.SetLists.Remove(set);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<SetListEntity?> DuplicateSetAsync(Guid setId, string? name = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setId)
            .ConfigureAwait(false);
        if (set is null)
            return null;

        var edges = await db.SetStoryEdges
            .Where(e => e.SetProjectId == setId)
            .ToListAsync()
            .ConfigureAwait(false);

        var copy = new SetListEntity
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"{set.Name} (Copy)" : name.Trim(),
            Status = SetProjectStatus.Draft,
            EventProfileId = set.EventProfileId,
        };
        db.SetLists.Add(copy);

        var idMap = new Dictionary<Guid, Guid>();
        foreach (var track in set.Tracks.OrderBy(t => t.Position))
        {
            var slot = new SetTrackEntity
            {
                SetListId = copy.Id,
                TrackUniqueHash = track.TrackUniqueHash,
                Position = track.Position,
                SlotRole = track.SlotRole,
                EnergyTarget = track.EnergyTarget,
                DjNotes = track.DjNotes,
            };
            db.SetTracks.Add(slot);
            idMap[track.Id] = slot.Id;
        }

        foreach (var edge in edges)
        {
            if (idMap.TryGetValue(edge.FromSlotId, out var from) && idMap.TryGetValue(edge.ToSlotId, out var to))
            {
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
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return copy;
    }

    public async Task<SetTrackEntity?> AppendTrackAsync(Guid setId, string trackUniqueHash)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setId)
            .ConfigureAwait(false);
        if (set is null)
            return null;

        var slot = new SetTrackEntity
        {
            SetListId = setId,
            TrackUniqueHash = trackUniqueHash,
            Position = set.Tracks.Count == 0 ? 0 : set.Tracks.Max(t => t.Position) + 1,
        };
        db.SetTracks.Add(slot);
        set.LastModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return slot;
    }

    public async Task<SetTrackEntity?> InsertTrackAsync(Guid setId, string trackUniqueHash, int position)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setId)
            .ConfigureAwait(false);
        if (set is null)
            return null;

        var ordered = set.Tracks.OrderBy(t => t.Position).ToList();
        var clamped = Math.Clamp(position, 0, ordered.Count);

        foreach (var t in ordered.Where(t => t.Position >= clamped))
            t.Position++;

        var slot = new SetTrackEntity
        {
            SetListId = setId,
            TrackUniqueHash = trackUniqueHash,
            Position = clamped,
        };
        db.SetTracks.Add(slot);
        set.LastModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return slot;
    }

    public async Task<bool> MoveTrackAsync(Guid slotId, int newPosition)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var slot = await db.SetTracks.FirstOrDefaultAsync(t => t.Id == slotId).ConfigureAwait(false);
        if (slot is null)
            return false;

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == slot.SetListId)
            .ConfigureAwait(false);
        if (set is null)
            return false;

        var ordered = set.Tracks.OrderBy(t => t.Position).ToList();
        var clamped = Math.Clamp(newPosition, 0, ordered.Count - 1);
        var current = slot.Position;

        if (clamped < current)
        {
            foreach (var t in ordered.Where(t => t.Id != slotId && t.Position >= clamped && t.Position < current))
                t.Position++;
        }
        else if (clamped > current)
        {
            foreach (var t in ordered.Where(t => t.Id != slotId && t.Position > current && t.Position <= clamped))
                t.Position--;
        }

        slot.Position = clamped;
        set.LastModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RemoveTrackAsync(Guid slotId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var slot = await db.SetTracks.FirstOrDefaultAsync(t => t.Id == slotId).ConfigureAwait(false);
        if (slot is null)
            return false;

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == slot.SetListId)
            .ConfigureAwait(false);
        if (set is null)
            return false;

        var edges = await db.SetStoryEdges
            .Where(e => e.FromSlotId == slotId || e.ToSlotId == slotId)
            .ToListAsync()
            .ConfigureAwait(false);

        db.SetStoryEdges.RemoveRange(edges);
        db.SetTracks.Remove(slot);

        foreach (var t in set.Tracks.Where(t => t.Id != slotId && t.Position > slot.Position))
            t.Position--;

        set.LastModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateSlotAsync(Guid slotId, SetSlotRole role, double? energyTarget)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var slot = await db.SetTracks.FirstOrDefaultAsync(t => t.Id == slotId).ConfigureAwait(false);
        if (slot is null)
            return false;

        slot.SlotRole = role;
        slot.EnergyTarget = energyTarget;

        var set = await db.SetLists.FirstOrDefaultAsync(s => s.Id == slot.SetListId).ConfigureAwait(false);
        if (set is not null)
            set.LastModifiedAt = DateTime.UtcNow;

        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateSlotNotesAsync(Guid slotId, string? notes)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var slot = await db.SetTracks.FirstOrDefaultAsync(t => t.Id == slotId).ConfigureAwait(false);
        if (slot is null)
            return false;

        slot.DjNotes = notes;

        var set = await db.SetLists.FirstOrDefaultAsync(s => s.Id == slot.SetListId).ConfigureAwait(false);
        if (set is not null)
            set.LastModifiedAt = DateTime.UtcNow;

        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<SetStoryEdgeEntity?> AddAlternateAsync(Guid setId, Guid fromSlotId, Guid toSlotId, double probability = 0.5, string? conditionText = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var fromExists = await db.SetTracks.AnyAsync(t => t.Id == fromSlotId && t.SetListId == setId).ConfigureAwait(false);
        var toExists = await db.SetTracks.AnyAsync(t => t.Id == toSlotId && t.SetListId == setId).ConfigureAwait(false);
        if (!fromExists || !toExists)
            return null;

        var edge = new SetStoryEdgeEntity
        {
            SetProjectId = setId,
            FromSlotId = fromSlotId,
            ToSlotId = toSlotId,
            Probability = probability,
            ConditionText = conditionText,
        };
        db.SetStoryEdges.Add(edge);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return edge;
    }

    public async Task<bool> RemoveAlternateAsync(Guid edgeId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var edge = await db.SetStoryEdges.FirstOrDefaultAsync(e => e.Id == edgeId).ConfigureAwait(false);
        if (edge is null)
            return false;

        db.SetStoryEdges.Remove(edge);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

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
}
