using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;

namespace SLSKDONET.Services.Library.Maintenance;

/// <summary>
/// Entry point for "Undo last maintenance run" (Settings → Maintenance): finds the most recent
/// completed, not-yet-reverted run and reverts both its tag changes and its playlist changes.
/// </summary>
public sealed class MaintenanceUndoService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly TagChangeService _tagChanges;
    private readonly StylePlaylistApplier _playlistApplier;
    private readonly ILogger<MaintenanceUndoService> _logger;

    public MaintenanceUndoService(
        IDbContextFactory<AppDbContext> dbFactory,
        TagChangeService tagChanges,
        StylePlaylistApplier playlistApplier,
        ILogger<MaintenanceUndoService> logger)
    {
        _dbFactory = dbFactory;
        _tagChanges = tagChanges;
        _playlistApplier = playlistApplier;
        _logger = logger;
    }

    public async Task<Guid?> GetLastRevertableRunIdAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var id = await db.MaintenanceRuns.AsNoTracking()
            .Where(r => r.Status == "Completed" && r.RevertedAt == null)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return id is null ? null : Guid.Parse(id);
    }

    /// <summary>Reverts the most recent revertable run. Returns false when there is nothing to undo.</summary>
    public async Task<bool> UndoLastAsync(CancellationToken ct = default)
    {
        var runId = await GetLastRevertableRunIdAsync(ct).ConfigureAwait(false);
        if (runId is null)
            return false;

        await _tagChanges.RevertRunAsync(runId.Value, ct).ConfigureAwait(false);
        await _playlistApplier.RevertAsync(runId.Value, ct).ConfigureAwait(false);
        _logger.LogInformation("[Maintenance] Undid last maintenance run {RunId}", runId);
        return true;
    }
}
