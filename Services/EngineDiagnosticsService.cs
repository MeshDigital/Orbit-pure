using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Structured, queryable audit trail for the import/search/library pipeline — the "Engine
/// Diagnostics" feature: what a pasted line became, what it was cleaned into, what got added to a
/// playlist, what was searched, what candidates were evaluated, and why a search did/didn't
/// resolve to a match. Backed by the durable <c>EngineDiagnosticEvents</c> table (see
/// <see cref="EngineDiagnosticEventEntity"/>) rather than free-text log files, since the goal is
/// cross-track/cross-time analysis ("show me every search this week that found nothing"), which a
/// SQL table answers and log files don't. Also publishes on <see cref="IEventBus"/> for a live feed,
/// mirroring <see cref="NetworkActivityMonitor"/>'s shape.
///
/// This is always-on backend auditing, not a user-facing feature toggle — there is deliberately no
/// config flag to disable it, so the trail can be trusted to actually be there when something needs
/// diagnosing. It's isolated behind this one class + one table specifically so it stays easy to
/// strip out or repurpose later (e.g. into a "send diagnostics" bug-report export) without touching
/// the pipeline code that calls it.
///
/// Never throws back into a caller — a diagnostics write failure must not break a real
/// download/import. All public methods swallow and log their own exceptions.
/// </summary>
public sealed class EngineDiagnosticsService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<EngineDiagnosticsService> _logger;

    public EngineDiagnosticsService(
        IDbContextFactory<AppDbContext> dbFactory,
        IEventBus eventBus,
        ILogger<EngineDiagnosticsService> logger)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        _logger = logger;
    }

    public Task LogImportLineAsync(Guid? playlistId, ImportLineAuditEntry entry, CancellationToken ct = default)
    {
        var summary = entry.Outcome == ImportLineOutcome.Kept
            ? $"Line {entry.LineNumber}: \"{entry.Artist} - {entry.Title}\""
            : $"Line {entry.LineNumber}: {entry.Outcome}";

        return LogAsync(EngineDiagnosticEventType.ImportLine, trackHash: null, playlistId, query: null, summary, entry, ct);
    }

    public Task LogTracksAddedToPlaylistAsync(Guid playlistId, int count, string playlistTitle, CancellationToken ct = default) =>
        LogAsync(EngineDiagnosticEventType.TracksAddedToPlaylist, trackHash: null, playlistId, query: null,
            $"Added {count} track(s) to \"{playlistTitle}\"", details: new { count, playlistTitle }, ct);

    public Task LogSearchDispatchedAsync(string? trackHash, string query, string lane, CancellationToken ct = default) =>
        LogAsync(EngineDiagnosticEventType.SearchDispatched, trackHash, playlistId: null, query, $"Dispatched [{lane}] '{query}'", details: null, ct);

    public Task LogSearchCandidateAsync(string? trackHash, string? query, string action, string reason, string? peer = null, object? details = null, CancellationToken ct = default) =>
        LogAsync(EngineDiagnosticEventType.SearchCandidateEvaluated, trackHash, playlistId: null, query, $"{action}: {reason}" + (peer != null ? $" (peer: {peer})" : ""), details, ct);

    public Task LogSearchResolvedAsync(string? trackHash, string? query, string summary, object? details = null, CancellationToken ct = default) =>
        LogAsync(EngineDiagnosticEventType.SearchResolved, trackHash, playlistId: null, query, summary, details, ct);

    private async Task LogAsync(string eventType, string? trackHash, Guid? playlistId, string? query, string summary, object? details, CancellationToken ct)
    {
        try
        {
            var entity = new EngineDiagnosticEventEntity
            {
                EventType = eventType,
                TrackHash = trackHash,
                PlaylistId = playlistId,
                Query = query,
                Summary = summary,
                DetailsJson = details == null ? null : JsonSerializer.Serialize(details),
            };

            await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                db.EngineDiagnosticEvents.Add(entity);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            _eventBus.Publish(new EngineDiagnosticEvent(entity.TimestampUtc, entity.EventType, entity.TrackHash, entity.PlaylistId, entity.Query, entity.Summary));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Diagnostics must never break the pipeline they're observing.
            _logger.LogWarning(ex, "[EngineDiagnostics] Failed to record {EventType} event", eventType);
        }
    }

    /// <summary>Newest-first recent events, optionally filtered by type and/or playlist, for seeding a UI panel on open.</summary>
    public async Task<IReadOnlyList<EngineDiagnosticEventEntity>> GetRecentAsync(
        string? eventType = null, Guid? playlistId = null, int take = 200, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var query = db.EngineDiagnosticEvents.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(eventType))
                query = query.Where(e => e.EventType == eventType);
            if (playlistId.HasValue)
                query = query.Where(e => e.PlaylistId == playlistId.Value);

            return await query
                .OrderByDescending(e => e.TimestampUtc)
                .Take(Math.Clamp(take, 1, 2000))
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EngineDiagnostics] Failed to query recent events");
            return Array.Empty<EngineDiagnosticEventEntity>();
        }
    }
}
