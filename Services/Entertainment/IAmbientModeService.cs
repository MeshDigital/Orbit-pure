using SLSKDONET.Models.Entertainment;

namespace SLSKDONET.Services.Entertainment;

/// <summary>
/// Controls ORBIT's Ambient Mode — the passive, atmospheric visual state
/// that activates when the user is idle or the player has been paused for a long time.
/// </summary>
public interface IAmbientModeService
{
    /// <summary>Gets whether Ambient Mode is currently active.</summary>
    bool IsActive { get; }

    /// <summary>Gets the current Ambient Mode configuration.</summary>
    AmbientModeConfig Config { get; }

    /// <summary>Raised when Ambient Mode activates or deactivates.</summary>
    event EventHandler<bool> ActiveChanged;

    /// <summary>Manually activates Ambient Mode.</summary>
    void Activate();

    /// <summary>Manually deactivates Ambient Mode.</summary>
    void Deactivate();

    /// <summary>Toggles Ambient Mode on/off.</summary>
    void Toggle();

    /// <summary>
    /// Notifies the service that the user performed an interaction,
    /// resetting the idle timer.
    /// </summary>
    void NotifyUserActivity();

    /// <summary>
    /// Notifies the service that playback state has changed.
    /// </summary>
    /// <param name="isPlaying">True if audio is currently playing.</param>
    void NotifyPlaybackState(bool isPlaying);

    /// <summary>Updates the Ambient Mode configuration.</summary>
    void UpdateConfig(AmbientModeConfig config);
}
