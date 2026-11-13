using System.Linq;
using SLSKDONET.Engine.Analysis.GenreFusion;
using Xunit;

namespace SLSKDONET.Tests.Engine.Analysis.GenreFusion;

/// <summary>
/// Golden equivalence tests for <see cref="GenreFusionEngine"/>. The expected winner / confidence /
/// distribution below were captured from the pre-refactor scoring in
/// <c>AudioAnalysisService.InferAndApplyGenreAsync</c> (master @ 6b8a708) over these same synthetic
/// inputs; the engine is a verbatim port, so every value must match bit-for-bit.
/// </summary>
public class GenreFusionEngineTests
{
    private sealed record GoldenCase(
        string Name,
        float Bpm,
        (string Label, float Score)[] Labels,
        string? Winner,
        float Confidence,
        (string Key, double Score)[] Distribution);

    private static readonly GoldenCase[] GoldenCases =
    {
        new("dnb_174", 174f, new[] { ("drumnbass", 0.9f), ("techno", 0.1f) },
            "Drum and Bass", 0.99f, new[] { ("Drum and Bass", 0.85) }),
        new("dnb_87_half", 87f, new[] { ("drumnbass", 0.9f) },
            "Drum and Bass", 0.95f, new[] { ("Drum and Bass", 0.6375) }),
        new("house_124", 124f, new[] { ("house", 0.8f), ("techno", 0.3f) },
            "House", 0.99f, new[] { ("House", 0.748), ("Techno", 0.323) }),
        new("deephouse_124", 124f, new[] { ("deephouse", 0.7f), ("house", 0.5f) },
            "House", 0.97999996f, new[] { ("House", 1.156) }),
        new("techno_132", 132f, new[] { ("techno", 0.85f) },
            "Techno", 0.99f, new[] { ("Techno", 0.7905) }),
        new("minimal_techno_132", 132f, new[] { ("minimal", 0.6f), ("techno", 0.5f) },
            "Techno", 0.88f, new[] { ("Techno", 1.071) }),
        new("dnb_house_mix_174", 174f, new[] { ("drumnbass", 0.5f), ("house", 0.6f) },
            "Drum and Bass", 0.702f, new[] { ("Drum and Bass", 0.51), ("House", 0.408) }),
        new("techno_house_128", 128f, new[] { ("techno", 0.6f), ("house", 0.6f) },
            "Techno", 0.68f, new[] { ("Techno", 0.578), ("House", 0.578) }),
        new("empty_174", 174f, System.Array.Empty<(string, float)>(),
            "Drum and Bass", 0.92f, new[] { ("Drum and Bass", 0.252) }),
        new("empty_124", 124f, System.Array.Empty<(string, float)>(),
            "House", 0.84f, new[] { ("House", 0.224) }),
        new("empty_132", 132f, System.Array.Empty<(string, float)>(),
            "House", 0.84f, new[] { ("House", 0.224) }),
        new("empty_90", 90f, System.Array.Empty<(string, float)>(),
            null, 0f, System.Array.Empty<(string, double)>()),
        new("low_conf_dnb_174", 174f, new[] { ("drumnbass", 0.1f) },
            "Drum and Bass", 0.37f, new[] { ("Drum and Bass", 0.17) }),
        new("dubstep_140", 140f, new[] { ("dubstep", 0.7f) },
            "Dubstep", 0.9f, new[] { ("Dubstep", 0.595) }),
        new("trance_138", 138f, new[] { ("trance", 0.8f) },
            "Trance", 0.99f, new[] { ("Trance", 0.68) }),
        new("breakbeat_174", 174f, new[] { ("breakbeat", 0.7f) },
            "Drum and Bass", 0.99f, new[] { ("Drum and Bass", 0.68) }),
        new("rock_only_174", 174f, new[] { ("rock", 0.9f) },
            "Drum and Bass", 0.92f, new[] { ("Drum and Bass", 0.252) }),
        new("zero_conf", 174f, new[] { ("drumnbass", 0f) },
            null, 0f, System.Array.Empty<(string, double)>()),
    };

    [Fact]
    public void Fuse_MatchesPreRefactorGoldenOutputs()
    {
        foreach (var c in GoldenCases)
        {
            var input = new GenreFusionInput(
                c.Labels.Select(l => new GenreLabelScore(l.Label, l.Score)).ToArray(),
                c.Bpm);

            var result = GenreFusionEngine.Fuse(input);

            if (c.Winner is null)
            {
                Assert.Null(result);
                continue;
            }

            Assert.NotNull(result);
            Assert.Equal(c.Winner, result.WinnerLabel);
            Assert.Equal(c.Confidence, result.Confidence);
            Assert.Equal(c.Distribution.Length, result.Distribution.Count);
            foreach (var (key, score) in c.Distribution)
            {
                Assert.True(result.Distribution.TryGetValue(key, out var actual),
                    $"case {c.Name}: missing distribution key '{key}'");
                Assert.Equal(score, actual);
            }
        }
    }

    [Fact]
    public void Fuse_EmptyInput_FallsBackToHeuristic()
    {
        var result = GenreFusionEngine.Fuse(new GenreFusionInput(
            System.Array.Empty<GenreLabelScore>(), 174f));

        Assert.NotNull(result);
        Assert.True(result.RanHeuristic);
        Assert.Contains(result.Candidates, c => c.Source == "heuristic" && c.Weight == 0.35f);
    }

    [Fact]
    public void Fuse_JamendoInput_DoesNotRunHeuristic()
    {
        var result = GenreFusionEngine.Fuse(new GenreFusionInput(
            new[] { new GenreLabelScore("drumnbass", 0.9f) }, 174f));

        Assert.NotNull(result);
        Assert.False(result.RanHeuristic);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("onnx-jamendo", candidate.Source);
        Assert.Equal(0.85f, candidate.Weight);
        Assert.Equal("Drum and Bass", candidate.Label);
    }

    [Fact]
    public void Fuse_FiltersZeroAndNegativeConfidence()
    {
        // "techno" at 174 BPM is guardrailed to ≤ 0, and "drumnbass" is boosted — only DnB survives.
        var result = GenreFusionEngine.Fuse(new GenreFusionInput(
            new[] { new GenreLabelScore("drumnbass", 0.9f), new GenreLabelScore("techno", 0.9f) }, 174f));

        Assert.NotNull(result);
        Assert.Equal("Drum and Bass", result.WinnerLabel);
        Assert.Single(result.Distribution);
    }
}
