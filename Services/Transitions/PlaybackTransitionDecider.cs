using System;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Transitions;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Repositories;

namespace SLSKDONET.Services.Transitions;

/// <summary>Which rule decided how a pair mixes during playback (for logging and MixBenchmark #599).</summary>
public enum PlaybackTransitionPath
{
    /// <summary>A transition the user saved for this playlist pair, or the pair's earlier mix.</summary>
    Saved,
    /// <summary>The structure plan (<see cref="TransitionPlanner"/>): sections, phrases, drops.</summary>
    Planned,
    /// <summary>Not mix-ready (no analysis, no grid, or tempo more than 6 % apart): a short echo-out.
    /// Without a plan it has no trigger points, so the player starts it near the file's end.</summary>
    EchoOutFallback,
}

/// <summary>One side of a pair, as the playback queue knows it.</summary>
public sealed record PlaybackPairTrack(
    Guid ItemId,
    Guid PlaylistId,
    string? TrackHash,
    string? CamelotKey,
    double? Energy,
    double? FileBpm);

/// <summary>What playback attaches to the incoming deck (<see cref="IAudioPlayerService.SetPendingTransitionForNext"/>).</summary>
public sealed record PlaybackTransitionDecision(
    PlaybackTransitionPath Path,
    TransitionModel Model,
    string PresetName,
    double? SourceTriggerSeconds,
    double? TargetTriggerSeconds,
    double IncomingBpm,
    double? OutgoingBpm,
    ResolvedTransitionSource? ResolvedSource,
    TransitionPlan? Plan,
    TrackStructure? OutgoingStructure,
    TrackStructure? IncomingStructure);

/// <summary>
/// Decides how real playback mixes one pair: a saved transition first, then the echo-out fallback
/// for pairs that aren't mix-ready, then the structure plan. Moved out of
/// <c>PlayerViewModel.AttachSavedTransitionAsync</c> with the same behaviour, so MixBenchmark (#599)
/// scores exactly what playback plays.
/// <para>The view model also had an older cue-point suggestion path after these. It became
/// unreachable when the echo-out fallback arrived (#528): <see cref="Timeline.TransitionPresetLibrary.RequiresUnreadyFallback"/>
/// is true whenever there is no plan, so it was dropped here rather than carried over as dead code.</para>
/// </summary>
public sealed class PlaybackTransitionDecider
{
    private readonly ITransitionResolver? _resolver;
    private readonly ITransitionRepository? _transitionRepository;
    private readonly TransitionPlanService? _planService;

    public PlaybackTransitionDecider(
        ITransitionResolver? resolver,
        ITransitionRepository? transitionRepository = null,
        TransitionPlanService? planService = null)
    {
        _resolver = resolver;
        _transitionRepository = transitionRepository;
        _planService = planService;
    }

    /// <summary>True when there is something to decide with (a resolver or a transition store).</summary>
    public bool CanDecide => _resolver != null || _transitionRepository != null;

    public async Task<PlaybackTransitionDecision> DecideAsync(
        PlaybackPairTrack outgoing, PlaybackPairTrack incoming, CancellationToken ct = default)
    {
        var bpm = incoming.FileBpm is > 0 ? incoming.FileBpm.Value : 128.0;
        double? outgoingBpm = outgoing.FileBpm is > 0 ? outgoing.FileBpm : null;

        var score = Playlist.TrackPairCompatibilityScorer.Score(
            outgoing.CamelotKey, incoming.CamelotKey, outgoing.Energy, incoming.Energy);

        PlaylistTrackTransition? saved = null;
        ResolvedTransitionSource? resolvedSource = null;
        (TransitionPlan Plan, TrackStructure Outgoing, TrackStructure Incoming)? planned = null;

        if (_resolver != null)
        {
            var resolved = await _resolver.ResolveAsync(
                TransitionScopeKind.Playlist,
                outgoing.PlaylistId,
                outgoing.ItemId,
                incoming.ItemId,
                outgoing.TrackHash,
                incoming.TrackHash,
                score.CombinedScore,
                ct: ct).ConfigureAwait(false);

            saved = resolved.SavedTransition;
            resolvedSource = resolved.Source;
            if (resolved.AutoPlan != null && resolved.OutgoingStructure != null && resolved.IncomingStructure != null)
                planned = (resolved.AutoPlan, resolved.OutgoingStructure, resolved.IncomingStructure);
        }
        else if (_transitionRepository != null)
        {
            saved = await _transitionRepository.GetTransitionAsync(outgoing.ItemId, incoming.ItemId).ConfigureAwait(false);
            if (_planService != null
                && outgoing.TrackHash is { Length: > 0 } outHash
                && incoming.TrackHash is { Length: > 0 } inHash)
            {
                try
                {
                    planned = await _planService.PlanAsync(outHash, inHash, score.CombinedScore).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "[Mix] Transition planning failed for {Out} -> {In}", outHash, inHash);
                }
            }
        }

        // Exact analysed BPMs, which the engine uses to tempo-match the incoming deck.
        if (planned is { } p)
        {
            bpm = p.Incoming.Bpm;
            outgoingBpm = p.Outgoing.Bpm;
        }

        PlaybackTransitionDecision Make(PlaybackTransitionPath path, TransitionModel model, string preset, double? source, double? target) =>
            new(path, model, preset, source, target, bpm, outgoingBpm, resolvedSource,
                planned?.Plan, planned?.Outgoing, planned?.Incoming);

        if (saved != null)
            return Make(PlaybackTransitionPath.Saved, saved.ToTransitionModel(), saved.PresetName,
                saved.SourceTriggerSeconds, saved.TargetTriggerSeconds);

        if (Timeline.TransitionPresetLibrary.RequiresUnreadyFallback(planned?.Outgoing, planned?.Incoming, outgoingBpm, bpm))
        {
            return Make(PlaybackTransitionPath.EchoOutFallback, Timeline.TransitionPresetLibrary.BuildUnreadyFallback(), "EchoOut",
                planned?.Plan.SourceTriggerSeconds, planned?.Plan.TargetTriggerSeconds);
        }

        // RequiresUnreadyFallback is true whenever there is no plan, so planned is set here.
        var plan = planned!.Value.Plan;
        return Make(PlaybackTransitionPath.Planned,
            Timeline.TransitionPresetLibrary.Build(plan.PresetName, score, plan.DurationBars),
            plan.PresetName, plan.SourceTriggerSeconds, plan.TargetTriggerSeconds);
    }
}
