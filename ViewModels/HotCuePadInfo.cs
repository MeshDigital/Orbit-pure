namespace SLSKDONET.ViewModels;

/// <summary>
/// Display model for a single hot cue pad (slots A–H).
/// Rebuilt from WorkingCues on every cue change — no INPC needed.
/// </summary>
public sealed class HotCuePadInfo
{
    public int Slot { get; set; }
    public string PadLabel { get; set; } = "";
    public string CueName { get; set; } = "";
    public string TimestampDisplay { get; set; } = "";
    public string Background { get; set; } = "#1A1A26";
    public string BorderColor { get; set; } = "#2A2A3A";
    public string Foreground { get; set; } = "#333344";
    public bool IsAssigned { get; set; }
}
