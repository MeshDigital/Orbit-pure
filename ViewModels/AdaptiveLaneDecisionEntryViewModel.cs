using System;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Lightweight display model for adaptive discovery lane decisions.
/// </summary>
public class AdaptiveLaneDecisionEntryViewModel
{
    public AdaptiveLaneDecisionEntryViewModel(AdaptiveLaneStatusEvent e)
    {
        Timestamp = DateTime.Now;
        CurrentLanes = e.CurrentLanes;
        ActiveLanes = e.ActiveLanes;
        Reason = e.Reason;
    }

    public DateTime Timestamp { get; }
    public int CurrentLanes { get; }
    public int ActiveLanes { get; }
    public string Reason { get; }

    public string TimeLabel => Timestamp.ToString("HH:mm:ss");
    public string LaneLabel => $"Lanes {ActiveLanes}/{CurrentLanes}";
}