using SLSKDONET.Data.Entities;
using SLSKDONET.Services.KnowledgeLifecycle;
using Xunit;

namespace SLSKDONET.Tests.Services.KnowledgeLifecycle;

public class TransitionCandidateFilterTests
{
    private static AudioFeaturesEntity Track(string hash, float bpm, string camelotKey, float energy = 0.5f)
        => new() { TrackUniqueHash = hash, Bpm = bpm, CamelotKey = camelotKey, Energy = energy };

    // ── BPM band (±8% of the seed) ─────────────────────────────────────

    [Theory]
    [InlineData(174.0, 174.0, true)]
    [InlineData(174.0, 180.0, true)]   // +3.4%, in band
    [InlineData(174.0, 190.0, false)]  // +9.2%, out of band (174 * 1.08 = 187.9)
    [InlineData(174.0, 168.0, true)]   // −3.4%, in band (lower bound is 174 * 0.92 = 160.08)
    [InlineData(174.0, 0.0, false)]    // unknown candidate BPM
    [InlineData(0.0, 174.0, false)]    // unknown seed BPM
    public void IsWithinBpmBand(double seed, double candidate, bool expected)
        => Assert.Equal(expected, TransitionCandidateFilter.IsWithinBpmBand(seed, candidate));

    // ── Camelot adjacency (distance ≤ 1) ───────────────────────────────

    [Theory]
    [InlineData("8A", "8A", true)]
    [InlineData("8A", "9A", true)]
    [InlineData("8A", "7A", true)]
    [InlineData("8A", "8B", true)]
    [InlineData("8A", "10A", false)]  // distance 2
    [InlineData("8A", "1A", false)]   // distance 5
    [InlineData("8A", null, false)]   // unknown key → distance 3
    public void IsAdjacentCamelot(string? seed, string? candidate, bool expected)
        => Assert.Equal(expected, TransitionCandidateFilter.IsAdjacentCamelot(seed, candidate));

    // ── Idempotency key ────────────────────────────────────────────────

    [Fact]
    public void IdempotencyKey_IncludesVersion()
    {
        Assert.Equal("a_b_v1", TransitionCandidateFilter.IdempotencyKey("a", "b", "v1"));
        Assert.NotEqual(
            TransitionCandidateFilter.IdempotencyKey("a", "b", "v1"),
            TransitionCandidateFilter.IdempotencyKey("a", "b", "v2"));
    }

    // ── Bounded selection ──────────────────────────────────────────────

    [Fact]
    public void SelectCandidates_ExcludesSelf_BoundsByBpmAndKey_AndCaps()
    {
        var seed = Track("seed", 174.0f, "8A");
        var library = new[]
        {
            Track("self", 174.0f, "8A"),       // excluded: self
            Track("same", 174.0f, "8A"),       // included
            Track("near", 176.0f, "9A"),       // included
            Track("far-bpm", 200.0f, "8A"),    // excluded: BPM out of band
            Track("far-key", 174.0f, "1A"),    // excluded: Camelot distance 5
            Track("also-ok", 174.0f, "8B"),    // included (but past the cap)
        };

        var result = TransitionCandidateFilter.SelectCandidates(seed, library, maxCandidates: 2);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, c => c.TrackUniqueHash == "self");
        Assert.DoesNotContain(result, c => c.TrackUniqueHash == "far-bpm");
        Assert.DoesNotContain(result, c => c.TrackUniqueHash == "far-key");
        Assert.Contains(result, c => c.TrackUniqueHash == "same");
    }
}
