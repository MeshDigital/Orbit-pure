namespace SLSKDONET.Models;

/// <summary>
/// Structured audit snapshot for one orchestrated search decision cycle.
/// Captures full candidate pool and emitted winners for explainability.
/// </summary>
public sealed class SearchSelectionAudit
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string Query { get; init; } = string.Empty;
    public string NetworkQuery { get; init; } = string.Empty;
    public int BufferSeconds { get; init; }
    public int CandidateCount { get; init; }
    public int WinnerCount { get; init; }
    public int? MinBitrate { get; init; }
    public int? MaxBitrate { get; init; }
    public string[] PreferredFormats { get; init; } = [];

    public List<SearchSelectionAuditCandidate> Candidates { get; init; } = new();
    public List<SearchSelectionAuditCandidate> Winners { get; init; } = new();
}

/// <summary>
/// Per-candidate telemetry fields used for ranking explainability.
/// </summary>
public sealed class SearchSelectionAuditCandidate
{
    public string Username { get; init; } = string.Empty;
    public string Filename { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public int Bitrate { get; init; }
    public int QueuePos { get; init; }
    public int PeerSpeed { get; init; }
    public bool IsDedup { get; init; }
    public bool IsFlagged { get; init; }
    public double Rank { get; init; }
    public double? BlendMatchScore { get; init; }
    public double? BlendFitScore { get; init; }
    public double? BlendReliability { get; init; }
    public double? BlendFinalScore { get; init; }
    public string ScoreBreakdown { get; init; } = string.Empty;
}