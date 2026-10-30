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
/// Startup/shutdown is managed by the swarm host (<see cref="KnowledgeSwarmHost"/>), not by the
/// constructor: <see cref="StartAsync"/> subscribes and starts the idle loop,
/// <see cref="StopAsync"/> cancels and releases them. Phrase-aware scoring is supplied by
/// <see cref="SectionVectorService"/> (outgoing Outro/Drop vs incoming Intro) so a full-file
/// cosine never dilutes transition quality.
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
    private readonly SectionVectorService _sectionVectors;

    private IDisposable? _subscription;
    private Task? _idleLoop;
    private readonly ConcurrentDictionary<string, byte> _seenKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<PendingPair> _backlog = new();
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    public string Name => nameof(TransitionAgent);

    private readonly record struct PendingPair(string SourceTrackId, string TargetTrackId, string AnalysisVersion);

    public TransitionAgent(
        IEventBus eventBus,
        ILogger<TransitionAgent> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        SectionVectorService sectionVectors)
    {
        _eventBus = eventBus;
        _logger = logger;
        _dbFactory = dbFactory;
        _sectionVectors = sectionVectors;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = _eventBus
            .GetEvent<TrackAnalysisCompletedEvent>()
            .Subscribe(evt => _ = OnAnalysisCompletedAsync(evt));
        _idleLoop = Task.Run(() => RunIdleLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        _subscription?.Dispose();
        _subscription = null;
        if (_idleLoop is not null)
        {
            try { await _idleLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _idleLoop = null;
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
        _drainGate.Dispose();
        _subscription?.Dispose();
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
            try { await ProcessPairAsync(pair, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "[TransitionAgent] failed to process {Source}->{Target}", pair.SourceTrackId, pair.TargetTrackId); }
            processed++;
        }
    }

    private async Task ProcessPairAsync(PendingPair pair, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var seed = await KnowledgeLifecycleRepository.GetAudioFeaturesAsync(db, pair.SourceTrackId).ConfigureAwait(false);
        var target = await KnowledgeLifecycleRepository.GetAudioFeaturesAsync(db, pair.TargetTrackId).ConfigureAwait(false);
        if (seed is null || target is null)
            return;

        // Phrase-aware sections: outgoing Outro/Drop vs incoming Intro/Drop — not full-file cosine.
        var seedOutro = await _sectionVectors.GetOutroSectionAsync(pair.SourceTrackId, ct).ConfigureAwait(false);
        var seedDrop = await _sectionVectors.GetPeakSectionAsync(pair.SourceTrackId, ct).ConfigureAwait(false);
        var targetIntro = await _sectionVectors.GetIntroSectionAsync(pair.TargetTrackId, ct).ConfigureAwait(false);
        var targetDrop = await _sectionVectors.GetPeakSectionAsync(pair.TargetTrackId, ct).ConfigureAwait(false);

        var embeddingCosine = SectionCosine(seedOutro, targetIntro);
        var score = TrackMatchScorer.Compute(
            seed, target,
            embeddingCosine,
            dropA: seedDrop,
            dropB: targetDrop,
            outroA: seedOutro,
            introB: targetIntro);

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

    /// <summary>Cosine similarity between two section embeddings; neutral 0.5 when either is missing.</summary>
    private static double SectionCosine(SectionFeatureVector? a, SectionFeatureVector? b, double fallback = 0.5)
    {
        var ea = a?.Embedding;
        var eb = b?.Embedding;
        if (ea is null || eb is null || ea.Length == 0 || eb.Length == 0 || ea.Length != eb.Length)
            return fallback;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < ea.Length; i++)
        {
            dot += ea[i] * eb[i];
            na += ea[i] * ea[i];
            nb += eb[i] * eb[i];
        }

        if (na == 0 || nb == 0)
            return fallback;
        return Math.Clamp(dot / (Math.Sqrt(na) * Math.Sqrt(nb)), 0.0, 1.0);
    }
}
