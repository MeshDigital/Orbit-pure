using System;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Lightweight display model for a single network call shown in the
/// Settings → Advanced → Network Activity panel.
/// </summary>
public class NetworkActivityEntryViewModel
{
    public NetworkActivityEntryViewModel(NetworkActivityEvent e)
    {
        Timestamp = e.TimestampUtc.ToLocalTime();
        Protocol = e.Protocol;
        Kind = e.Kind;
        Detail = e.Detail;
        DurationMs = e.DurationMs;
        Success = e.Success;
    }

    public DateTime Timestamp { get; }
    public string Protocol { get; }
    public string Kind { get; }
    public string Detail { get; }
    public long? DurationMs { get; }
    public bool Success { get; }

    public string TimeLabel => Timestamp.ToString("HH:mm:ss");
    public string DurationLabel => DurationMs.HasValue ? $"{DurationMs}ms" : "";

    public string ProtocolColor => Protocol switch
    {
        "Soulseek" => "#4EC9B0",
        "HTTP" => "#00D4FF",
        "Socket" => "#D7BA7D",
        "DNS" => "#C586C0",
        _ => "#888",
    };

    public string StatusColor => Success ? "#4EC9B0" : "#F44336";
}
