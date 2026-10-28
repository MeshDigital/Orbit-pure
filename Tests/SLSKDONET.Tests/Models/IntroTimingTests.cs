using SLSKDONET.Models;
using Xunit;

namespace SLSKDONET.Tests.Models;

public sealed class IntroTimingTests
{
    [Theory]
    [InlineData(32, 120, 16)]
    [InlineData(22.0689655172, 174, 16)]
    [InlineData(0, 120, 0)]
    public void CalculateBars_ValidTiming_ReturnsApproximateFourFourBars(
        double seconds, double bpm, double expectedBars)
    {
        var bars = IntroTiming.CalculateBars(seconds, bpm);

        Assert.NotNull(bars);
        Assert.Equal(expectedBars, bars!.Value, precision: 6);
    }

    [Theory]
    [InlineData(null, 120.0)]
    [InlineData(32.0, null)]
    [InlineData(-1.0, 120.0)]
    [InlineData(32.0, 0.0)]
    [InlineData(32.0, -120.0)]
    public void CalculateBars_MissingOrInvalidTiming_ReturnsNull(double? seconds, double? bpm)
    {
        Assert.Null(IntroTiming.CalculateBars(seconds, bpm));
    }
}
