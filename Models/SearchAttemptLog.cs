namespace SLSKDONET.Models;

/// <summary>
/// Diagnostic log entry for a single search attempt.
/// Captures what was searched, what was found, and why results were rejected.
/// </summary>
public class SearchAttemptLog
{
    public string QueryString { get; set; } = "";
    public int ResultsCount { get; set; }
    public int RejectedByQuality { get; set; }
    public int RejectedByFormat { get; set; }
    public int RejectedByBlacklist { get; set; }
    public int RejectedByForensics { get; set; } // Phase 14: Operation Forensic Core
    
    
    // Fix: Added SearchScore to resolve build error in DownloadDiscoveryService
    public double SearchScore { get; set; }
    
    /// <summary>
    /// True if the search was aborted due to the global discovery timeout (90s).
    /// </summary>
    public bool TimedOut { get; set; }
    
    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Top 3 Rejected Results: Details for the highest-scoring results that were rejected.
    /// Ordered by rank (best first). Focus diagnostic attention on [0] = the #1 choice that failed.
    /// Enables UX like: "Best result: 192kbps MP3 from @User123 (Required: 320kbps)"
    /// </summary>
    public List<RejectedResult> Top3RejectedResults { get; set; } = new();
    
    /// <summary>
    /// Human-readable summary of this search attempt.
    /// Example: "Found 20 results, top 3 rejected: Quality (192kbps < 320kbps), Format (MP3 ≠ FLAC), Quality (128kbps)"
    /// </summary>
    public string GetSummary()
    {
        if (ResultsCount == 0)
            return "No results found";
            
        if (!Top3RejectedResults.Any())
            return $"Found {ResultsCount} results (no quality candidates)";
            
        var topReasons = string.Join(", ", Top3RejectedResults.Select(r => r.ShortReason));
        return $"Found {ResultsCount} results, top 3 rejected: {topReasons}";
    }
}

/// <summary>
/// Represents a rejected search result with full diagnostic context.
/// Provides actionable feedback about what was found and why it wasn't suitable.
/// </summary>
public class RejectedResult
{
    public int Rank { get; set; } // 1-based rank from search scoring
    public string Username { get; set; } = "";
    public int Bitrate { get; set; }
    public string Format { get; set; } = "";
    public long FileSize { get; set; }
    public string Filename { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public double SearchScore { get; set; } // ResultSorter score
    public string? ScoreBreakdown { get; set; } // Phase 1.1: Detailed points breakdown
    
    /// <summary>
    /// Detailed rejection reason for logging/debugging.
    /// Example: "Bitrate 192kbps below minimum threshold 320kbps"
    /// </summary>
    public string RejectionReason { get; set; } = "";
    
    /// <summary>
    /// Short form for compact UI display.
    /// Example: "Quality (192 < 320)"
    /// </summary>
    public string ShortReason { get; set; } = "";
    
    public override string ToString()
    {
        return $"#{Rank}: {Bitrate}kbps {Format} from @{Username} (Score: {SearchScore:F1}) - {RejectionReason}";
    }
    
    public Avalonia.Media.IBrush ReasonColor
    {
        get
        {
            if (string.IsNullOrEmpty(ShortReason)) return Avalonia.Media.Brushes.Gray;
            
            if (ShortReason.Contains("Bitrate") || RejectionReason.Contains("Bitrate")) return Avalonia.Media.Brushes.Orange;
            if (ShortReason.Contains("Length") || RejectionReason.Contains("Length")) return Avalonia.Media.Brushes.Yellow;
            if (ShortReason.Contains("Score") || RejectionReason.Contains("Score")) return Avalonia.Media.Brushes.IndianRed; 
            
            return Avalonia.Media.Brushes.PaleVioletRed;
        }
    }

    /// <summary>
    /// Phase 1.0: Color for the search score progress bar.
    /// Green = close to threshold (70), Red = far from it.
    /// </summary>
    public Avalonia.Media.IBrush ScoreColor
    {
        get
        {
            if (SearchScore >= 60) return Avalonia.Media.Brushes.LimeGreen;
            if (SearchScore >= 40) return Avalonia.Media.Brushes.Orange;
            return Avalonia.Media.Brushes.IndianRed;
        }
    }
}

