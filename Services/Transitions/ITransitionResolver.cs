using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Transitions;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Repositories;
using SLSKDONET.Services.Sets;

namespace SLSKDONET.Services.Transitions;

/// <summary>
/// Which step of the 3-step lookup rule answered for an outgoing→incoming pair (Epic #517 / FO1c #520).
/// </summary>
public enum ResolvedTransitionSource
{
    /// <summary>Step 1: Saved directly for this container (playlist or set) and item ids ("Yours").</summary>
    Container = 1,

    /// <summary>Step 2: Newest shared row (UseAsPairDefault=true) for this track pair ("From your earlier mix").</summary>
    PairDefault = 2,

    /// <summary>Step 3: Computed Auto plan from cues, phrases, and tempo/key fit.</summary>
    Auto = 3,
}

/// <summary>
/// Unified result of resolving what happens between track A and track B in a playlist or set.
/// </summary>
public sealed record ResolvedTransition(
    ResolvedTransitionSource Source,
    string Technique,
    PlaylistTrackTransition? SavedTransition,
    string? EarlierMixSourceName,
    bool EarlierMixIsSet,
    TransitionPlan? AutoPlan,
    TrackStructure? OutgoingStructure,
    TrackStructure? IncomingStructure);

/// <summary>
/// Single lookup rule for playback, Transitions (Flow Builder), and the Mix tab (Epic #517 / FO1c #520):
/// 1. Container row for (ScopeKind, ScopeId, OutgoingItemId, IncomingItemId) → Yours.
/// 2. Newest shared row (UseAsPairDefault=true) for (OutgoingTrackHash, IncomingTrackHash) → From your earlier mix.
/// 3. Auto plan from cues and phrases → Auto.
/// </summary>
public interface ITransitionResolver
{
    Task<ResolvedTransition> ResolveAsync(
        TransitionScopeKind scopeKind,
        Guid scopeId,
        Guid outgoingItemId,
        Guid incomingItemId,
        string? outgoingTrackHash,
        string? incomingTrackHash,
        double compatibilityScore = 0.75,
        ITransitionRepository? storeOverride = null,
        CancellationToken ct = default);
}

public sealed class TransitionResolver : ITransitionResolver
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ITransitionHistoryService _historyService;
    private readonly TransitionPlanService? _planService;
    private readonly ILogger<TransitionResolver> _logger;

    public TransitionResolver(
        IDbContextFactory<AppDbContext> dbFactory,
        ITransitionHistoryService historyService,
        ILogger<TransitionResolver> logger,
        TransitionPlanService? planService = null)
    {
        _dbFactory = dbFactory;
        _historyService = historyService;
        _logger = logger;
        _planService = planService;
    }

    public async Task<ResolvedTransition> ResolveAsync(
        TransitionScopeKind scopeKind,
        Guid scopeId,
        Guid outgoingItemId,
        Guid incomingItemId,
        string? outgoingTrackHash,
        string? incomingTrackHash,
        double compatibilityScore = 0.75,
        ITransitionRepository? storeOverride = null,
        CancellationToken ct = default)
    {
        // Step 1: Container-scoped row (exact item ids in this playlist or set)
        PlaylistTrackTransition? containerSaved = null;
        try
        {
            if (storeOverride != null)
            {
                containerSaved = await storeOverride.GetTransitionAsync(outgoingItemId, incomingItemId).ConfigureAwait(false);
            }
            else
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                var entity = await db.Transitions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        t => t.ScopeKind == scopeKind
                          && (scopeId == Guid.Empty || t.ScopeId == scopeId)
                          && t.OutgoingItemId == outgoingItemId
                          && t.IncomingItemId == incomingItemId,
                        ct)
                    .ConfigureAwait(false);
                if (entity != null)
                {
                    containerSaved = TransitionRepository.ToDomain(entity);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TransitionResolver] Container lookup failed for {Out}->{In}", outgoingItemId, incomingItemId);
        }

        // Also resolve analysed track structure when available so playback gets exact BPMs even for saved rows.
        (TransitionPlan Plan, TrackStructure Outgoing, TrackStructure Incoming)? planned = null;
        if (_planService != null
            && !string.IsNullOrWhiteSpace(outgoingTrackHash)
            && !string.IsNullOrWhiteSpace(incomingTrackHash))
        {
            try
            {
                planned = await _planService.PlanAsync(outgoingTrackHash, incomingTrackHash, compatibilityScore).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TransitionResolver] Auto plan failed for {Out}->{In}", outgoingTrackHash, incomingTrackHash);
            }
        }

        if (containerSaved != null)
        {
            var technique = TransitionEntity.MapLegacyTechnique(containerSaved.PresetName, containerSaved.Type.ToString());
            return new ResolvedTransition(
                ResolvedTransitionSource.Container,
                technique,
                containerSaved,
                EarlierMixSourceName: null,
                EarlierMixIsSet: scopeKind == TransitionScopeKind.Set,
                AutoPlan: planned?.Plan,
                OutgoingStructure: planned?.Outgoing,
                IncomingStructure: planned?.Incoming);
        }

        // Step 2: Newest shared row (UseAsPairDefault=true) for this track pair across any playlist or set
        if (!string.IsNullOrWhiteSpace(outgoingTrackHash) && !string.IsNullOrWhiteSpace(incomingTrackHash))
        {
            try
            {
                var fromHistory = await _historyService
                    .FindLatestAsync(outgoingTrackHash, incomingTrackHash, outgoingItemId, incomingItemId)
                    .ConfigureAwait(false);
                if (fromHistory != null)
                {
                    var technique = TransitionEntity.MapLegacyTechnique(
                        fromHistory.Transition.PresetName,
                        fromHistory.Transition.Type.ToString());
                    return new ResolvedTransition(
                        ResolvedTransitionSource.PairDefault,
                        technique,
                        fromHistory.Transition,
                        EarlierMixSourceName: fromHistory.SourceName,
                        EarlierMixIsSet: fromHistory.IsSet,
                        AutoPlan: planned?.Plan,
                        OutgoingStructure: planned?.Outgoing,
                        IncomingStructure: planned?.Incoming);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TransitionResolver] Pair-default lookup failed for {Out}->{In}", outgoingTrackHash, incomingTrackHash);
            }
        }

        // Step 3: Auto plan from cues and phrases
        var autoTechnique = planned?.Plan.PresetName ?? "Auto";
        return new ResolvedTransition(
            ResolvedTransitionSource.Auto,
            autoTechnique,
            SavedTransition: null,
            EarlierMixSourceName: null,
            EarlierMixIsSet: false,
            AutoPlan: planned?.Plan,
            OutgoingStructure: planned?.Outgoing,
            IncomingStructure: planned?.Incoming);
    }
}
