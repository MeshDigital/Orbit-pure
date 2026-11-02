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
/// Saved transitions between adjacent set slots (rebuild plan P3.2), stored in
/// <see cref="SetTrackTransitionEntity"/>. Implements the same <see cref="ITransitionRepository"/>
/// as the playlist store so the one transition editor can save into either; here the "playlist"
/// id is the set id and the track ids are slot ids.
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
        var entity = await db.SetTrackTransitions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.OutgoingSlotId == outgoingSlotId && t.IncomingSlotId == incomingSlotId)
            .ConfigureAwait(false);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<List<PlaylistTrackTransition>> GetTransitionsForPlaylistAsync(Guid setId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var entities = await db.SetTrackTransitions
            .AsNoTracking()
            .Where(t => t.SetId == setId)
            .ToListAsync()
            .ConfigureAwait(false);
        return entities.Select(ToDomain).ToList();
    }

    public async Task UpsertTransitionAsync(PlaylistTrackTransition transition)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            var existing = await db.SetTrackTransitions
                .FirstOrDefaultAsync(t => t.OutgoingSlotId == transition.OutgoingPlaylistTrackId
                                       && t.IncomingSlotId == transition.IncomingPlaylistTrackId)
                .ConfigureAwait(false);

            transition.UpdatedAtUtc = DateTime.UtcNow;
            if (existing is null)
                db.SetTrackTransitions.Add(ToEntity(transition));
            else
                db.Entry(existing).CurrentValues.SetValues(ToEntity(transition, existing.Id));

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
            var existing = await db.SetTrackTransitions
                .FirstOrDefaultAsync(t => t.OutgoingSlotId == outgoingSlotId && t.IncomingSlotId == incomingSlotId)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                db.SetTrackTransitions.Remove(existing);
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal static PlaylistTrackTransition ToDomain(SetTrackTransitionEntity e) => new()
    {
        Id = e.Id,
        PlaylistId = e.SetId,
        OutgoingPlaylistTrackId = e.OutgoingSlotId,
        IncomingPlaylistTrackId = e.IncomingSlotId,
        PresetName = e.PresetName,
        Type = Enum.TryParse<TransitionType>(e.TransitionType, out var type) ? type : TransitionType.Crossfade,
        DurationBars = e.DurationBars,
        EchoDecayFactor = e.EchoDecayFactor,
        FilterStartFrequency = e.FilterStartFrequency,
        FilterEndFrequency = e.FilterEndFrequency,
        WaveDuckDepth = e.WaveDuckDepth,
        FilterSweepRising = e.FilterSweepRising,
        EqSwapLow = e.EqSwapLow,
        EqSwapMid = e.EqSwapMid,
        EqSwapHigh = e.EqSwapHigh,
        EqLowCrossoverHz = e.EqLowCrossoverHz,
        EqHighCrossoverHz = e.EqHighCrossoverHz,
        LoopBars = e.LoopBars,
        LoopRepeats = e.LoopRepeats,
        SourceTriggerSeconds = e.SourceTriggerSeconds,
        TargetTriggerSeconds = e.TargetTriggerSeconds,
        UpdatedAtUtc = e.UpdatedAtUtc,
    };

    private static SetTrackTransitionEntity ToEntity(PlaylistTrackTransition m, Guid? existingId = null) => new()
    {
        Id = existingId ?? m.Id,
        SetId = m.PlaylistId,
        OutgoingSlotId = m.OutgoingPlaylistTrackId,
        IncomingSlotId = m.IncomingPlaylistTrackId,
        PresetName = m.PresetName,
        TransitionType = m.Type.ToString(),
        DurationBars = m.DurationBars,
        EchoDecayFactor = m.EchoDecayFactor,
        FilterStartFrequency = m.FilterStartFrequency,
        FilterEndFrequency = m.FilterEndFrequency,
        WaveDuckDepth = m.WaveDuckDepth,
        FilterSweepRising = m.FilterSweepRising,
        EqSwapLow = m.EqSwapLow,
        EqSwapMid = m.EqSwapMid,
        EqSwapHigh = m.EqSwapHigh,
        EqLowCrossoverHz = m.EqLowCrossoverHz,
        EqHighCrossoverHz = m.EqHighCrossoverHz,
        LoopBars = m.LoopBars,
        LoopRepeats = m.LoopRepeats,
        SourceTriggerSeconds = m.SourceTriggerSeconds,
        TargetTriggerSeconds = m.TargetTriggerSeconds,
        UpdatedAtUtc = m.UpdatedAtUtc,
    };
}
