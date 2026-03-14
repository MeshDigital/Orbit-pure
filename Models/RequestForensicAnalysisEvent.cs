namespace SLSKDONET.Models;

/// <summary>
/// Event published when user requests to view a track in the Forensic Lab dashboard.
/// This triggers navigation from Library -> Analysis Mission Control.
/// </summary>
/// <param name="TrackHash">The unique hash of the track to analyze</param>
public record RequestForensicAnalysisEvent(string TrackHash);
