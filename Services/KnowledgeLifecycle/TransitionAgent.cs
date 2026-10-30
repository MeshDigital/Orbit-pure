using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Similarity;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): the Transition Agent. On every
/// <see cref="TrackAnalysisCompletedEvent"/> it bounds the search space (BPM band + adjacent Camelot),
/// then drains the resulting candidate backlog strictly during idle time — writing
/// <see cref="TransitionCandidateEntity"/> rows for the pairs that pass the promotion threshold.
/// <para>
/// Stateless scoring lives in <see cref="KnowledgeScoringService"/>; DB access in
/// <see cref="KnowledgeLifecycleRepository"/>. This agent only orchestrates acquisition.
/// </para>
/// </summary>
public sealed class TransitionAgent : IKnowledgeAgent, IDisposable
{
    /// <summary>Minimum overall match score (0-1) a pair must reach to become a candidate.</summary>
    public const double PromotionThreshold = 0.70;

    /// <summary>Max pairs drained per idle tick, to bound per-tick work.</summary>
    public const int MaxBatchSize = 8;

    public static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);

    private readonly IEventBus _eventBus;
    private readonly ILogger<TransitionAgent> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    private readonly IDisposable _subscription;
    private readonly ConcurrentDictionary<string, byte> _seenKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<PendingPair> _backlog = new();
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _idleLoop;

    public string Name => nameof(TransitionAgent);

    private readonly record struct PendingPair(string SourceTrackId, string TargetTrackId, string AnalysisVersion);

    public TransitionAgent(
        IEventBus eventBus,
        ILogger<TransitionAgent> logger,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _eventBus = eventBus;
        _logger = logger;
        _dbFactory = dbFactory;

        _subscription = _eventBus
            .GetEvent<TrackAnalysisCompletedEvent>()
            .Subscribe(evt => _ = OnAnalysisCompletedAsync(evt));

        _idleLoop = Task.Run(() => RunIdleLoopAsync(_cts.Token));
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task StopAsync()
    {
        _cts.Cancel();
        try { await _idleLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _cts.Dispose();
        _drainGate.Dispose();
    }

    private async Task OnAnalysisCompletedAsync(TrackAnalysisCompletedEvent evt)
    {
        if (!evt.Success)
            return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            var seed = await KnowledgeLifecycleRepository.GetAudioFeaturesAsync(db, evt.TrackGlobalId).ConfigureAwait(false);
            if (seed is null)
                return;

            var library = await KnowledgeLifecycleRepository.GetCandidateFeaturesAsync(db, seed).ConfigureAwait(false);
            var candidates = TransitionCandidateFilter.SelectCandidates(seed, library);

            foreach (var candidate in candidates)
            {
                var key = TransitionCandidateFilter.IdempotencyKey(
                    seed.TrackUniqueHash, candidate.TrackUniqueHash, seed.AnalysisVersion);
                if (_seenKeys.TryAdd(key, 0))
                    _backlog.Enqueue(new PendingPair(seed.TrackUniqueHash, candidate.TrackUniqueHash, seed.AnalysisVersion));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TransitionAgent] failed to enqueue candidates for {Track}", evt.TrackGlobalId);
        }
    }

    private async Task RunIdleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(IdlePollInterval, ct).ConfigureAwait(false);
                if (!_drainGate.Wait(0))
                    continue;

                try { await DrainBacklogAsync(ct).ConfigureAwait(false); }
                finally { _drainGate.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "[TransitionAgent] idle loop error"); }
        }
    }

    private async Task DrainBacklogAsync(CancellationToken ct)
    {
        var processed = 0;
        while (processed < MaxBatchSize && _backlog.TryDequeue(out var pair) && !ct.IsCancellationRequested)
        {
            try { await ProcessPairAsync(pair).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "[TransitionAgent] failed to process {Source}->{Target}", pair.SourceTrackId, pair.TargetTrackId); }
            processed++;
        }
    }

    private async Task ProcessPairAsync(PendingPair pair)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var seed = await KnowledgeLifecycleRepository.GetAudioFeaturesAsync(db, pair.SourceTrackId).ConfigureAwait(false);
        var target = await KnowledgeLifecycleRepository.GetAudioFeaturesAsync(db, pair.TargetTrackId).ConfigureAwait(false);
        if (seed is null || target is null)
            return;

        // No section vectors for the first pass — harmony + beat + neutral sound. Section-aware
        // scoring (drop/outro/intro via SectionVectorService) is a later enrichment.
        var score = TrackMatchScorer.Compute(seed, target, embeddingCosine: 0.5);
        if (score.OverallScore < PromotionThreshold)
            return;

        var candidate = new TransitionCandidateEntity
        {
            SourceTrackId = pair.SourceTrackId,
            TargetTrackId = pair.TargetTrackId,
            IdempotencyKey = TransitionCandidateFilter.IdempotencyKey(pair.SourceTrackId, pair.TargetTrackId, pair.AnalysisVersion),
            CompatibilityScore = Math.Round(score.OverallScore * 100.0, 2),
            HarmonicScore = Math.Round(score.HarmonyScore * 100.0, 2),
            BpmScore = Math.Round(score.BeatScore * 100.0, 2),
            EnergyScore = Math.Round(TrackMatchScorer.ComputeEnergyCompatibility(seed.Energy, target.Energy) * 100.0, 2),
            PhraseScore = Math.Round(score.OutroIntroScore * 100.0, 2),
            DiscoverySource = "BoundedScan",
        };

        await KnowledgeLifecycleRepository.UpsertCandidateAsync(db, candidate).ConfigureAwait(false);
    }
}
