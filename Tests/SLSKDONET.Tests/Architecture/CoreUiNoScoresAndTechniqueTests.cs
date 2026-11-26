using System;
using System.IO;
using System.Linq;
using SLSKDONET.Models.Timeline;
using SLSKDONET.Services.Timeline;
using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// Guard & contract tests for Epic #517 Phase 3 — #531 (FO3c: Technique dropdown) and
/// #532 (FO3d: no transition grades, ratings, or compatibility percentages on core screens).
/// </summary>
public class CoreUiNoScoresAndTechniqueTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SLSKDONET.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }

    [Theory]
    [InlineData("Auto", TransitionType.Crossfade)]
    [InlineData("Cut", TransitionType.Cut)]
    [InlineData("Fade", TransitionType.Crossfade)]
    [InlineData("EchoOut", TransitionType.EchoOut)]
    [InlineData("Rise", TransitionType.FilterSweep)]
    [InlineData("Blend", TransitionType.EqSwap)]
    public void BuiltInTechniques_MapToRealAudibleTransitionModels(string presetName, TransitionType expectedType)
    {
        var model = TransitionPresetLibrary.Build(presetName);
        Assert.Equal(expectedType, model.Type);
        Assert.True(model.DurationBeats > 0);
    }

    [Theory]
    [InlineData("Cut", false, "Cut")]
    [InlineData("Fade", false, "Crossfade")]
    [InlineData("EchoOut", false, "EchoOut")]
    [InlineData("Melt", false, "EchoOut")]
    [InlineData("Rise", false, "FilterSweep")]
    [InlineData("Blend", false, "EqSwap")]
    [InlineData("Custom", true, "Loop")]
    [InlineData("Auto", false, "Auto")]
    [InlineData("Rolling", false, "Auto")]
    public void TechniqueKeyMapping_RoundTripsLegacyAndCanonicalNames(string presetName, bool loopEnabled, string expectedKey)
    {
        Assert.Equal(expectedKey, MixTransitionViewModel.MapPresetToTechniqueKey(presetName, loopEnabled));
    }

    [Theory]
    [InlineData("Views/Avalonia/FlowBuilderView.axaml", "SuggestedFlowScoreDisplay")]
    [InlineData("Views/Avalonia/Controls/FlowTransitionEditorPanel.axaml", "ActiveBridge.ScoreDisplay")]
    [InlineData("Views/Avalonia/Controls/FlowTransitionEditorPanel.axaml", "ActiveBridge.ScorePercent")]
    [InlineData("Views/Avalonia/Controls/PlaylistIntelligencePanel.axaml", "BaseScoreDisplay")]
    [InlineData("Views/Avalonia/Dialogs/SuggestedFlowImpactDialog.axaml", "AverageTransitionScoreDisplay")]
    public void CoreUiPages_DoNotDisplayTransitionScoresOrCompatibilityPercentages(string relativePath, string forbiddenBinding)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(fullPath), $"Expected file at {fullPath}");
        var xaml = File.ReadAllText(fullPath);
        Assert.DoesNotContain(forbiddenBinding, xaml, StringComparison.Ordinal);
    }
}
