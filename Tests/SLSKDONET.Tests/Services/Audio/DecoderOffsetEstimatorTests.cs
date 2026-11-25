using System;
using SLSKDONET.Services.Audio;
using Xunit;

namespace SLSKDONET.Tests.Services.Audio;

/// <summary>LF6 (#348): the decoder-offset cross-correlation finds a known synthetic shift.</summary>
public class DecoderOffsetEstimatorTests
{
    [Fact]
    public void EstimateOffsetMs_FindsAPositiveDelay()
    {
        const int rate = 1000;
        const int delay = 50; // samples → 50 ms
        var reference = new float[2000];
        var candidate = new float[2000];

        // Deterministic noise: its autocorrelation has a single sharp peak, unlike a periodic tone.
        var rng = new Random(1234);
        for (var i = 0; i < reference.Length; i++)
            reference[i] = (float)(rng.NextDouble() - 0.5);
        for (var i = delay; i < candidate.Length; i++)
            candidate[i] = reference[i - delay];

        var offset = DecoderOffsetEstimator.EstimateOffsetMs(reference, candidate, rate);

        Assert.Equal(50, offset, 1);
    }

    [Fact]
    public void EstimateOffsetMs_ReturnsZeroForIdenticalSignals()
    {
        var reference = new float[1000];
        for (var i = 0; i < reference.Length; i++)
            reference[i] = (float)Math.Sin(2 * Math.PI * 10 * i / 1000);

        var offset = DecoderOffsetEstimator.EstimateOffsetMs(reference, reference, 1000);

        Assert.Equal(0, offset, 1);
    }

    [Fact]
    public void EstimateOffsetMs_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0, DecoderOffsetEstimator.EstimateOffsetMs(Array.Empty<float>(), new float[10], 44100));
    }
}
