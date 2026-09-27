using System.Linq;
using SLSKDONET.ViewModels;
using SLSKDONET.Views.Avalonia.Controls;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class EnergyCurveDownsamplingTests
{
    [Fact]
    public void ComputeEnergyCurve_ShortWaveform_IsUnchangedPerSampleAverage()
    {
        var low = new byte[] { 0, 255, 51 };
        var mid = new byte[] { 255, 255, 51 };
        var high = new byte[] { 0, 255, 51 };

        var curve = PlaylistTrackViewModel.ComputeEnergyCurve(low, mid, high);

        Assert.Equal(3, curve.Count);
        Assert.Equal(1.0 / 3, curve[0], precision: 6);
        Assert.Equal(1.0, curve[1], precision: 6);
        Assert.Equal(0.2, curve[2], precision: 6);
    }

    [Fact]
    public void ComputeEnergyCurve_LongWaveform_IsCappedAndBucketAveraged()
    {
        // 2,000 samples alternating 0 / 255 in every band: each bucket spans several samples,
        // so every output point averages to ~0.5 rather than keeping the raw alternation.
        var data = Enumerable.Range(0, 2000).Select(i => (byte)(i % 2 == 0 ? 0 : 255)).ToArray();

        var curve = PlaylistTrackViewModel.ComputeEnergyCurve(data, data, data);

        Assert.Equal(256, curve.Count);
        Assert.All(curve, v => Assert.InRange(v, 0.4, 0.6));
    }

    [Fact]
    public void SparklineDownsample_ReturnsSameInstanceWhenAlreadySmallEnough()
    {
        var values = new double[] { 1, 2, 3 };
        Assert.Same(values, SparklineControl.Downsample(values, 10));
    }

    [Fact]
    public void SparklineDownsample_AveragesIntoRequestedPointCount()
    {
        var values = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();

        var result = SparklineControl.Downsample(values, 10);

        Assert.Equal(10, result.Count);
        Assert.Equal(4.5, result[0], precision: 6);   // mean of 0..9
        Assert.Equal(94.5, result[9], precision: 6);  // mean of 90..99
    }
}
