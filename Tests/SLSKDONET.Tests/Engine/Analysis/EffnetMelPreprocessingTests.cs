using System;
using System.IO;
using System.Linq;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Services.Similarity;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

/// <summary>
/// Issue #270: the EffNet input must be log10(1 + 10000·x) of Slaney area-normalised mel bands.
/// The real-model check is <c>Tests/GenreBenchmark --mel-experiment</c>; these pin the math.
/// </summary>
public class EffnetMelPreprocessingTests
{
    [Fact]
    public void Silence_IsZero()
    {
        var patches = EffnetMelSpectrogramExtractor.ExtractPatches(new float[16000 * 3]);
        Assert.NotEmpty(patches);
        Assert.All(patches[0], v => Assert.Equal(0f, v));
    }

    [Fact]
    public void FullScaleNoise_StaysInTheLog10Range()
    {
        // With ln and unit-height filters (the #270 bug) full-scale noise reached ~16-19;
        // the trained range for log10 of area-normalised bands is a few units.
        var rng = new Random(3);
        var noise = Enumerable.Range(0, 16000 * 4).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
        var patch = EffnetMelSpectrogramExtractor.ExtractPatches(noise)[0];
        Assert.True(patch.Max() < 8f, $"max {patch.Max()}");
        Assert.True(patch.Average() > 0.5f, $"mean {patch.Average()}");
    }

    [Fact]
    public void ModelTag_CarriesThePreprocessingVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"effnet-tag-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        try
        {
            var tag = DiscogsEffnetEmbeddingExtractor.BuildModelTag(path);
            Assert.EndsWith($"|mel{EffnetMelSpectrogramExtractor.PreprocessingVersion}", tag);
            Assert.Equal(2, EffnetMelSpectrogramExtractor.PreprocessingVersion);
        }
        finally { File.Delete(path); }
    }
}
