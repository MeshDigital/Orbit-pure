using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Models;
using Xunit;

namespace SLSKDONET.Tests.Engine;

/// <summary>
/// Coverage for which stored phrase analysis feeds cue placement. The real failure mode behind
/// "the phrasing is clearly visible but the drop is way off": the legacy PhraseSegmentsJson column
/// held half-time boundaries (Intro 0–88s, Drop 1 at 132s of a 256s track) and an empty source, so
/// the generator ignored phrases entirely, while the fresh TrackPhrases rows (Intro 0–44s, Drop 1
/// at 66s) were never read.
/// </summary>
public class PhraseSourceResolverTests
{
    private static PhraseSegment Seg(string label, double start, double duration) =>
        new() { Label = label, Start = (float)start, Duration = (float)duration };

    [Fact]
    public void Resolve_PrefersFreshTrackPhrases_OverStaleLegacyJson()
    {
        var staleJson = new List<PhraseSegment> { Seg("Intro", 0, 88.28), Seg("Drop", 132.41, 44.14), Seg("Outro", 397.24, 88.28) };
        var stored = new List<PhraseSegment> { Seg("Intro", 0, 44.14), Seg("Build", 44.14, 22.07), Seg("Drop", 66.21, 66.21), Seg("Outro", 242.77, 13.09) };

        var chosen = PhraseSourceResolver.Resolve("", staleJson, stored, duration: 255.86);

        Assert.Equal(4, chosen.Count);
        Assert.Contains(chosen, s => s.Label == "Drop" && System.Math.Abs(s.Start - 66.21) < 0.01);
        Assert.DoesNotContain(chosen, s => s.Start > 255.86);
    }

    [Fact]
    public void Resolve_KeepsRekordboxPssi_EvenWhenTrackPhrasesExist()
    {
        var pssi = new List<PhraseSegment> { Seg("Intro", 0, 32), Seg("Drop", 64, 64), Seg("Outro", 200, 40) };
        var stored = new List<PhraseSegment> { Seg("Intro", 0, 44), Seg("Drop", 66, 66) };

        var chosen = PhraseSourceResolver.Resolve("RekordboxPSSI", pssi, stored, duration: 240);

        Assert.Equal(64, chosen.First(s => s.Label == "Drop").Start, 3);
    }

    [Fact]
    public void Resolve_FallsBackToJson_WhenNoStoredPhrases()
    {
        var json = new List<PhraseSegment> { Seg("Intro", 0, 30), Seg("Drop", 60, 60) };

        var chosen = PhraseSourceResolver.Resolve("Heuristic", json, null, duration: 240);

        Assert.Equal(2, chosen.Count);
        Assert.Equal(60, chosen[1].Start, 3);
    }

    [Fact]
    public void Sanitize_DropsSegmentsStartingPastTheEnd_AndClampsOverlongDurations()
    {
        var segments = new List<PhraseSegment>
        {
            Seg("Intro", 0, 44),
            Seg("Drop", 66, 300),      // claims to run past the file; duration is clamped
            Seg("Outro", 397, 88),     // starts after the file ended: stale analysis
        };

        var clean = PhraseSourceResolver.Sanitize(segments, duration: 256);

        Assert.Equal(2, clean.Count);
        Assert.Equal(190, clean[1].Duration, 3);
        Assert.DoesNotContain(clean, s => s.Start > 256);
    }

    [Fact]
    public void FromTrackPhrases_UsesTheTypeName_WhenTheLabelIsEmpty()
    {
        var rows = new List<TrackPhraseEntity>
        {
            new() { Label = null, Type = PhraseType.Intro, StartTimeSeconds = 0, EndTimeSeconds = 44, Confidence = 0.9f },
            new() { Label = "Drop 1", Type = PhraseType.Drop, StartTimeSeconds = 66, EndTimeSeconds = 132, Confidence = 0.8f },
        };

        var segments = PhraseSourceResolver.FromTrackPhrases(rows);

        Assert.Equal("Intro", segments[0].Label);
        Assert.Equal("Drop 1", segments[1].Label);
        Assert.Equal(66, segments[1].Duration, 3);
    }
}
