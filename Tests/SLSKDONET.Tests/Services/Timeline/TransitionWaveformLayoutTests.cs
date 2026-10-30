using SLSKDONET.Services.Timeline;
using Xunit;

namespace SLSKDONET.Tests.Services.Timeline;

public class TransitionWaveformLayoutTests
{
    [Fact]
    public void BarSeconds_IsFourBeatsAtBpm()
        => Assert.Equal(1.0, TransitionWaveformLayout.BarSeconds(240.0), 6);

    [Fact]
    public void MixWindowSeconds_IsBarsTimesBarSeconds()
        => Assert.Equal(4.0, TransitionWaveformLayout.MixWindowSeconds(240.0, 4), 6);

    [Fact]
    public void TimeToX_MapsProportionallyAndClamps()
    {
        Assert.Equal(50.0, TransitionWaveformLayout.TimeToX(5.0, 10.0, 100.0), 6);
        Assert.Equal(0.0, TransitionWaveformLayout.TimeToX(-1.0, 10.0, 100.0), 6);
        Assert.Equal(100.0, TransitionWaveformLayout.TimeToX(99.0, 10.0, 100.0), 6);
    }

    [Fact]
    public void MixWindowBox_AnchorIsEnd_EndsAtAnchor()
    {
        // 240 BPM → 1s/bar; 2 bars = 2s. Anchor at 8s → window [6, 8] → x [60, 80].
        var (x1, x2) = TransitionWaveformLayout.MixWindowBox(
            8.0, 240.0, 2, 10.0, 100.0, anchorIsEnd: true);

        Assert.Equal(60.0, x1, 6);
        Assert.Equal(80.0, x2, 6);
    }

    [Fact]
    public void MixWindowBox_AnchorIsStart_StartsAtAnchor()
    {
        // Anchor (mix-in) at 2s → window [2, 4] → x [20, 40].
        var (x1, x2) = TransitionWaveformLayout.MixWindowBox(
            2.0, 240.0, 2, 10.0, 100.0, anchorIsEnd: false);

        Assert.Equal(20.0, x1, 6);
        Assert.Equal(40.0, x2, 6);
    }
}
