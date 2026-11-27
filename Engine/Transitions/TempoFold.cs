namespace SLSKDONET.Engine.Transitions;

/// <summary>
/// Half/double-time folding for mixing: a DnB track analysed at 87 BPM and one at 174 BPM are the
/// same tempo for a beat-matched mix. One rule, shared by the player's tempo sync
/// (<c>AudioPlayerService.TempoSyncRatio</c>), the mix-ready check
/// (<c>TransitionPresetLibrary.RequiresUnreadyFallback</c>) and the planner's tempo lock, so the three
/// can't disagree about whether a pair can be beat-matched (#610).
/// </summary>
public static class TempoFold
{
    /// <summary>
    /// <paramref name="outgoingBpm"/> / <paramref name="incomingBpm"/>, halved above 1.8 and doubled
    /// below 0.55; 1 when either tempo is unknown.
    /// </summary>
    public static double Ratio(double outgoingBpm, double incomingBpm)
    {
        if (outgoingBpm <= 0 || incomingBpm <= 0) return 1.0;
        double ratio = outgoingBpm / incomingBpm;
        if (ratio > 1.8) ratio /= 2; else if (ratio < 0.55) ratio *= 2;
        return ratio;
    }

    /// <summary>The incoming tempo expressed at the outgoing track's tempo scale (174 → 87 next to an 87 BPM track).</summary>
    public static double IncomingAtOutgoingScale(double outgoingBpm, double incomingBpm) =>
        outgoingBpm > 0 && incomingBpm > 0 ? outgoingBpm / Ratio(outgoingBpm, incomingBpm) : incomingBpm;

    /// <summary>
    /// Relative tempo difference after folding, measured the way the player's tempo sync measures it:
    /// |out / in′ − 1|. The mix-ready check used |out − in| / out, which disagrees near the 6 % limit, so
    /// MixBenchmark found pairs planned as beat-matched that the player then played without tempo sync.
    /// </summary>
    public static double RelativeDifference(double outgoingBpm, double incomingBpm) =>
        outgoingBpm > 0 && incomingBpm > 0 ? System.Math.Abs(Ratio(outgoingBpm, incomingBpm) - 1) : 0;
}
