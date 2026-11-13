using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.Views;

namespace SLSKDONET.Services.Library.Maintenance;

/// <summary>
/// Handles <see cref="UseAsGenreRequestedEvent"/> from the Track Inspector: resolves the track's top
/// Discogs style, applies it as the Genre through a single-change maintenance run (file + DB, with
/// journal + revert), and reports the result via toast and the notification bell.
/// </summary>
public sealed class UseAsGenreService : IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly TagChangeService _tagChanges;
    private readonly NotificationCenterService _notifications;
    private readonly ILogger<UseAsGenreService> _logger;
    private readonly IDisposable _subscription;

    public UseAsGenreService(
        IEventBus eventBus,
        IDbContextFactory<AppDbContext> dbFactory,
        TagChangeService tagChanges,
        NotificationCenterService notifications,
        ILogger<UseAsGenreService> logger)
    {
        _eventBus = eventBus;
        _dbFactory = dbFactory;
        _tagChanges = tagChanges;
        _notifications = notifications;
        _logger = logger;
        _subscription = eventBus.GetEvent<UseAsGenreRequestedEvent>().Subscribe(e => _ = HandleAsync(e));
    }

    private async Task HandleAsync(UseAsGenreRequestedEvent e)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            var features = await db.AudioFeatures.AsNoTracking()
                .FirstOrDefaultAsync(f => f.TrackUniqueHash == e.TrackHash).ConfigureAwait(false);
            var entry = await db.LibraryEntries.AsNoTracking()
                .FirstOrDefaultAsync(x => x.UniqueHash == e.TrackHash).ConfigureAwait(false);

            if (features?.TopStyle is null)
            {
                _logger.LogWarning("[UseAsGenre] Track {Hash} has no top style; nothing applied.", e.TrackHash);
                return;
            }
            if (entry is null)
            {
                _logger.LogWarning("[UseAsGenre] Track {Hash} has no library entry; nothing applied.", e.TrackHash);
                return;
            }

            var (_, name) = StylePlaylistPlanner.SplitKey(features.TopStyle);

            var runId = await _tagChanges.BeginRunAsync(new MaintenanceRunOptions(), CancellationToken.None).ConfigureAwait(false);
            var proposal = new ProposedTagChange(
                Hash: e.TrackHash,
                Rule: "UseAsGenre",
                Changes: new[] { new TagPatchField(TagField.Genre, name) },
                Confidence: ProposalConfidence.High,
                Evidence: $"Top style {features.TopStyle}",
                DefaultSelected: true,
                FilePath: entry.FilePath);

            var report = await _tagChanges.ApplyAsync(runId, new[] { proposal }, null, CancellationToken.None).ConfigureAwait(false);
            await _tagChanges.CompleteRunAsync(runId, new MaintenanceRunSummary(report.Applied, report.FileWritten, report.DbOnly, report.Failed), CancellationToken.None).ConfigureAwait(false);

            if (report.Failed > 0)
            {
                _eventBus.Publish(new ToastRequestedEvent(
                    "Genre not saved",
                    $"{entry.Title}: {report.Errors.FirstOrDefault() ?? "write failed"}",
                    NotificationType.Error,
                    TimeSpan.FromSeconds(6)));
                return;
            }

            _notifications.AddMaintenanceResult($"Genre set to {name}", $"{entry.Artist} — {entry.Title}");
            _eventBus.Publish(new ToastRequestedEvent(
                "Genre set",
                $"{name} was written to {entry.Title} (file + library).",
                NotificationType.Success,
                TimeSpan.FromSeconds(6)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UseAsGenre] Failed for {Hash}", e.TrackHash);
            _eventBus.Publish(new ToastRequestedEvent("Genre not saved", ex.Message, NotificationType.Error, TimeSpan.FromSeconds(8)));
        }
    }

    public void Dispose() => _subscription.Dispose();
}
