using System;

namespace SLSKDONET.Models;

/// <summary>
/// Represents a folder used to organize playlists into a nested tree.
/// Folders can contain playlists and other folders (via ParentFolderId).
/// </summary>
public class PlaylistFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Folder";
    public Guid? ParentFolderId { get; set; }
    public int SortOrder { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
