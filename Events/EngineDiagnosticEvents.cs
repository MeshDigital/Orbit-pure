using System;

namespace SLSKDONET.Models;

/// <summary>
/// Published on the event bus whenever <see cref="Services.EngineDiagnosticsService"/> records a
/// row, for a live-updating diagnostics feed — mirrors <see cref="Data.Entities.EngineDiagnosticEventEntity"/>
/// minus its Id (the persisted row is the source of truth; a UI panel seeds from
/// <c>EngineDiagnosticsService.GetRecentAsync</c> then subscribes to this for live updates,
/// following the same seed-then-subscribe pattern established by NetworkActivityMonitor).
/// </summary>
public record EngineDiagnosticEvent(
    DateTime TimestampUtc,
    string EventType,
    string? TrackHash,
    Guid? PlaylistId,
    string? Query,
    string Summary);
