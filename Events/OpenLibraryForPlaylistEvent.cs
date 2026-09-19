using System;

namespace SLSKDONET.Models;

/// <summary>
/// Published by Cue Forge's "Back to Mix Transition" link (CueForgeViewModel.BackToMixTransition
/// — only enabled once SetMixTransitionOrigin has recorded where the current track was opened
/// from). Picked up by MainViewModel, which navigates to Library, selects this playlist, and
/// republishes SLSKDONET.Events.OpenMixTransitionEvent so the CONTEXT sidepanel's Mix tab reopens
/// on the exact pair the user came from — the round-trip half of MixTransitionViewModel's
/// "Fix in Cue Forge" link.
/// </summary>
public record OpenLibraryForPlaylistEvent(Guid PlaylistId, Guid OutgoingPlaylistTrackId, Guid IncomingPlaylistTrackId);
