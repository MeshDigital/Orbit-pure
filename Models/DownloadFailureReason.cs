namespace SLSKDONET.Models;

/// <summary>
/// Structured failure reasons for download diagnostics.
/// Replaces generic string messages with actionable categories.
/// </summary>
public enum DownloadFailureReason
{
    None,
    NoSearchResults,
    AllResultsRejectedQuality,
    AllResultsRejectedFormat,
    AllResultsBlacklisted,
    TransferFailed,
    TransferCancelled,
    FileVerificationFailed,
    SonicIntegrityFailed,
    AtomicRenameFailed,
    MaxRetriesExceeded,
    NetworkError,
    Timeout,
    DiskFull,
    PermissionDenied,
    UserCancelled,
    Interrupted,        // App closed mid-download
    DiscoveryTimeout    // 90s search timeout
}

/// <summary>
/// Extension methods for mapping failure reasons to user-facing messages and suggestions.
/// </summary>
public static class DownloadFailureReasonExtensions
{
    public static string ToDisplayMessage(this DownloadFailureReason reason)
    {
        return reason switch
        {
            DownloadFailureReason.NoSearchResults => "No search results found",
            DownloadFailureReason.AllResultsRejectedQuality => "All results rejected: Quality too low",
            DownloadFailureReason.AllResultsRejectedFormat => "All results rejected: Wrong format",
            DownloadFailureReason.AllResultsBlacklisted => "All results rejected: Blacklisted users",
            DownloadFailureReason.TransferFailed => "Network transfer failed",
            DownloadFailureReason.TransferCancelled => "Transfer cancelled by peer",
            DownloadFailureReason.FileVerificationFailed => "File verification failed: Invalid or corrupted",
            DownloadFailureReason.SonicIntegrityFailed => "Rejected: Suspected upscale/fake detected",
            DownloadFailureReason.AtomicRenameFailed => "System error: File rename failed",
            DownloadFailureReason.MaxRetriesExceeded => "Max retry attempts exceeded",
            DownloadFailureReason.NetworkError => "Network error: Connection timeout",
            DownloadFailureReason.Timeout => "Operation timed out (stalled)",
            DownloadFailureReason.DiskFull => "System error: No space left on device",
            DownloadFailureReason.PermissionDenied => "System error: Permission denied",
            DownloadFailureReason.UserCancelled => "Cancelled by user",
            DownloadFailureReason.Interrupted => "App closed during download (Incomplete)",
            DownloadFailureReason.DiscoveryTimeout => "Search timed out after 90s",
            _ => "Unknown failure"
        };
    }

    public static string ToActionableSuggestion(this DownloadFailureReason reason)
    {
        return reason switch
        {
            DownloadFailureReason.NoSearchResults => 
                "Check your Soulseek connection or try broader search terms",
            DownloadFailureReason.AllResultsRejectedQuality => 
                "Try lowering your Bitrate threshold in Settings",
            DownloadFailureReason.AllResultsRejectedFormat => 
                "Adjust allowed formats in Settings or enable format conversion",
            DownloadFailureReason.AllResultsBlacklisted => 
                "Review your user blacklist in Settings",
            DownloadFailureReason.NetworkError => 
                "Ensure your firewall allows Soulseek connections",
            DownloadFailureReason.DiskFull => 
                "Free up disk space in your download directory",
            DownloadFailureReason.AtomicRenameFailed => 
                "Check antivirus interference or file permissions",
            DownloadFailureReason.SonicIntegrityFailed =>
                "Source file quality is suspicious. Try a different uploader",
            _ => ""
        };
    }

    /// <summary>
    /// Determines if this failure reason benefits from automatic retry.
    /// Quality/format rejections should NOT retry automatically.
    /// </summary>
    public static bool ShouldAutoRetry(this DownloadFailureReason reason)
    {
        return reason switch
        {
            DownloadFailureReason.AllResultsRejectedQuality => false,
            DownloadFailureReason.AllResultsRejectedFormat => false,
            DownloadFailureReason.AllResultsBlacklisted => false,
            DownloadFailureReason.UserCancelled => false,
            DownloadFailureReason.DiskFull => false,
            DownloadFailureReason.PermissionDenied => false,
            DownloadFailureReason.Interrupted => true,       // Should retry on next boot
            DownloadFailureReason.DiscoveryTimeout => true,  // Should retry with different timing
            _ => true
        };
    }
}
