using System.Linq;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Services.Similarity;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

/// <summary>GA7 / #252: energy-weighted pooling of EffNet patches.</summary>
public class PatchEnergyWeightsTests
{
    private const int PatchSamples = EffnetMelSpectrogramExtractor.PatchFrames * EffnetMelSpectrogramExtractor.HopSize;

    private static float[] Profile(params float[] patchLevels)
    {
        var audio = new float[patchLevels.Length * PatchSamples + EffnetMelSpectrogramExtractor.FrameSize];
        for (var p = 0; p < patchLevels.Length; p++)
            for (var i = 0; i < PatchSamples; i++)
                audio[p * PatchSamples + i] = patchLevels[p] * (float)System.Math.Sin(i * 0.05);
        return audio;
    }

    [Fact]
    public void QuietIntroAndOutro_LoudBody_BodyDominates()
    {
        // 3 quiet intro patches, 4 loud body patches, 3 quiet outro patches.
        var levels = new[] { 0.05f, 0.05f, 0.05f, 0.9f, 0.9f, 0.9f, 0.9f, 0.1f, 0.1f, 0.1f };
        var w = PatchEnergyWeights.Compute(Profile(levels), levels.Length);

        Assert.All(new[] { 3, 4, 5, 6 }, p => Assert.Equal(PatchEnergyWeights.LoudWeight, w[p]));
        Assert.All(new[] { 0, 1, 2, 7, 8, 9 }, p => Assert.True(w[p] < PatchEnergyWeights.LoudWeight));
        var body = new[] { 3, 4, 5, 6 }.Sum(p => w[p]);
        var rest = new[] { 0, 1, 2, 7, 8, 9 }.Sum(p => w[p]);
        Assert.True(body > rest, $"body {body} vs intro/outro {rest}");
        Assert.Equal(PatchEnergyWeights.QuietWeight, w[0]); // the quietest 20 %
    }

    [Fact]
    public void FewPatchesOrSilence_AreUnweighted()
    {
        Assert.All(PatchEnergyWeights.Compute(Profile(0.5f, 0.1f), 2), x => Assert.Equal(1f, x));
        Assert.All(PatchEnergyWeights.Compute(new float[10 * PatchSamples], 10), x => Assert.Equal(1f, x));
    }

    [Fact]
    public void StyleTag_IsTheEmbeddingTagPlusThePooling()
    {
        // The embedding tag must not change (similarity compatibility); only the style tag gains the suffix.
        Assert.Equal("discogs-effnet-bsdynamic-1|ABCDEF12|mel2|pool=energy1",
            DiscogsEffnetEmbeddingExtractor.BuildStyleTag("discogs-effnet-bsdynamic-1|ABCDEF12|mel2"));
    }
}
