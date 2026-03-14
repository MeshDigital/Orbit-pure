namespace SLSKDONET.Events;

/// <summary>
/// Event published to stream granular download status updates to the UI's Live Console.
/// </summary>
public record TrackDetailedStatusEvent(
    string TrackHash, 
    string Message, 
    bool IsError = false);
