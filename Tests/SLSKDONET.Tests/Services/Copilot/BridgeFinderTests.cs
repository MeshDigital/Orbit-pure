using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Copilot;
using Xunit;

namespace SLSKDONET.Tests.Services.Copilot;

/// <summary>Slice K7: exercises the Bridge Finder's recursive CTE against a seeded knowledge graph.</summary>
[Collection(NonParallelCollection.Name)]
public class BridgeFinderTests
{
    [Fact]
    public async Task FindBridgesAsync_ConnectsThroughVerifiedEdges()
    {
        using var scope = new FreshDatabaseScope();
        await Architecture.FreshDatabaseSchemaParityTests.CreateDatabaseService().InitAsync();

        using (var context = new AppDbContext())
        {
            context.TransitionKnowledge.AddRange(
                Edge("A", "B", 90),
                Edge("B", "C", 85),
                Edge("A", "D", 95),
                Edge("D", "E", 90), // no path to C
                Edge("X", "Y", 80));
            await context.SaveChangesAsync();
        }

        using (var context = new AppDbContext())
        {
            var paths = await BridgeFinder.FindBridgesAsync(context, "A", "C", new CopilotConstraints());

            var path = Assert.Single(paths);
            Assert.Equal(new[] { "A", "B", "C" }, path.TrackHashes);
            Assert.Equal(2, path.Depth);
            Assert.Equal(175.0, path.TotalScore, 6);
        }
    }

    [Fact]
    public async Task FindBridgesAsync_ExcludesUnverifiedOrIncompatibleEdges()
    {
        using var scope = new FreshDatabaseScope();
        await Architecture.FreshDatabaseSchemaParityTests.CreateDatabaseService().InitAsync();

        using (var context = new AppDbContext())
        {
            var unverified = Edge("A", "B", 90);
            unverified.Verified = false; // unverified → excluded
            context.TransitionKnowledge.AddRange(unverified, Edge("B", "C", 85));
            await context.SaveChangesAsync();
        }

        using (var context = new AppDbContext())
        {
            var paths = await BridgeFinder.FindBridgesAsync(context, "A", "C", new CopilotConstraints());
            Assert.Empty(paths);
        }
    }

    private static TransitionKnowledgeEntity Edge(string source, string target, double score) => new()
    {
        SourceTrackId = source,
        TargetTrackId = target,
        TransitionType = KnowledgeTransitionType.Blend,
        CompatibilityScore = score,
        HarmonicScore = 85,
        BpmScore = 85,
        ConfidenceLevel = KnowledgeConfidenceLevel.Likely,
        Verified = true,
    };
}
