using SLSKDONET.Models.Entertainment;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services.Entertainment;

/// <summary>
/// Provides ORBIT's Flow Mode — smart auto-mixing that selects upcoming tracks
/// based on BPM, key, energy, and mood for a continuous DJ-like experience.
/// </summary>
public interface IFlowModeService
{
    /// <summary>Gets the current runtime state of Flow Mode.</summary>
    FlowModeState State { get; }

    /// <summary>Raised when the Flow Mode state changes.</summary>
    event EventHandler<FlowModeState> StateChanged;

    /// <summary>Activates Flow Mode with the given preset and optional energy nudge.</summary>
    void Activate(VibePreset preset = VibePreset.SilkySmooth, double energyNudge = 0.0);

    /// <summary>Deactivates Flow Mode.</summary>
    void Deactivate();

    /// <summary>Toggles Flow Mode on/off using the last-used preset.</summary>
    void Toggle();

    /// <summary>
    /// Updates the energy nudge applied to upcoming track selection.
    /// </summary>
    /// <param name="nudge">-1.0 = much more chill, +1.0 = much more energetic.</param>
    void SetEnergyNudge(double nudge);

    /// <summary>Changes the active Flow preset without deactivating.</summary>
    void SetPreset(VibePreset preset);

    /// <summary>
    /// Rebuilds the flow path using the current library and active track context.
    /// Should be called when the current track changes.
    /// </summary>
    /// <param name="currentTrackHash">Unique hash of the track now playing.</param>
    /// <param name="candidateHashes">Hashes of tracks available for flow selection.</param>
    Task RebuildFlowPathAsync(
        string currentTrackHash,
        IEnumerable<string> candidateHashes,
        CancellationToken cancellationToken = default);
}
