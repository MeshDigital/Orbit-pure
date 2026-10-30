using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Copilot;
using Xunit;

namespace SLSKDONET.Tests.Services.Copilot;

/// <summary>Slice K7: verifies the orchestrator hydrates entities and delegates to the static engines.</summary>
[Collection(NonParallelCollection.Name)]
public class OrbitCopilotTests
{
    [Fact]
    public async Task FindBridgesAsync_HydratesAndDelegates()
    {
        using var scope = new FreshDatabaseScope();
        await Architecture.FreshDatabaseSchemaParityTests.CreateDatabaseService().InitAsync();

        using (var context = new AppDbContext())
        {
            context.TransitionKnowledge.AddRange(Edge("A", "B", 0.9), Edge("B", "C", 0.9));
            await context.SaveChangesAsync();
        }

        var copilot = new OrbitCopilot(new TestDbContextFactory());
        var paths = await copilot.FindBridgesAsync("A", "C");

        var path = Assert.Single(paths);
        Assert.Equal(new[] { "A", "B", "C" }, path.TrackHashes);
    }

    [Fact]
    public async Task BuildSetAsync_HydratesCandidatesAndReturnsSpine()
    {
        using var scope = new FreshDatabaseScope();
        await Architecture.FreshDatabaseSchemaParityTests.CreateDatabaseService().InitAsync();

        using (var context = new AppDbContext())
        {
            context.AudioFeatures.AddRange(
                Features("A", bpm: 120, energy: 0.4f, duration: 60),
                Features("B", bpm: 120, energy: 0.6f, duration: 60),
                Features("C", bpm: 120, energy: 0.8f, duration: 60));
            context.TransitionKnowledge.AddRange(Edge("A", "B", 0.9), Edge("B", "C", 0.9));
            await context.SaveChangesAsync();
        }

        var copilot = new OrbitCopilot(new TestDbContextFactory());
        var result = await copilot.BuildSetAsync(new SetBuildRequest(TargetMinutes: 3, EventProfile: null, MaxBpmVariance: 0.08));

        Assert.NotEmpty(result.SpineTrackHashes);
        Assert.Equal("A", result.SpineTrackHashes[0]);
    }

    [Fact]
    public async Task DetectClashesAsync_HydratesAndDetectsHarmonicClash()
    {
        using var scope = new FreshDatabaseScope();
        await Architecture.FreshDatabaseSchemaParityTests.CreateDatabaseService().InitAsync();

        Guid setId;
        using (var context = new AppDbContext())
        {
            var set = new SetListEntity { Name = "Clash Test" };
            set.Tracks.Add(new SetTrackEntity { TrackUniqueHash = "A", Position = 0 });
            set.Tracks.Add(new SetTrackEntity { TrackUniqueHash = "B", Position = 1 });
            context.SetLists.Add(set);
            await context.SaveChangesAsync();
            setId = set.Id;

            var a = Features("A", bpm: 120, energy: 0.4f, duration: 60);
            a.CamelotKey = "8A";
            var b = Features("B", bpm: 120, energy: 0.4f, duration: 60);
            b.CamelotKey = "3A";
            context.AudioFeatures.AddRange(a, b);
            await context.SaveChangesAsync();
        }

        var copilot = new OrbitCopilot(new TestDbContextFactory());
        var clashes = await copilot.DetectClashesAsync(setId);

        Assert.Contains(clashes, c => c.Reason == ClashDetector.HarmonicDissonanceReason);
    }

    private static TransitionKnowledgeEntity Edge(string source, string target, double confidence) => new()
    {
        SourceTrackId = source,
        TargetTrackId = target,
        TransitionType = KnowledgeTransitionType.Blend,
        CompatibilityScore = 85,
        HarmonicScore = 85,
        BpmScore = 85,
        ConfidenceScore = confidence,
        ConfidenceLevel = KnowledgeConfidenceLevel.Likely,
        Verified = true,
    };

    private static AudioFeaturesEntity Features(string hash, float bpm, float energy, double duration) => new()
    {
        TrackUniqueHash = hash,
        Bpm = bpm,
        Energy = energy,
        TrackDuration = duration,
        VocalDensity = 0f,
    };

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new AppDbContext();

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext());
    }
}
