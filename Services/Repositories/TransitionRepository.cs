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

/// <summary>
/// Playlist-scoped adapter over the unified <see cref="TransitionEntity"/> (<c>Transitions</c>) table
/// (Epic #517 / FO1b #519). Preserves the <see cref="ITransitionRepository"/> contract unchanged.
/// </summary>
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
        var entity = await context.Transitions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.ScopeKind == TransitionScopeKind.Playlist
                                    && t.OutgoingItemId == outgoingPlaylistTrackId
                                    && t.IncomingItemId == incomingPlaylistTrackId);
        return entity == null ? null : ToDomain(entity);
    }

    public async Task<List<PlaylistTrackTransition>> GetTransitionsForPlaylistAsync(Guid playlistId)
    {
        using var context = new AppDbContext();
        var entities = await context.Transitions
            .AsNoTracking()
            .Where(t => t.ScopeKind == TransitionScopeKind.Playlist && t.ScopeId == playlistId)
            .ToListAsync();
        return entities.Select(ToDomain).ToList();
    }

    public async Task UpsertTransitionAsync(PlaylistTrackTransition transition)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var existing = await context.Transitions.FirstOrDefaultAsync(
                t => t.ScopeKind == TransitionScopeKind.Playlist
                  && t.OutgoingItemId == transition.OutgoingPlaylistTrackId
                  && t.IncomingItemId == transition.IncomingPlaylistTrackId);

            var outHash = await ResolvePlaylistTrackHashAsync(context, transition.OutgoingPlaylistTrackId);
            var inHash = await ResolvePlaylistTrackHashAsync(context, transition.IncomingPlaylistTrackId);
            var hasPairHashes = !string.IsNullOrEmpty(outHash) && !string.IsNullOrEmpty(inHash);

            transition.UpdatedAtUtc = DateTime.UtcNow;

            if (existing == null)
            {
                context.Transitions.Add(ToEntity(
                    transition,
                    TransitionScopeKind.Playlist,
                    outHash,
                    inHash,
                    useAsPairDefault: hasPairHashes));
            }
            else
            {
                var updated = ToEntity(
                    transition,
                    TransitionScopeKind.Playlist,
                    outHash ?? existing.OutgoingTrackHash,
                    inHash ?? existing.IncomingTrackHash,
                    useAsPairDefault: hasPairHashes && existing.UseAsPairDefault,
                    existingId: existing.Id);
                context.Entry(existing).CurrentValues.SetValues(updated);
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
            var existing = await context.Transitions.FirstOrDefaultAsync(
                t => t.ScopeKind == TransitionScopeKind.Playlist
                  && t.OutgoingItemId == outgoingPlaylistTrackId
                  && t.IncomingItemId == incomingPlaylistTrackId);
            if (existing != null)
            {
                context.Transitions.Remove(existing);
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    private static async Task<string?> ResolvePlaylistTrackHashAsync(AppDbContext context, Guid playlistTrackId)
    {
        var raw = await context.PlaylistTracks
            .AsNoTracking()
            .Where(t => t.Id == playlistTrackId)
            .Select(t => t.TrackUniqueHash)
            .FirstOrDefaultAsync();
        var trimmed = raw?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    internal static PlaylistTrackTransition ToDomain(TransitionEntity e)
    {
        var (presetName, type) = MapTechniqueToLegacy(e.Technique);
        return new PlaylistTrackTransition
        {
            Id = e.Id,
            PlaylistId = e.ScopeId,
            OutgoingPlaylistTrackId = e.OutgoingItemId,
            IncomingPlaylistTrackId = e.IncomingItemId,
            PresetName = presetName,
            Type = type,
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
    }

    internal static TransitionEntity ToEntity(
        PlaylistTrackTransition m,
        TransitionScopeKind scopeKind,
        string? outgoingTrackHash,
        string? incomingTrackHash,
        bool useAsPairDefault,
        Guid? existingId = null) => new()
    {
        Id = existingId ?? m.Id,
        ScopeKind = scopeKind,
        ScopeId = m.PlaylistId,
        OutgoingItemId = m.OutgoingPlaylistTrackId,
        IncomingItemId = m.IncomingPlaylistTrackId,
        OutgoingTrackHash = outgoingTrackHash,
        IncomingTrackHash = incomingTrackHash,
        UseAsPairDefault = useAsPairDefault,
        Technique = TransitionEntity.MapLegacyTechnique(m.PresetName, m.Type.ToString()),
        DurationBars = m.DurationBars,
        SourceTriggerSeconds = m.SourceTriggerSeconds,
        TargetTriggerSeconds = m.TargetTriggerSeconds,
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
        Origin = string.Equals(m.PresetName, "Auto", StringComparison.OrdinalIgnoreCase)
            ? TransitionOrigin.AutoKept
            : TransitionOrigin.Manual,
        UpdatedAtUtc = m.UpdatedAtUtc,
    };

    internal static (string PresetName, TransitionType Type) MapTechniqueToLegacy(string? technique)
    {
        var t = technique?.Trim();
        return t switch
        {
            "Auto" => ("Auto", TransitionType.Crossfade),
            "Crossfade" => ("Fade", TransitionType.Crossfade),
            "FilterSweep" => ("Rise", TransitionType.FilterSweep),
            "EqSwap" => ("Blend", TransitionType.Crossfade),
            "WaveDuck" => ("Wave", TransitionType.Crossfade),
            "EchoOut" => ("Melt", TransitionType.EchoOut),
            "Loop" => ("DoubleDrop", TransitionType.DoubleDrop),
            "Cut" => ("Cut", TransitionType.Cut),
            _ when !string.IsNullOrWhiteSpace(t) => (t!, TransitionType.Crossfade),
            _ => ("Auto", TransitionType.Crossfade),
        };
    }
}
