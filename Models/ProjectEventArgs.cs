using System;

namespace SLSKDONET.Models;

/// <summary>
/// Event args for when a new project is added to the download manager
/// </summary>
public class ProjectEventArgs : EventArgs
{
    public PlaylistJob Job { get; }
    
    public ProjectEventArgs(PlaylistJob job)
    {
        Job = job ?? throw new ArgumentNullException(nameof(job));
    }
}
