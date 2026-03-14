using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Repositories;

public class EnrichmentTaskRepository : IEnrichmentTaskRepository
{
    private readonly ILogger<EnrichmentTaskRepository> _logger;

    public EnrichmentTaskRepository(ILogger<EnrichmentTaskRepository> logger)
    {
        _logger = logger;
    }

    public async Task QueueTaskAsync(string trackId, Guid? albumId = null)
    {
        using var context = new AppDbContext();
        
        // 1. Check if track is already enriched in the main Tracks table
        var alreadyEnriched = await context.Tracks
            .AnyAsync(t => t.GlobalId == trackId && t.IsEnriched && !string.IsNullOrEmpty(t.SpotifyTrackId));
        
        if (alreadyEnriched) return;

        try
        {
            // Idempotency check: Don't queue if already queued or processing
            var exists = await context.EnrichmentTasks
                .AnyAsync(t => t.TrackId == trackId && 
                               (t.Status == EnrichmentStatus.Queued || t.Status == EnrichmentStatus.Processing));

            if (exists) return;

            var task = new EnrichmentTaskEntity
            {
                Id = Guid.NewGuid(),
                TrackId = trackId,
                AlbumId = albumId,
                Status = EnrichmentStatus.Queued,
                CreatedAt = DateTime.UtcNow
            };

            context.EnrichmentTasks.Add(task);
            await context.SaveChangesAsync();
            _logger.LogDebug("Queued enrichment task for track {TrackId}", trackId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue enrichment task for {TrackId}", trackId);
        }
    }

    public async Task<EnrichmentTaskEntity?> GetNextPendingTaskAsync()
    {
        using var context = new AppDbContext();
        
        var task = await context.EnrichmentTasks
            .Where(t => t.Status == EnrichmentStatus.Queued)
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        return task;
    }

    public async Task MarkProcessingAsync(Guid taskId)
    {
        using var context = new AppDbContext();
        var task = await context.EnrichmentTasks.FindAsync(taskId);
        if (task != null)
        {
            task.Status = EnrichmentStatus.Processing;
            task.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }

    public async Task MarkCompletedAsync(Guid taskId)
    {
        using var context = new AppDbContext();
        var task = await context.EnrichmentTasks.FindAsync(taskId);
        if (task != null)
        {
            task.Status = EnrichmentStatus.Completed;
            task.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }

    public async Task MarkFailedAsync(Guid taskId, string error)
    {
        using var context = new AppDbContext();
        var task = await context.EnrichmentTasks.FindAsync(taskId);
        if (task != null)
        {
            task.Status = EnrichmentStatus.Failed;
            task.ErrorMessage = error;
            task.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }
}
