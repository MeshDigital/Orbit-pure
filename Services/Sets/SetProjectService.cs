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

/// <summary>
/// The single write path for SetProjects (<see cref="SetListEntity"/>) and their slots
/// (<see cref="SetTrackEntity"/>). Every call persists immediately, so UI edits can't be lost by a
/// later reload. See DOCS/workstation/SETS_AND_TRANSITIONS_REBUILD_PLAN.md (P0.1 / P2.1).
/// </summary>
public interface ISetProjectService
{
    /// <summary>Creates a Draft set. A blank name gets a timestamped default.</summary>
    Task<SetListEntity> CreateSetAsync(string? name = null);

    /// <summary>Appends a track as the last slot of a set. Returns null when the set no longer exists.</summary>
    Task<SetTrackEntity?> AppendTrackAsync(Guid setId, string trackUniqueHash);

    /// <summary>Writes a slot's role and energy target. Returns false when the slot no longer exists.</summary>
    Task<bool> UpdateSlotAsync(Guid slotId, SetSlotRole role, double? energyTarget);

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
