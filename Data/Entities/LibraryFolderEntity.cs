using System;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Represents a user-configured folder to scan for music files
/// </summary>
public class LibraryFolderEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// Full path to the folder (e.g., C:\Music\DnB)
    /// </summary>
    public string FolderPath { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether this folder is currently enabled for scanning
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>
    /// When the folder was added to the library
    /// </summary>
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Last time this folder was scanned
    /// </summary>
    public DateTime? LastScannedAt { get; set; }
    
    /// <summary>
    /// Total tracks found during last scan
    /// </summary>
    public int TracksFound { get; set; }
}
