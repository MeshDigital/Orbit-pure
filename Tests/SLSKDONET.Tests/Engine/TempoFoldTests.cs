using System.Linq;
using SLSKDONET.Engine.Transitions;
using SLSKDONET.Services;
using SLSKDONET.Services.Timeline;
using Xunit;

namespace SLSKDONET.Tests.Engine;

/// <summary>
/// #610: half/double-time pairs (87 vs 174 BPM) fell back to an echo-out although the player's tempo
/// sync beat-matches them; 6.6 % of all playlist pairs in MixBenchmark (#599). One fold rule now serves
/// the player, the mix-ready check and the planner.
/// </summary>
public sealed class TempoFoldTests
{
    [Theory]
    [InlineData(174, 87, 1.0)]
    [InlineData(87, 174, 1.0)]
    [InlineData(174, 172, 174.0 / 172)]
    [InlineData(140, 174, 140.0 / 174)] // not a half/double pair: left alone
    public void Ratio_FoldsHalfAndDoubleTime(double outBpm, double inBpm, double expected) =>
        Assert.Equal(expected, TempoFold.Ratio(outBpm, inBpm), 6);

    [Fact]
    public void Ratio_UnknownTempo_IsOne() => Assert.Equal(1.0, TempoFold.Ratio(0, 174));

    [Theory]
    [InlineData(174, 87, false)]   // DnB analysed at half time: beat-matchable
    [InlineData(87.5, 172, false)] // half time and 1.7 % apart
    [InlineData(174, 172, false)]
    [InlineData(140, 174, true)]   // a real tempo jump
    [InlineData(128, 174, true)]
    public void UnreadyFallback_TreatsHalfAndDoubleTimeAsTheSameTempo(double outBpm, double inBpm, bool fallback)
    {
        Assert.Equal(fallback, TransitionPresetLibrary.RequiresUnreadyFallback(Gridded(outBpm), Gridded(inBpm)));
    }

    [Theory]
    [InlineData(174, 87)]
    [InlineData(87, 174)]
    [InlineData(174, 170)]
    [InlineData(140, 174)]
    [InlineData(100, 94.2)] // where |out-in|/out (5.8 %) and |out/in-1| (6.2 %) used to disagree
    public void UnreadyFallback_AgreesWithThePlayersTempoSync(double outBpm, double inBpm)
    {
        bool playerSyncs = AudioPlayerService.TempoSyncRatio(outBpm, inBpm) != 1.0 || TempoFold.Ratio(outBpm, inBpm) == 1.0;
        bool beatMatched = !TransitionPresetLibrary.RequiresUnreadyFallback(Gridded(outBpm), Gridded(inBpm));
        Assert.Equal(playerSyncs, beatMatched);
    }

    private static TrackStructure Gridded(double bpm) => new()
    {
        DurationSeconds = 300,
        Bpm = bpm,
        FirstDownbeat = 0.5,
        BeatGrid = Enumerable.Range(0, 200).Select(i => 0.5 + i * 60.0 / bpm).ToList(),
    };
}
