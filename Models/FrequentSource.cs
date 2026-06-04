using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Models;

/// <summary>
/// Local-only summary of peers and folders the user repeatedly downloads from.
/// No PII, IP addresses, or remote playlist contents are stored.
/// </summary>
[Table("FrequentSources")]
public sealed class FrequentSource
{
    [Required]
    [MaxLength(128)]
    public string SourceUsername { get; set; } = string.Empty;

    [Required]
    [MaxLength(1024)]
    public string FolderPath { get; set; } = string.Empty;

    public int DownloadCount { get; set; }

    public DateTime LastDownloadedAtUtc { get; set; } = DateTime.UtcNow;

    public long TotalBytesDownloaded { get; set; }

    [MaxLength(2048)]
    public string? LocalNote { get; set; }

    public bool IsFriend { get; set; }

    public bool IsPinned { get; set; }
}