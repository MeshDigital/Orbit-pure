using System;

namespace SLSKDONET.Events;

/// <summary>
/// The Library's selected playlist changed (null/empty id: no real playlist, e.g. All Tracks).
/// Sent on ReactiveUI's MessageBus; the Discover tab follows it.
/// </summary>
public sealed record PlaylistContextChangedEvent(Guid? PlaylistId, string? Title);

/// <summary>Open the CONTEXT sidepanel's Discover tab for a playlist.</summary>
public sealed record OpenPlaylistDiscoverEvent(Guid PlaylistId, string Title);
