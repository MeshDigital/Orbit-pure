using System;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Lightweight display model for a single Engine Diagnostics row shown in
/// Settings → Advanced → Engine Diagnostics.
/// </summary>
public class EngineDiagnosticEntryViewModel
{
    public EngineDiagnosticEntryViewModel(EngineDiagnosticEvent e)
    {
        Timestamp = e.TimestampUtc.ToLocalTime();
        EventType = e.EventType;
        TrackHash = e.TrackHash;
        Query = e.Query;
        Summary = e.Summary;
    }

    public EngineDiagnosticEntryViewModel(Data.Entities.EngineDiagnosticEventEntity e)
    {
        Timestamp = e.TimestampUtc.ToLocalTime();
        EventType = e.EventType;
        TrackHash = e.TrackHash;
        Query = e.Query;
        Summary = e.Summary;
    }

    public DateTime Timestamp { get; }
    public string EventType { get; }
    public string? TrackHash { get; }
    public string? Query { get; }
    public string Summary { get; }

    public string TimeLabel => Timestamp.ToString("HH:mm:ss");

}
