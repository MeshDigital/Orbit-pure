using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Sets;
using Xunit;

namespace SLSKDONET.Tests.Services.Sets;

/// <summary>#205 P4.2: "Suggest next" ranking and its knowledge query.</summary>
public sealed class NextTrackRankerTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("suggest-next");

    public void Dispose() => _db.Dispose();

    private static SetPoolTrack T(string hash, double? bpm, string? key) => new(hash, "Title " + hash, "Artist", bpm, key);

    private static readonly SetPoolTrack From = T("from", 174, "8A");

    [Fact]
    public void PlayedTransitionsComeFirst_ThenScanCandidates_ThenAlgorithmic()
    {
        var pool = new[] { T("played", 172, "1B"), T("scan", 174, "8A"), T("algo", 175, "9A") };
        var known = new[]
        {
            new KnownNextTransition("scan", 90, 0, false, FromEvidence: false),
            new KnownNextTransition("played", 70, 3, true, FromEvidence: true),
        };
        var ranked = NextTrackRanker.Rank(From, pool, known, new Dictionary<string, double>());

        Assert.Equal(new[] { "played", "scan", "algo" }, ranked.Select(r => r.Track.TrackUniqueHash));
        Assert.Equal(NextSuggestionBasis.Played, ranked[0].Basis);
        Assert.Contains("3 times", ranked[0].Reason);
        Assert.Contains("verified", ranked[0].Reason);
        Assert.Equal("Your history", ranked[0].BasisLabel);
    }

    [Fact]
    public void Algorithmic_StaysInTheBpmWindow_AndPrefersCompatibleKeysAndSectionFit()
    {
        var pool = new[]
        {
            T("compatible", 175, "9A"),
            T("clash", 174, "3B"),
            T("too-fast", 190, "8A"),
            T("fits", 176, "8A"),
        };
        var fit = new Dictionary<string, double> { ["fits"] = 0.95, ["compatible"] = 0.4 };
        var ranked = NextTrackRanker.Rank(From, pool, Array.Empty<KnownNextTransition>(), fit);

        Assert.DoesNotContain(ranked, r => r.Track.TrackUniqueHash == "too-fast");
        Assert.Equal("fits", ranked[0].Track.TrackUniqueHash);
        Assert.True(ranked.Single(r => r.Track.TrackUniqueHash == "compatible").Score > ranked.Single(r => r.Track.TrackUniqueHash == "clash").Score);
        Assert.Contains("8A → 3B clashes", ranked.Single(r => r.Track.TrackUniqueHash == "clash").Reason);
        Assert.Contains("outro→intro fit 95", ranked[0].Reason);
    }

    [Fact]
    public void UnanalysedSlot_OnlyGetsKnownTransitions()
    {
        var pool = new[] { T("played", 172, "1B"), T("other", 174, "8A") };
        var known = new[] { new KnownNextTransition("played", 60, 1, false, FromEvidence: true) };
        var ranked = NextTrackRanker.Rank(T("from", null, null), pool, known, new Dictionary<string, double>());
        var only = Assert.Single(ranked);
        Assert.Equal("played", only.Track.TrackUniqueHash);
        Assert.Contains("once", only.Reason);
    }

    [Fact]
    public async Task KnownNext_KeepsEvidenceEdges_StrongestPerTarget_AndUnpromotedCandidates()
    {
        await using (var db = _db.Factory.CreateDbContext())
        {
            db.TransitionKnowledge.AddRange(
                new TransitionKnowledgeEntity { SourceTrackId = "a", TargetTrackId = "b", CompatibilityScore = 60, EvidenceCount = 1 },
                new TransitionKnowledgeEntity { SourceTrackId = "a", TargetTrackId = "b", TransitionType = KnowledgeTransitionType.Cut, CompatibilityScore = 80, EvidenceCount = 2 },
                new TransitionKnowledgeEntity { SourceTrackId = "a", TargetTrackId = "z", CompatibilityScore = 99 }); // no evidence: not a suggestion
            db.TransitionCandidates.AddRange(
                new TransitionCandidateEntity { SourceTrackId = "a", TargetTrackId = "c", CompatibilityScore = 70, IdempotencyKey = "a-c" },
                new TransitionCandidateEntity { SourceTrackId = "a", TargetTrackId = "b", CompatibilityScore = 95, IdempotencyKey = "a-b" }); // b already known
            await db.SaveChangesAsync();
        }

        var known = await new SetProjectService(_db.Factory).GetKnownNextAsync("a");

        var b = Assert.Single(known, k => k.TargetHash == "b");
        Assert.True(b.FromEvidence);
        Assert.Equal(80, b.Score);
        Assert.Contains(known, k => k.TargetHash == "c" && !k.FromEvidence);
        Assert.DoesNotContain(known, k => k.TargetHash == "z");
    }
}
