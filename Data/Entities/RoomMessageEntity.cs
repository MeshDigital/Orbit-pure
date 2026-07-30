using System;
using System.ComponentModel.DataAnnotations;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// A single Soulseek chat-room message. Room membership/roster is intentionally NOT persisted —
/// it's live, server-authoritative state refetched via JoinRoomAsync/RoomJoined each session,
/// same as how the app already treats other live state (transfer progress) as ephemeral.
/// </summary>
public class RoomMessageEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(256)]
    public string RoomName { get; set; } = string.Empty;

    [Required, MaxLength(128)]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Locally-stamped receipt time — the Soulseek protocol does not include a server timestamp
    /// for room messages (confirmed via the library's RoomMessageReceivedEventArgs, which only
    /// carries RoomName/Username/Message).
    /// </summary>
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public bool IsOutgoing { get; set; }
}
