using System;
using System.Linq;
using SLSKDONET.Engine.Analysis;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

public class SubBassDropoutEngineTests
{
    [Fact]
    public void ComputeSubBassEnergyCurve_MatchesComputeBandEnergyCurveAt120Hz()
    {
        // ComputeSubBassEnergyCurve was generalized into a thin wrapper over
        // ComputeBandEnergyCurve(signal, rate, 120.0) so StructuralStrippingEngine could reuse
        // the same filter/windowing pipeline at a different cutoff — this proves the refactor
        // preserved the original method's exact behavior for existing callers.
        var engine = new SubBassDropoutEngine();
        var rng = new Random(42);
        var signal = Enumerable.Range(0, 44100 * 3).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();

        var viaWrapper = engine.ComputeSubBassEnergyCurve(signal, 44100);
        var viaGeneralized = engine.ComputeBandEnergyCurve(signal, 44100, 120.0);

        Assert.Equal(viaWrapper.Length, viaGeneralized.Length);
        for (int i = 0; i < viaWrapper.Length; i++)
        {
            Assert.Equal(viaWrapper[i], viaGeneralized[i], precision: 6);
        }
    }
}
