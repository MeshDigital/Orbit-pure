using System;

namespace SLSKDONET.Events;

/// <summary>
/// Published when the user clicks a Mix transition badge between two adjacent playlist rows.
/// Picked up by <see cref="SLSKDONET.ViewModels.SidebarViewModel"/> to open the "Mix" tab in
/// the CONTEXT sidepanel and load the pair into <see cref="SLSKDONET.ViewModels.MixTransitionViewModel"/>.
/// </summary>
public sealed class OpenMixTransitionEvent
{
    public Guid PlaylistId { get; }
    public Guid OutgoingPlaylistTrackId { get; }
    public Guid IncomingPlaylistTrackId { get; }

    public OpenMixTransitionEvent(Guid playlistId, Guid outgoingPlaylistTrackId, Guid incomingPlaylistTrackId)
    {
        PlaylistId = playlistId;
        OutgoingPlaylistTrackId = outgoingPlaylistTrackId;
        IncomingPlaylistTrackId = incomingPlaylistTrackId;
    }
}
