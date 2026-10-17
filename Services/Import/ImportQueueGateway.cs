using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Import;

/// <summary>
/// The two things the import preview needs from the download system: commit a playlist, and ask
/// whether a track is already being downloaded. A seam so the preview can be unit-tested without
/// a live <see cref="DownloadManager"/>.
/// </summary>
public interface IImportQueueGateway
{
    Task<ProjectQueueResult> QueueProjectAsync(PlaylistJob job);
    bool IsInFlight(string? spotifyTrackId, string? artist, string? title);
}

internal sealed class DownloadManagerImportQueueGateway : IImportQueueGateway
{
    private readonly DownloadManager? _downloadManager;

    public DownloadManagerImportQueueGateway(DownloadManager? downloadManager)
    {
        _downloadManager = downloadManager;
    }

    public Task<ProjectQueueResult> QueueProjectAsync(PlaylistJob job)
    {
        if (_downloadManager == null)
            throw new System.InvalidOperationException("The download manager is not available.");
        return _downloadManager.QueueProject(job);
    }

    public bool IsInFlight(string? spotifyTrackId, string? artist, string? title)
        => _downloadManager?.IsTrackInFlight(spotifyTrackId, artist, title) ?? false;
}
