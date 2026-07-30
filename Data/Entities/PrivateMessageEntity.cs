using System;
using System.ComponentModel.DataAnnotations;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// A single 1:1 Soulseek private message, either sent or received. Incoming messages are
/// deduplicated on <see cref="SoulseekMessageId"/> (the library's own message ID) so a
/// server-replayed message (delivered again after a reconnect) is never persisted twice.
/// </summary>
public class PrivateMessageEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The Soulseek protocol's own message ID — present only for incoming messages (outgoing
    /// sends via SendPrivateMessageAsync don't return one). Null for outgoing rows.
    /// </summary>
    public int? SoulseekMessageId { get; set; }

    [Required, MaxLength(128)]
    public string PeerUsername { get; set; } = string.Empty;

    public bool IsOutgoing { get; set; }

    [Required]
    public string Message { get; set; } = string.Empty;

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>True when the Soulseek server redelivered this message (e.g. after a reconnect).</summary>
    public bool WasReplayed { get; set; }
}
