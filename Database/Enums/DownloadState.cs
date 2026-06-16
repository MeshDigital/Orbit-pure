namespace SLSKDONET.Database.Enums;

/// <summary>
/// Defines the track physical download states.
/// </summary>
public enum DownloadState
{
    Pending = 0,
    Searching = 1,
    Downloading = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}
