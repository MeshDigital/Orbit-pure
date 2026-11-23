using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Models;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis;

/// <summary>#285: vocal span/density/class from an isolated vocal stem's per-window RMS.</summary>
public class VocalActivityTests
{
    private const double Window = 0.5;

    [Fact]
    public void Silence_IsInstrumental_WithNullSpan()
    {
        var r = VocalActivity.FromStemRms(new float[] { 0f, 0f, 0f, 0f }, Window);

        Assert.Equal(0f, r.Density);
        Assert.Null(r.StartSeconds);
        Assert.Null(r.EndSeconds);
        Assert.Equal(VocalType.Instrumental, r.Type);
        Assert.Equal(0f, r.Intensity);
    }

    [Fact]
    public void SustainedVocalSection_ReportsSpanDensityAndLeadVocal()
    {
        // Windows 2-4 sing (indices 2,3,4 → 1.0s .. 2.5s at 0.5 s per window).
        var rms = new float[] { 0f, 0f, 0.3f, 0.3f, 0.25f, 0f, 0f };

        var r = VocalActivity.FromStemRms(rms, Window);

        Assert.Equal(3f / 7f, r.Density, 3);
        Assert.Equal(1.0f, r.StartSeconds!.Value, 3);
        Assert.Equal(2.5f, r.EndSeconds!.Value, 3);
        Assert.Equal(VocalType.LeadVocal, r.Type);
        Assert.True(r.Intensity > 0.9f);
    }

    [Fact]
    public void SparseHits_AreVocalChops_WithoutAContinuousSpan()
    {
        // Two isolated voiced windows (0 and 5), neither in a run of two → no span.
        var rms = new float[] { 0.3f, 0f, 0f, 0f, 0f, 0.3f, 0f, 0f };

        var r = VocalActivity.FromStemRms(rms, Window);

        Assert.Equal(0.25f, r.Density, 3);
        Assert.Null(r.StartSeconds);
        Assert.Null(r.EndSeconds);
        Assert.Equal(VocalType.VocalChops, r.Type);
    }

    [Fact]
    public void LeakageBelowTheAbsoluteFloor_IsInstrumental()
    {
        // A quiet, constant stem (Demucs bleed) whose peak is under the −60 dBFS floor.
        var rms = new float[] { 0.0005f, 0.0005f, 0.0008f, 0.0005f };

        var r = VocalActivity.FromStemRms(rms, Window);

        Assert.Equal(VocalType.Instrumental, r.Type);
        Assert.Equal(0f, r.Density);
        Assert.Null(r.StartSeconds);
    }

    [Fact]
    public void Apply_WritesEveryVocalField()
    {
        var features = new AudioFeaturesEntity { TrackUniqueHash = "h" };
        var result = new VocalActivityResult(0.42f, 1.0f, 2.5f, VocalType.LeadVocal, 0.9f, "[0,1,0.5]");

        VocalActivity.Apply(result, features);

        Assert.Equal(0.42f, features.VocalDensity);
        Assert.Equal(1.0f, features.VocalStartSeconds);
        Assert.Equal(2.5f, features.VocalEndSeconds);
        Assert.Equal(VocalType.LeadVocal, features.DetectedVocalType);
        Assert.Equal(0.9f, features.VocalIntensity);
        Assert.Equal("[0,1,0.5]", features.VocalDensityCurveJson);
    }

    [Fact]
    public void IsMeasured_IsTrueOnceACurveWasWritten()
    {
        Assert.False(VocalActivity.IsMeasured(new AudioFeaturesEntity()));
        Assert.False(VocalActivity.IsMeasured(new AudioFeaturesEntity { VocalDensityCurveJson = "[]" }));
        Assert.True(VocalActivity.IsMeasured(new AudioFeaturesEntity { VocalDensityCurveJson = "[0,0.1]" }));
    }
}
