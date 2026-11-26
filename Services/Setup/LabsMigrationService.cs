using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;

namespace SLSKDONET.Services.Setup;

/// <summary>
/// Outcome of the one-time Labs migration check on startup (Epic #517 / #537).
/// </summary>
public sealed record LabsMigrationResult(bool EnabledLabs, bool ShouldShowNote);

/// <summary>
/// On first launch after the Focus &amp; Optimisation update (Epic #517 / #537), existing users who
/// already have saved Sets or Workstation sessions automatically keep Labs turned on and see a
/// one-time note explaining where those pages moved. Fresh installs stay with Labs off and no note.
/// </summary>
public static class LabsMigrationService
{
    public static LabsMigrationResult EvaluateAndApply(AppConfig config, bool hasSavedLabsData)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.LabsMigrationNoteShown)
        {
            return new LabsMigrationResult(EnabledLabs: false, ShouldShowNote: false);
        }

        if (!hasSavedLabsData)
        {
            return new LabsMigrationResult(EnabledLabs: false, ShouldShowNote: false);
        }

        config.LabsEnabled = true;
        config.LabsMigrationNoteShown = true;
        return new LabsMigrationResult(EnabledLabs: true, ShouldShowNote: true);
    }

    public static async Task<LabsMigrationResult> RunOnceAsync(
        AppConfig config,
        ConfigManager? configManager,
        IDbContextFactory<AppDbContext>? dbFactory,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (config.LabsMigrationNoteShown || dbFactory is null)
            return new LabsMigrationResult(false, false);

        bool hasSavedData = false;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            hasSavedData = await db.SetLists.AsNoTracking().AnyAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "[LabsMigration] Could not query SetLists; skipping Labs auto-enable check.");
            return new LabsMigrationResult(false, false);
        }

        var result = EvaluateAndApply(config, hasSavedData);
        if (result.EnabledLabs)
        {
            configManager?.Save(config);
            logger?.LogInformation("[LabsMigration] Auto-enabled Labs for existing user with saved Sets/Workstation data.");
        }

        return result;
    }
}
