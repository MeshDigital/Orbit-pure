using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// NAudio's <c>WdlResamplingSampleProvider</c> corrupts its output when read at a non-zero buffer
/// offset (the mix bus crackled on every 48 kHz device in 0.5.2-beta.2). Every resampler must go
/// through <c>OffsetSafeResampler.Create</c>, the one file allowed to construct it.
/// </summary>
public class ResamplerGuardTests
{
    [Fact]
    public void Resamplers_AreOnlyCreatedThroughOffsetSafeResampler()
    {
        var root = RepoGuard.Root;
        var offenders = new[] { "Services", "ViewModels", "Views", "Engine", "Companions" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !f.EndsWith("OffsetSafeResampler.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => File.ReadAllText(f).Contains("new WdlResamplingSampleProvider(")
                        || File.ReadAllText(f).Contains("SampleProviders.WdlResamplingSampleProvider("))
            .Select(RepoGuard.Rel)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Create resamplers with OffsetSafeResampler.Create (NAudio's WDL resampler crackles when read at an offset): "
            + string.Join(", ", offenders));
    }
}
