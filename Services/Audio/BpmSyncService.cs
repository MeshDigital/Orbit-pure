using System;

namespace SLSKDONET.Services.Audio;

/// <summary>Designates which deck is the master tempo reference for sync.</summary>
public enum DeckSide { A, B }

/// <summary>
/// Beat-matching and phase-alignment service for the dual-deck engine.
///
/// Beat-matching sets the slave deck's tempo so that its effective BPM equals the master's
/// effective BPM. Phase-alignment subsequently nudges the slave's playhead so that its
/// downbeats align with the master's downbeats.
/// </summary>
public sealed class BpmSyncService
{
    /// <summary>
    /// Adjusts <paramref name="slave"/>'s <see cref="DeckEngine.TempoPercent"/> so that the
    /// slave's effective BPM matches the master's effective BPM.
    /// </summary>
    /// <param name="master">The tempo-reference deck.</param>
    /// <param name="masterTrackBpm">Native (file) BPM of the master track.</param>
    /// <param name="slave">The deck whose tempo will be adjusted.</param>
    /// <param name="slaveTrackBpm">Native (file) BPM of the slave track.</param>
    public void BeatMatch(
        DeckEngine master, double masterTrackBpm,
        DeckEngine slave,  double slaveTrackBpm)
    {
        if (masterTrackBpm <= 0 || slaveTrackBpm <= 0) return;

        // Effective BPM takes the master's current pitch-fader position into account
        double masterEffectiveBpm = masterTrackBpm * (1.0 + master.TempoPercent / 100.0);

        // Required playback rate for the slave to match
        double requiredRate = masterEffectiveBpm / slaveTrackBpm;
        slave.TempoPercent  = (requiredRate - 1.0) * 100.0;
    }

    /// <summary>
    /// Nudges the slave deck's playhead to the nearest beat that phase-aligns with the master.
    /// Call this after <see cref="BeatMatch"/> so the slave is already at the correct tempo.
    /// </summary>
    /// <param name="master">The reference deck.</param>
    /// <param name="masterBpm">Effective BPM of the master (after tempo adjustment).</param>
    /// <param name="slave">The deck to nudge.</param>
    /// <param name="slaveBpm">Effective BPM of the slave (after BeatMatch).</param>
    public void PhaseAlign(
        DeckEngine master, double masterBpm,
        DeckEngine slave,  double slaveBpm)
    {
        if (masterBpm <= 0 || slaveBpm <= 0) return;

        double masterBeatLen = 60.0 / masterBpm;
        double slaveBeatLen  = 60.0 / slaveBpm;

        // Where within the current master beat are we? (0 .. masterBeatLen seconds)
        double masterBeatPhase = master.PositionSeconds % masterBeatLen;

        double slavePos = slave.PositionSeconds;

        // Round slave to the nearest beat boundary, then offset by masterBeatPhase
        double nearestSlaveBeat = Math.Round(slavePos / slaveBeatLen) * slaveBeatLen;
        double targetPos        = nearestSlaveBeat + masterBeatPhase;

        // If the aligned position is behind the current playhead, advance by one slave beat
        if (targetPos < slavePos - 0.01)
            targetPos += slaveBeatLen;

        slave.Seek(targetPos);
    }

    /// <summary>
    /// Returns the phase difference between deck A and deck B in fractional beats,
    /// wrapped to the range [−0.5, +0.5]. Positive means deck A is ahead.
    /// Used to drive the visual phase-offset indicator in <c>DeckViewModel</c>.
    /// </summary>
    /// <param name="deckA">Deck A engine.</param>
    /// <param name="bpmA">Effective BPM of deck A.</param>
    /// <param name="deckB">Deck B engine.</param>
    /// <param name="bpmB">Effective BPM of deck B.</param>
    /// <returns>Phase offset in beats, [-0.5, +0.5].</returns>
    public double GetPhaseOffsetBeats(
        DeckEngine deckA, double bpmA,
        DeckEngine deckB, double bpmB)
    {
        if (bpmA <= 0 || bpmB <= 0) return 0;

        // Normalise each deck's position to a 0..1 value within a single beat
        double phaseA = deckA.PositionSeconds % (60.0 / bpmA) / (60.0 / bpmA);
        double phaseB = deckB.PositionSeconds % (60.0 / bpmB) / (60.0 / bpmB);

        double diff = phaseA - phaseB;

        // Wrap to [-0.5, +0.5]
        if (diff >  0.5) diff -= 1.0;
        if (diff < -0.5) diff += 1.0;

        return diff;
    }
}
