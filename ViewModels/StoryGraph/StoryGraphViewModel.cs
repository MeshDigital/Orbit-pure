using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Services.StoryGraph;

namespace SLSKDONET.ViewModels.StoryGraph;

/// <summary>
/// Slice K5: view model for the Set Projects Story Graph canvas. Loads a SetProject's slots +
/// <see cref="SLSKDONET.Data.Entities.SetStoryEdgeEntity"/> branches, computes the
/// <see cref="StoryGraphLayout"/> geometry, and exposes selection for the inspector.
/// </summary>
public sealed class StoryGraphViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    private StoryGraphLayoutResult? _layout;
    public StoryGraphLayoutResult? Layout
    {
        get => _layout;
        private set => this.RaiseAndSetIfChanged(ref _layout, value);
    }

    private Guid? _selectedSlotId;
    public Guid? SelectedSlotId
    {
        get => _selectedSlotId;
        set => this.RaiseAndSetIfChanged(ref _selectedSlotId, value);
    }

    public StoryGraphViewModel(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task LoadSetAsync(Guid setProjectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var set = await db.SetLists
            .Include(s => s.Tracks)
            .FirstOrDefaultAsync(s => s.Id == setProjectId)
            .ConfigureAwait(false);

        if (set is null)
        {
            Layout = null;
            return;
        }

        var edges = await db.SetStoryEdges
            .Where(e => e.SetProjectId == setProjectId)
            .ToListAsync()
            .ConfigureAwait(false);

        Layout = StoryGraphLayout.Compute(set.Tracks.ToList(), edges);
    }
}
