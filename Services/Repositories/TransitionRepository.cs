using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models.Timeline;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services.Repositories;

public class TransitionRepository : ITransitionRepository
{
    private readonly ILogger<TransitionRepository> _logger;
    private static readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);

    public TransitionRepository(ILogger<TransitionRepository> logger)
    {
        _logger = logger;
    }

    public async Task<PlaylistTrackTransition?> GetTransitionAsync(Guid outgoingPlaylistTrackId, Guid incomingPlaylistTrackId)
    {
        using var context = new AppDbContext();
        var entity = await context.PlaylistTrackTransitions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.OutgoingPlaylistTrackId == outgoingPlaylistTrackId
                                    && t.IncomingPlaylistTrackId == incomingPlaylistTrackId);
        return entity == null ? null : ToDomain(entity);
    }

    public async Task<List<PlaylistTrackTransition>> GetTransitionsForPlaylistAsync(Guid playlistId)
    {
        using var context = new AppDbContext();
        var entities = await context.PlaylistTrackTransitions
            .AsNoTracking()
            .Where(t => t.PlaylistId == playlistId)
            .ToListAsync();
        return entities.Select(ToDomain).ToList();
    }

    public async Task UpsertTransitionAsync(PlaylistTrackTransition transition)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var existing = await context.PlaylistTrackTransitions.FirstOrDefaultAsync(
                t => t.OutgoingPlaylistTrackId == transition.OutgoingPlaylistTrackId
                  && t.IncomingPlaylistTrackId == transition.IncomingPlaylistTrackId);

            transition.UpdatedAtUtc = DateTime.UtcNow;

            if (existing == null)
            {
                context.PlaylistTrackTransitions.Add(ToEntity(transition));
            }
            else
            {
                context.Entry(existing).CurrentValues.SetValues(ToEntity(transition, existing.Id));
            }

            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save transition {Outgoing}->{Incoming}",
                transition.OutgoingPlaylistTrackId, transition.IncomingPlaylistTrackId);
            throw;
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task DeleteTransitionAsync(Guid outgoingPlaylistTrackId, Guid incomingPlaylistTrackId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var existing = await context.PlaylistTrackTransitions.FirstOrDefaultAsync(
                t => t.OutgoingPlaylistTrackId == outgoingPlaylistTrackId
                  && t.IncomingPlaylistTrackId == incomingPlaylistTrackId);
            if (existing != null)
            {
                context.PlaylistTrackTransitions.Remove(existing);
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    private static PlaylistTrackTransition ToDomain(PlaylistTrackTransitionEntity e) => new()
    {
        Id = e.Id,
        PlaylistId = e.PlaylistId,
        OutgoingPlaylistTrackId = e.OutgoingPlaylistTrackId,
        IncomingPlaylistTrackId = e.IncomingPlaylistTrackId,
        PresetName = e.PresetName,
        Type = Enum.TryParse<TransitionType>(e.TransitionType, out var t) ? t : TransitionType.Crossfade,
        DurationBars = e.DurationBars,
        EchoDecayFactor = e.EchoDecayFactor,
        FilterStartFrequency = e.FilterStartFrequency,
        FilterEndFrequency = e.FilterEndFrequency,
        EqLowGain = e.EqLowGain,
        EqMidGain = e.EqMidGain,
        EqHighGain = e.EqHighGain,
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

    private static PlaylistTrackTransitionEntity ToEntity(PlaylistTrackTransition m, Guid? existingId = null) => new()
    {
        Id = existingId ?? m.Id,
        PlaylistId = m.PlaylistId,
        OutgoingPlaylistTrackId = m.OutgoingPlaylistTrackId,
        IncomingPlaylistTrackId = m.IncomingPlaylistTrackId,
        PresetName = m.PresetName,
        TransitionType = m.Type.ToString(),
        DurationBars = m.DurationBars,
        EchoDecayFactor = m.EchoDecayFactor,
        FilterStartFrequency = m.FilterStartFrequency,
        FilterEndFrequency = m.FilterEndFrequency,
        EqLowGain = m.EqLowGain,
        EqMidGain = m.EqMidGain,
        EqHighGain = m.EqHighGain,
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
