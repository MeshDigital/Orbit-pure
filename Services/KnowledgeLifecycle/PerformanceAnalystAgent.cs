using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>
/// Phase 2 (Knowledge Lifecycle): the Performance Analyst Agent. On
/// <see cref="PerformanceSessionRecordedEvent"/> it reconciles the live
/// <see cref="PerformanceSessionEntity"/> against reality and writes append-only
/// <see cref="TransitionEvidenceEntity"/> rows for every live audible — strictly where
/// <see cref="PerformanceSessionTrackEntity.AudibleChange"/> is true, so tracks that were cued but
/// never brought into the master mix produce no evidence.
/// </summary>
public sealed class PerformanceAnalystAgent : IKnowledgeAgent, IDisposable
{
    private const double LiveChoiceBaseScore = 65.0;   // a live audible is a weak positive even unrated
    private const double LiveChoiceConfidence = 0.7;
    private const double CrowdRatedConfidence = 0.9;

    private readonly IEventBus _eventBus;
    private readonly ILogger<PerformanceAnalystAgent> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    private IDisposable? _subscription;

    public string Name => nameof(PerformanceAnalystAgent);

    public PerformanceAnalystAgent(
        IEventBus eventBus,
        ILogger<PerformanceAnalystAgent> logger,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _eventBus = eventBus;
        _logger = logger;
        _dbFactory = dbFactory;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = _eventBus
            .GetEvent<PerformanceSessionRecordedEvent>()
            .Subscribe(evt => _ = OnSessionRecordedAsync(evt));
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();

    private async Task OnSessionRecordedAsync(PerformanceSessionRecordedEvent evt)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            var session = await db.PerformanceSessions
                .Include(s => s.Tracks)
                .FirstOrDefaultAsync(s => s.Id == evt.SessionId)
                .ConfigureAwait(false);
            if (session is null)
                return;

            var audibles = PerformanceSessionReconciler.ExtractAudibleTransitions(session.Tracks.ToList());

            foreach (var audible in audibles)
            {
                var type = audible.TransitionType.HasValue
                    ? (KnowledgeTransitionType)audible.TransitionType.Value
                    : KnowledgeTransitionType.Blend;

                var edge = await KnowledgeLifecycleRepository
                    .GetOrCreateKnowledgeEdgeAsync(db, audible.SourceTrackId, audible.TargetTrackId, type)
                    .ConfigureAwait(false);

                var source = audible.CrowdReaction.HasValue
                    ? TransitionEvidenceSource.CrowdReaction
                    : TransitionEvidenceSource.LivePerformance;
                var score = audible.CrowdReaction ?? LiveChoiceBaseScore;
                var confidence = audible.CrowdReaction.HasValue ? CrowdRatedConfidence : LiveChoiceConfidence;

                await KnowledgeLifecycleRepository
                    .AddEvidenceAsync(db, edge.Id, source, score, confidence, evt.SessionId)
                    .ConfigureAwait(false);
                await KnowledgeLifecycleRepository.RecomputeAsync(edge.Id, db).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "[PerformanceAnalyst] reconciled {Count} audible(s) for session {Session}",
                audibles.Count, evt.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PerformanceAnalyst] failed to reconcile session {Session}", evt.SessionId);
        }
    }
}
