using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Transitions;

/// <summary>
/// Passively logs pairs that were actually mixed during playback into <c>TransitionPlays</c>
/// (Epic #517 / #534). Never writes to <c>Transitions</c>.
/// </summary>
public interface ITransitionPlayLogService
{
    Task<Guid?> RecordMixPlayAsync(
        string? outgoingTrackHash,
        string? incomingTrackHash,
        string? technique,
        bool wasSkipped = false,
        CancellationToken ct = default);

    Task MarkSkippedIfWithinWindowAsync(
        Guid playId,
        DateTime playedAtUtc,
        DateTime skippedAtUtc,
        double skipWindowSeconds = TransitionPlayLogService.DefaultSkipWindowSeconds,
        CancellationToken ct = default);
}

/// <summary>
/// Singleton implementation of <see cref="ITransitionPlayLogService"/>. Non-throwing constructor.
/// </summary>
public sealed class TransitionPlayLogService : ITransitionPlayLogService
{
    public const double DefaultSkipWindowSeconds = 15.0;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<TransitionPlayLogService>? _logger;
    private readonly Func<DateTime> _utcNow;

    public TransitionPlayLogService(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<TransitionPlayLogService>? logger = null,
        Func<DateTime>? utcNow = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<Guid?> RecordMixPlayAsync(
        string? outgoingTrackHash,
        string? incomingTrackHash,
        string? technique,
        bool wasSkipped = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(outgoingTrackHash) || string.IsNullOrWhiteSpace(incomingTrackHash))
            return null;

        try
        {
            var entity = new TransitionPlayEntity
            {
                Id = Guid.NewGuid(),
                OutgoingTrackHash = outgoingTrackHash.Trim(),
                IncomingTrackHash = incomingTrackHash.Trim(),
                Technique = string.IsNullOrWhiteSpace(technique) ? "Auto" : technique.Trim(),
                WasSkipped = wasSkipped,
                PlayedAtUtc = _utcNow(),
            };

            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.TransitionPlays.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return entity.Id;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[TransitionPlayLog] Failed to record play for {Out} -> {In}", outgoingTrackHash, incomingTrackHash);
            return null;
        }
    }

    public async Task MarkSkippedIfWithinWindowAsync(
        Guid playId,
        DateTime playedAtUtc,
        DateTime skippedAtUtc,
        double skipWindowSeconds = DefaultSkipWindowSeconds,
        CancellationToken ct = default)
    {
        if (playId == Guid.Empty) return;
        double elapsedSeconds = (skippedAtUtc - playedAtUtc).TotalSeconds;
        if (elapsedSeconds < 0 || elapsedSeconds > skipWindowSeconds) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.TransitionPlays.FirstOrDefaultAsync(p => p.Id == playId, ct).ConfigureAwait(false);
            if (row is null || row.WasSkipped) return;

            row.WasSkipped = true;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[TransitionPlayLog] Failed to mark play {PlayId} as skipped", playId);
        }
    }
}
