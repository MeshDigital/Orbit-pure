using System;
using System.IO;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// Architecture guards for Epic #517 Phase 3 — #533 (FO3e: slim Transitions editor, shared
/// MixTransitionViewModel, reads cues and never writes them, pair list uses transition labels).
/// </summary>
public class TransitionsEditorSlimGuardTests
{
    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SLSKDONET.csproj")))
            dir = dir.Parent;
        var full = Path.Combine(dir!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Expected file at {full}");
        return File.ReadAllText(full);
    }

    [Fact]
    public void FlowBuilderPairList_ShowsTransitionLabel_AndNoNumericScores()
    {
        var xaml = ReadRepoFile("Views/Avalonia/FlowBuilderView.axaml");
        Assert.Contains("TransitionLabel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Bridge.ScorePercent", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Bridge.ScoreDisplay", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TransitionsEditor_DoesNotExposeInlineCueEditingToggle()
    {
        var xaml = ReadRepoFile("Views/Avalonia/Controls/FlowTransitionEditorPanel.axaml");
        Assert.DoesNotContain("Text=\"Edit cues\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsChecked=\"{Binding IsCueEditMode}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AllTransitionEditorEntryPoints_ShareMixTransitionViewModel()
    {
        var flowEditor = ReadRepoFile("Views/Avalonia/Controls/FlowTransitionEditorPanel.axaml");
        var window = ReadRepoFile("Views/Avalonia/TransitionEditorWindow.axaml");
        var mainWindow = ReadRepoFile("Views/Avalonia/MainWindow.axaml");

        Assert.Contains("MixTransitionVm", flowEditor, StringComparison.Ordinal);
        Assert.Contains("MixTransitionPanel", window, StringComparison.Ordinal);
        Assert.Contains("MixTransitionPanel", mainWindow, StringComparison.Ordinal);
    }
}
