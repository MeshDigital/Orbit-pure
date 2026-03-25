namespace SLSKDONET.Models.Entertainment;

/// <summary>
/// Configuration for ORBIT's Ambient Mode — a slow, meditative atmospheric state
/// that activates during user idle time, long pauses, or on explicit toggle.
/// </summary>
public sealed class AmbientModeConfig
{
    /// <summary>
    /// Duration of user inactivity (in seconds) before Ambient Mode auto-activates.
    /// Default: 120 seconds (2 minutes).
    /// </summary>
    public double IdleTimeoutSeconds { get; set; } = 120.0;

    /// <summary>
    /// Duration the player must be paused (in seconds) before Ambient Mode activates.
    /// Default: 180 seconds (3 minutes).
    /// </summary>
    public double PausedTimeoutSeconds { get; set; } = 180.0;

    /// <summary>
    /// Whether floating metadata (artist/title) should drift across the screen.
    /// </summary>
    public bool ShowFloatingMetadata { get; set; } = true;

    /// <summary>
    /// Speed multiplier for animations in Ambient Mode (0.1 = very slow, 1.0 = normal).
    /// </summary>
    public double AnimationSpeed { get; set; } = 0.2;

    /// <summary>
    /// Target opacity for the UI chrome in Ambient Mode (0 = fully hidden, 1 = fully visible).
    /// </summary>
    public double UiChromeOpacity { get; set; } = 0.0;

    /// <summary>
    /// Whether to dim the album art in Ambient Mode for a softer look.
    /// </summary>
    public bool DimArtwork { get; set; } = true;

    /// <summary>
    /// Artwork dimming opacity when <see cref="DimArtwork"/> is enabled (0.0–1.0).
    /// </summary>
    public double ArtworkDimOpacity { get; set; } = 0.4;
}
