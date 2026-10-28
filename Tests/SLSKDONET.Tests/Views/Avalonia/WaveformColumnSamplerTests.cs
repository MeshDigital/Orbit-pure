using SLSKDONET.Views.Avalonia.Controls;
using Xunit;

namespace SLSKDONET.Tests.Views.Avalonia;

public class WaveformColumnSamplerTests
{
    [Fact]
    public void Sample_CollapsedColumns_PreservesTheDropPeak()
    {
        var low = new byte[] { 12, 14, 13, 240, 12, 11 };
        var mid = new byte[] { 20, 18, 19, 90, 21, 20 };
        var high = new byte[] { 8, 9, 8, 45, 7, 8 };

        var column = WaveformColumnSampler.Sample(low, mid, high, 0, low.Length);

        Assert.Equal(240, column.Low);
        Assert.Equal(90, column.Mid);
        Assert.Equal(45, column.High);
    }

    [Fact]
    public void Sample_SubSampleZoom_InterpolatesBetweenKnownValues()
    {
        var low = new byte[] { 100, 200 };
        var mid = new byte[] { 40, 80 };
        var high = new byte[] { 10, 30 };

        var column = WaveformColumnSampler.Sample(low, mid, high, 0.5, 0.75);

        Assert.Equal(150, column.Low);
        Assert.Equal(60, column.Mid);
        Assert.Equal(20, column.High);
    }
}
