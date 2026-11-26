using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// Epic #517 / FO1e (#522): Core transition resolution, storage, planning, playback, and the
/// Transitions (Flow Builder) editor must never read or depend on the quarantined knowledge
/// lifecycle tables/services (which are reserved for Sets and Copilot).
/// </summary>
public sealed class TransitionKnowledgeQuarantineGuardTests
{
    private static readonly string[] ForbiddenTokens =
    {
        "ITransitionRatingService",
        "TransitionRatingService",
        "TransitionKnowledge",
        "TransitionEvidence",
        "TransitionCandidates",
    };

    private static readonly string[] CoreFiles =
    {
        "Services/Transitions/ITransitionResolver.cs",
        "Services/Transitions/TransitionPlanService.cs",
        "Services/Repositories/TransitionRepository.cs",
        "ViewModels/PlayerViewModel.cs",
        "ViewModels/PlayerViewModel.QueueAndTransitions.cs",
        "ViewModels/PlayerViewModel.TransportAndSummaries.cs",
        "ViewModels/FlowBuilderViewModel.cs",
    };

    [Fact]
    public void CoreTransitionAndPlaybackTypes_DoNotDependOnKnowledgeLifecycle()
    {
        foreach (var relativePath in CoreFiles)
        {
            var fullPath = Path.Combine(RepoGuard.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"Expected core file not found: {relativePath}");

            var codeLines = File.ReadAllLines(fullPath)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
            var code = string.Join("\n", codeLines);

            foreach (var token in ForbiddenTokens)
            {
                Assert.DoesNotContain(token, code, StringComparison.Ordinal);
            }
        }
    }
}
