using System;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.KnowledgeLifecycle;
using SLSKDONET.Tests.Services.Sets;
using Xunit;

namespace SLSKDONET.Tests.Services.KnowledgeLifecycle;

/// <summary>#205 P4.6 / F12: the Transition Agent's candidate query orders before its cap and loads only what it needs.</summary>
public sealed class CandidateFeaturesQueryTests : IDisposable
{
    private readonly SetsTestDatabase _db = new("candidate-features");

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ClosestBpmSurvivesTheCap_AndRowsAreLightweight()
    {
        var seed = new AudioFeaturesEntity { TrackUniqueHash = "seed", Bpm = 174, CamelotKey = "8A" };
        _db.SeedLibrary(Enumerable.Range(0, 600).Select(i => $"t{i:000}").Concat(new[] { "seed", "exact", "out-of-band" })
            .Select(h => (h, "Artist", "Title " + h)).ToArray());
        await using (var db = _db.Factory.CreateDbContext())
        {
            db.AudioFeatures.Add(seed);
            // 600 in-band tracks inserted far-BPM first, so an unordered Take(500) would miss the closest ones.
            for (var i = 0; i < 600; i++)
                db.AudioFeatures.Add(new AudioFeaturesEntity
                {
                    TrackUniqueHash = $"t{i:000}",
                    Bpm = 174 + 13 - i * 0.02f, // 187 → 175.02
                    CamelotKey = "8A",
                    InstrumentalProbability = 0.9f,
                });
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = "exact", Bpm = 174, CamelotKey = "8A" });
            db.AudioFeatures.Add(new AudioFeaturesEntity { TrackUniqueHash = "out-of-band", Bpm = 140, CamelotKey = "8A" });
            await db.SaveChangesAsync();
        }

        await using var read = _db.Factory.CreateDbContext();
        var rows = await KnowledgeLifecycleRepository.GetCandidateFeaturesAsync(read, seed);

        Assert.Equal(500, rows.Count);
        Assert.Equal("exact", rows[0].TrackUniqueHash);
        Assert.Contains(rows, r => r.TrackUniqueHash == "t599"); // the closest of the 600, inserted last
        Assert.DoesNotContain(rows, r => r.TrackUniqueHash is "seed" or "out-of-band");
        Assert.All(rows, r => Assert.Equal(0.5f, r.InstrumentalProbability)); // projected: unloaded columns keep defaults

        var picked = TransitionCandidateFilter.SelectCandidates(seed, rows);
        Assert.Equal(new[] { "exact", "t599", "t598" }, picked.Take(3).Select(p => p.TrackUniqueHash));
    }
}
