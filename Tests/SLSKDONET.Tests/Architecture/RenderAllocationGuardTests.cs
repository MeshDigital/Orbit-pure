using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media;
using SLSKDONET.Views.Avalonia.Controls;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// #318 / CLAUDE.md "Don't allocate brushes/pens in Render; cache them": the waveform's per-frame drawing methods
/// must not create brushes, pens or laid-out text. They use <see cref="RenderResourceCache"/> and FormattedTextCache.
/// </summary>
public class RenderAllocationGuardTests
{
    private static readonly string[] PerFrameMethods =
    {
        "RenderInternal", "RenderPhraseSegments", "ResolvePhraseVisuals", "RenderCurves", "RenderCurve", "RenderCues",
    };

    private static readonly Regex Allocation = new(@"new\s+(Pen|SolidColorBrush|FormattedText|Typeface)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void WaveformControl_PerFrameMethods_AllocateNoBrushesPensOrText()
    {
        var source = File.ReadAllText(Path.Combine(RepoGuard.Root, "Views", "Avalonia", "Controls", "WaveformControl.cs"));
        var offenders = PerFrameMethods
            .SelectMany(m => Allocation.Matches(MethodBody(source, m)).Select(x => $"{m}: {x.Value}"))
            .ToList();
        Assert.True(offenders.Count == 0, "Per-frame allocations in WaveformControl:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Cache_ReturnsTheSameImmutableObjects()
    {
        var a = RenderResourceCache.Brush(Colors.Red, 0.5);
        Assert.Same(a, RenderResourceCache.Brush(Colors.Red, 0.5));
        Assert.Same(RenderResourceCache.Pen(Colors.Red, 0.5, 2), RenderResourceCache.Pen(Colors.Red, 0.5, 2));
        Assert.NotSame(RenderResourceCache.Pen(Colors.Red, 0.5, 2), RenderResourceCache.Pen(Colors.Red, 0.5, 3));
        Assert.Equal(Colors.White, RenderResourceCache.ParseColor("not a colour", Colors.White));
        Assert.Equal(Color.Parse("#553D2E"), RenderResourceCache.ParseColor("#553D2E", Colors.White));
    }

    /// <summary>The text of a method from its signature to its matching closing brace.</summary>
    private static string MethodBody(string source, string name)
    {
        var match = Regex.Match(source, $@"\b{name}\s*\([^)]*\)\s*\{{");
        Assert.True(match.Success, $"{name} not found in WaveformControl.cs");
        int depth = 0;
        for (int i = match.Index + match.Length - 1; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[match.Index..(i + 1)];
        }
        return source[match.Index..];
    }
}
