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

    /// <summary>Deactivates Flow Mode and clears the planned path.</summary>
    void Deactivate();

    /// <summary>Toggles Flow Mode on/off using the last-used preset.</summary>
    void Toggle();

    /// <summary>
    /// Updates the energy nudge applied to upcoming track selection. The caller is responsible for
    /// rebuilding the path afterwards (the listener only mirrors state).
    /// </summary>
    /// <param name="nudge">-1.0 = much more chill, +1.0 = much more energetic.</param>
    void SetEnergyNudge(double nudge);

    /// <summary>Changes the active Flow preset without deactivating.</summary>
    void SetPreset(VibePreset preset);

    /// <summary>
    /// Rebuilds the flow path from the track that is playing and the tracks it may flow into.
    /// Cheap and synchronous on purpose: it runs on every track change, before the next deck is
    /// preloaded, and a plan that arrives late is a plan that picks the wrong track.
    /// </summary>
    /// <param name="current">The playing track, or null when playback stopped.</param>
    /// <param name="candidates">Playable tracks Flow may choose from (the queue).</param>
    /// <param name="recentlyPlayed">Hashes played in the last handful of hops, so a set doesn't loop.</param>
    void RebuildFlowPath(
        FlowCandidate? current,
        IReadOnlyList<FlowCandidate> candidates,
        IReadOnlyCollection<string>? recentlyPlayed = null);
}
