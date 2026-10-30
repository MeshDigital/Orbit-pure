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
/// Phase 2 (Knowledge Lifecycle): the Export Agent. On <see cref="SetExportRequestedEvent"/> it
/// walks the SetProject spine, resolves each adjacent pair's knowledge edge, and annotates it with
/// the DJ-filtered recommendation score (confidence × freshness × djFit). The physical
/// USB/Rekordbox write is delegated to the existing <c>Services.Export</c> /
/// <c>Services.Library.PlaylistExportService</c> path.
/// </summary>
public sealed class ExportAgent : IKnowledgeAgent, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ILogger<ExportAgent> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    private IDisposable? _subscription;

    public string Name => nameof(ExportAgent);

    public ExportAgent(
        IEventBus eventBus,
        ILogger<ExportAgent> logger,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _eventBus = eventBus;
        _logger = logger;
        _dbFactory = dbFactory;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = _eventBus
            .GetEvent<SetExportRequestedEvent>()
            .Subscribe(evt => _ = OnExportRequestedAsync(evt));
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();

    private async Task OnExportRequestedAsync(SetExportRequestedEvent evt)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            var set = await db.SetLists
                .Include(s => s.Tracks)
                .FirstOrDefaultAsync(s => s.Id == evt.SetProjectId)
                .ConfigureAwait(false);
            if (set is null)
                return;

            var fingerprint = await KnowledgeLifecycleRepository.GetOrCreateFingerprintAsync(db).ConfigureAwait(false);
            var ordered = set.Tracks.OrderBy(t => t.Position).ToList();

            var annotated = 0;
            for (var i = 1; i < ordered.Count; i++)
            {
                var from = ordered[i - 1];
                var to = ordered[i];

                var edge = await db.TransitionKnowledge
                    .FirstOrDefaultAsync(e =>
                        e.SourceTrackId == from.TrackUniqueHash &&
                        e.TargetTrackId == to.TrackUniqueHash)
                    .ConfigureAwait(false);
                if (edge is null)
                    continue;

                var djFit = DjFingerprintService.ComputeDjFit(
                    edge.TransitionType,
                    fingerprint.PreferredTransitionTypesJson,
                    fingerprint.AvoidancePatternsJson);
                var recScore = KnowledgeScoringService.ComputeRecommendationScore(edge, djFit);

                _logger.LogInformation(
                    "[ExportAgent] {From}->{To}: rec={Rec:F1} conf={Conf} fresh={Fresh:F1}",
                    from.TrackUniqueHash, to.TrackUniqueHash, recScore, edge.ConfidenceLevel, edge.Freshness);
                annotated++;
            }

            _logger.LogInformation(
                "[ExportAgent] annotated {Count} transition(s) for set {Set}",
                annotated, evt.SetProjectId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ExportAgent] failed to export set {Set}", evt.SetProjectId);
        }
    }
}
