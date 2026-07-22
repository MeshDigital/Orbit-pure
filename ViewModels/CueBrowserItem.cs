namespace SLSKDONET.ViewModels;

/// <summary>
/// Lightweight entry for the Cue Forge playlist browser.
/// Replaces the heavy PlaylistTrackViewModel — only carries what the browser needs.
/// </summary>
public sealed class CueBrowserItem
{
    public string GlobalId { get; init; } = "";
    public string Title    { get; init; } = "";
    public string Artist   { get; init; } = "";
}
