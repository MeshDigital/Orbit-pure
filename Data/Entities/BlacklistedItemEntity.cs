using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// "The Immune System": Represents a file hash that has been explicitly rejected by the user.
/// Search results matching these hashes will be visually blocked or hidden.
/// </summary>
[Table("BlacklistedItems")]
public class BlacklistedItemEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// MD5 or SHA1 hash of the file. Indexed for fast lookups.
    /// </summary>
    [Required]
    [MaxLength(64)] // Fits SHA256 (64 hex chars), MD5 is 32
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Why this was blocked (e.g., "Bad Rip", "Fake Upscale", "Spam", "User Blocked").
    /// </summary>
    public string Reason { get; set; } = "User Blocked";

    /// <summary>
    /// The original file name or title for context/audit.
    /// </summary>
    public string? OriginalTitle { get; set; }

    public DateTime BlockedAt { get; set; } = DateTime.UtcNow;
}
