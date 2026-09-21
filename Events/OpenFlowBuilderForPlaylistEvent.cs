using System;

namespace SLSKDONET.Models;

/// <summary>
/// Published by the Mix transition editor's "Open in Flow Builder" link.
/// MixTransitionViewModel only edits one adjacent track pair at a time — Flow Builder is the
/// full-option editor for the whole playlist. Picked up by MainViewModel, which navigates to
/// Flow Builder and preloads this playlist so the user doesn't land on whatever playlist Flow
/// Builder last had open.
/// </summary>
public record OpenFlowBuilderForPlaylistEvent(Guid PlaylistId);
