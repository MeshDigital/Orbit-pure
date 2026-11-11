using SLSKDONET.Services.AudioAnalysis;
using Xunit;

namespace SLSKDONET.Tests.Services.AudioAnalysis;

public class PhraseBarIndexingTests
{
    [Theory]
    [InlineData(2.0, 120.0, 2.0, 0)]   // on Bar 1
    [InlineData(6.0, 120.0, 2.0, 2)]   // bar 2 (2s per bar)
    [InlineData(10.0, 120.0, 2.0, 4)]  // bar 4
    public void SecondsToBar_CountsFromBarOne(double seconds, double bpm, double downbeat, int expectedBar)
    {
        Assert.Equal(expectedBar, PhraseBarIndexing.SecondsToBar(seconds, bpm, downbeat));
    }

    [Fact]
    public void BarToSeconds_RoundTrips()
    {
        const double bpm = 174.0;
        const double downbeat = 0.5;
        int bar = 12;

        double seconds = PhraseBarIndexing.BarToSeconds(bar, bpm, downbeat);
        Assert.Equal(bar, PhraseBarIndexing.SecondsToBar(seconds, bpm, downbeat));
    }

    [Fact]
    public void DurationToBars_ComputesTheBarCount()
    {
        Assert.Equal(4, PhraseBarIndexing.DurationToBars(8.0, 120.0)); // 2s/bar at 120
    }

    [Fact]
    public void BarSeconds_IsFourBeats()
    {
        Assert.Equal(2.0, PhraseBarIndexing.BarSeconds(120.0), 6);
        Assert.Equal(0.0, PhraseBarIndexing.BarSeconds(0.0), 6);
    }
}
