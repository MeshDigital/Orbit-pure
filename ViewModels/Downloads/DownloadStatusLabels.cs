using SLSKDONET.Models;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// The one status-filter strip on the Download Center: All | Active | Queued | Failed | Completed.
/// </summary>
public enum DownloadStatusFilter
{
    All,
    Active,
    Queued,
    Failed,
    Completed,
}

/// <summary>Semantic tone of a row's status dot (maps 1:1 to the status-dot style classes).</summary>
public enum DownloadStatusTone
{
    Neutral,
    Accent,
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Short, cell-sized labels for download states. Rows show these (2–3 words, never a sentence);
/// the long form (<see cref="DownloadFailureReasonExtensions.ToDisplayMessage"/>, raw error text)
/// goes in the tooltip and the details panel.
/// </summary>
public static class DownloadStatusLabels
{
    public static string ToShortLabel(this DownloadFailureReason reason, bool hasRejectionDetails = false) => reason switch
    {
        DownloadFailureReason.NoSearchResults => "No results",
        DownloadFailureReason.AllResultsRejectedQuality => "Quality too low",
        DownloadFailureReason.AllResultsRejectedFormat => "Wrong format",
        DownloadFailureReason.AllResultsBlacklisted => "Peers blocked",
        DownloadFailureReason.TransferFailed => "Transfer failed",
        DownloadFailureReason.TransferCancelled => "Peer cancelled",
        DownloadFailureReason.FileVerificationFailed => "Corrupt file",
        DownloadFailureReason.SonicIntegrityFailed => "Fake lossless",
        DownloadFailureReason.AtomicRenameFailed => "Rename failed",
        DownloadFailureReason.MaxRetriesExceeded => "Retries used up",
        DownloadFailureReason.NetworkError => "Network error",
        DownloadFailureReason.Timeout => "Timed out",
        DownloadFailureReason.DiskFull => "Disk full",
        DownloadFailureReason.PermissionDenied => "No permission",
        DownloadFailureReason.RemoteQueueDenied => "Peer queue full",
        DownloadFailureReason.RemoteAccessDenied => "Access denied",
        DownloadFailureReason.PeerRejected => "Peer rejected",
        DownloadFailureReason.UserCancelled => "Cancelled",
        DownloadFailureReason.Interrupted => "Interrupted",
        DownloadFailureReason.DiscoveryTimeout => "Search timed out",
        _ => hasRejectionDetails ? "All results rejected" : "Failed",
    };

    /// <summary>Which strip segment a row status belongs to.</summary>
    public static bool Matches(DownloadRowStatus status, DownloadStatusFilter filter) => filter switch
    {
        DownloadStatusFilter.Active => status is DownloadRowStatus.Searching or DownloadRowStatus.Downloading or DownloadRowStatus.Verifying,
        DownloadStatusFilter.Queued => status is DownloadRowStatus.Queued,
        DownloadStatusFilter.Failed => status is DownloadRowStatus.Failed or DownloadRowStatus.Cancelled,
        DownloadStatusFilter.Completed => status is DownloadRowStatus.Completed,
        _ => true,
    };
}
