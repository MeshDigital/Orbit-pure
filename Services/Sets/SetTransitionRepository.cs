using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Repositories;

namespace SLSKDONET.Services.Sets;

/// <summary>
/// Set-scoped adapter over the unified <see cref="TransitionEntity"/> (<c>Transitions</c>) table
/// (Epic #517 / FO1b #519). Implements <see cref="ITransitionRepository"/> with unchanged signatures;
/// here the "playlist" id is the set id and the track ids are slot ids.
/// </summary>
public sealed class SetTransitionRepository : ITransitionRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SetTransitionRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<PlaylistTrackTransition?> GetTransitionAsync(Guid outgoingSlotId, Guid incomingSlotId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var entity = await db.Transitions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.ScopeKind == TransitionScopeKind.Set
                                   && t.OutgoingItemId == outgoingSlotId
                                   && t.IncomingItemId == incomingSlotId)
            .ConfigureAwait(false);
        return entity is null ? null : TransitionRepository.ToDomain(entity);
    }

    public async Task<List<PlaylistTrackTransition>> GetTransitionsForPlaylistAsync(Guid setId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var entities = await db.Transitions
            .AsNoTracking()
            .Where(t => t.ScopeKind == TransitionScopeKind.Set && t.ScopeId == setId)
            .ToListAsync()
            .ConfigureAwait(false);
        return entities.Select(TransitionRepository.ToDomain).ToList();
    }

    public async Task UpsertTransitionAsync(PlaylistTrackTransition transition)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            var existing = await db.Transitions
                .FirstOrDefaultAsync(t => t.ScopeKind == TransitionScopeKind.Set
                                       && t.OutgoingItemId == transition.OutgoingPlaylistTrackId
                                       && t.IncomingItemId == transition.IncomingPlaylistTrackId)
                .ConfigureAwait(false);

            var outHash = await ResolveSlotHashAsync(db, transition.OutgoingPlaylistTrackId).ConfigureAwait(false);
            var inHash = await ResolveSlotHashAsync(db, transition.IncomingPlaylistTrackId).ConfigureAwait(false);
            var hasPairHashes = !string.IsNullOrEmpty(outHash) && !string.IsNullOrEmpty(inHash);

            transition.UpdatedAtUtc = DateTime.UtcNow;
            if (existing is null)
            {
                db.Transitions.Add(TransitionRepository.ToEntity(
                    transition,
                    TransitionScopeKind.Set,
                    outHash,
                    inHash,
                    useAsPairDefault: hasPairHashes && transition.UseAsPairDefault));
            }
            else
            {
                var updated = TransitionRepository.ToEntity(
                    transition,
                    TransitionScopeKind.Set,
                    outHash ?? existing.OutgoingTrackHash,
                    inHash ?? existing.IncomingTrackHash,
                    useAsPairDefault: hasPairHashes && transition.UseAsPairDefault,
                    existingId: existing.Id);
                db.Entry(existing).CurrentValues.SetValues(updated);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DeleteTransitionAsync(Guid outgoingSlotId, Guid incomingSlotId)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            var existing = await db.Transitions
                .FirstOrDefaultAsync(t => t.ScopeKind == TransitionScopeKind.Set
                                       && t.OutgoingItemId == outgoingSlotId
                                       && t.IncomingItemId == incomingSlotId)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                db.Transitions.Remove(existing);
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<string?> ResolveSlotHashAsync(AppDbContext db, Guid slotId)
    {
        var raw = await db.SetTracks
            .AsNoTracking()
            .Where(t => t.Id == slotId)
            .Select(t => t.TrackUniqueHash)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        var trimmed = raw?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
