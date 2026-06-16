using System;

namespace SLSKDONET.ViewModels.Downloads;

public class EngineLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Level { get; set; } = "INFO";
    public string Stage { get; set; } = "ENGINE";
    public string Message { get; set; } = string.Empty;
}
