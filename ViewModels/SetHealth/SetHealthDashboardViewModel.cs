using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.KnowledgeLifecycle;

namespace SLSKDONET.ViewModels.SetHealth;

/// <summary>
/// Slice K4: drives the Set Health dashboard. Loads a SetProject, resolves each adjacent
/// spine transition's knowledge edge, and computes the narrative/technical/risk scores via the
/// pure <see cref="SetHealthService"/>. The view only renders the report.
/// </summary>
public sealed class SetHealthDashboardViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    private SetHealthReport? _report;
    public SetHealthReport? Report { get => _report; private set => this.RaiseAndSetIfChanged(ref _report, value); }

    private double _transitionQuality;
    public double TransitionQuality { get => _transitionQuality; private set => this.RaiseAndSetIfChanged(ref _transitionQuality, value); }

    private double _energyFlow;
    public double EnergyFlow { get => _energyFlow; private set => this.RaiseAndSetIfChanged(ref _energyFlow, value); }

    private double _peakPlacement;
    public double PeakPlacement { get => _peakPlacement; private set => this.RaiseAndSetIfChanged(ref _peakPlacement, value); }

    private double _crowdRecovery;
    public double CrowdRecovery { get => _crowdRecovery; private set => this.RaiseAndSetIfChanged(ref _crowdRecovery, value); }

    public SetHealthDashboardViewModel(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task LoadAsync(Guid setProjectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setProjectId)
            .ConfigureAwait(false);
        if (set is null)
        {
            Report = null;
            return;
        }

        var ordered = set.Tracks.OrderBy(t => t.Position).ToList();
        var eventProfile = set.EventProfileId is Guid epId
            ? await db.EventProfiles.FirstOrDefaultAsync(e => e.Id == epId).ConfigureAwait(false)
            : null;

        var edgeScores = new List<double>();
        var energyTargets = new List<double>();
        var roles = new List<SetSlotRole>();

        for (var i = 0; i < ordered.Count; i++)
        {
            var slot = ordered[i];
            energyTargets.Add(slot.EnergyTarget ?? 50.0);
            roles.Add(slot.SlotRole);

            if (i > 0)
            {
                var from = ordered[i - 1];
                var edge = await db.TransitionKnowledge
                    .FirstOrDefaultAsync(e =>
                        e.SourceTrackId == from.TrackUniqueHash &&
                        e.TargetTrackId == slot.TrackUniqueHash)
                    .ConfigureAwait(false);

                edgeScores.Add(edge is null ? 50.0 : edge.CompatibilityScore);
            }
        }

        Report = SetHealthService.Compute(edgeScores, energyTargets, roles, eventProfile, preparationCompleteness: 1.0);
        TransitionQuality = SetHealthService.TransitionQuality(edgeScores);
        EnergyFlow = SetHealthService.EnergyFlow(energyTargets, eventProfile);
        PeakPlacement = SetHealthService.PeakPlacement(roles);
        CrowdRecovery = SetHealthService.CrowdRecovery(roles);
    }
}
