using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.Services;

/// <summary>
/// Local-only staging queue for opt-in prefetching.
/// The implementation is intentionally conservative and does not upload anything.
/// </summary>
public sealed class PrefetchService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly DatabaseService _databaseService;
    private readonly AppConfig _appConfig;
    private readonly TrackFingerprintBackfillService _fingerprintBackfillService;
    private readonly ILogger<PrefetchService> _logger;

    public PrefetchService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        DatabaseService databaseService,
        AppConfig appConfig,
        TrackFingerprintBackfillService fingerprintBackfillService,
        ILogger<PrefetchService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _databaseService = databaseService;
        _appConfig = appConfig;
        _fingerprintBackfillService = fingerprintBackfillService;
        _logger = logger;
    }

    public async Task<PrefetchQueueItem?> EnqueueAsync(string sourceUsername, string remotePath, string localStagingPath, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled() || string.IsNullOrWhiteSpace(sourceUsername) || string.IsNullOrWhiteSpace(remotePath) || string.IsNullOrWhiteSpace(localStagingPath))
            return null;

        await _databaseService.InitAsync().ConfigureAwait(false);

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var item = new PrefetchQueueItem
        {
            SourceUsername = sourceUsername,
            RemotePath = remotePath,
            LocalStagingPath = localStagingPath,
            Status = PrefetchQueueStatus.Queued,
            EnqueuedAtUtc = DateTime.UtcNow,
        };

        context.PrefetchQueueItems.Add(item);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return item;
    }

    public async Task<IReadOnlyList<PrefetchQueueItem>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled())
            return Array.Empty<PrefetchQueueItem>();

        await _databaseService.InitAsync().ConfigureAwait(false);

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.PrefetchQueueItems
            .OrderBy(item => item.Status)
            .ThenBy(item => item.EnqueuedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkReadyAsync(Guid itemId, long bytesDownloaded, string? trackHash = null, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled())
            return;

        await _databaseService.InitAsync().ConfigureAwait(false);

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var item = await context.PrefetchQueueItems.FirstOrDefaultAsync(queueItem => queueItem.Id == itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
            return;

        item.Status = PrefetchQueueStatus.Ready;
        item.BytesDownloaded = Math.Max(0, bytesDownloaded);
        item.CompletedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(trackHash))
        {
            try
            {
                await _fingerprintBackfillService.BuildForTrackAsync(trackHash, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prefetch completion fingerprint backfill failed for {TrackHash}", trackHash);
            }
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled())
            return;

        await _databaseService.InitAsync().ConfigureAwait(false);

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.PrefetchQueueItems.RemoveRange(context.PrefetchQueueItems);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsEnabled()
        => _appConfig.EnableFrequentSources;
}