using System;
using System.IO;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>
/// Architecture guards for Epic #517 Phase 4 — #539 (Analysis leaves sidebar, stays reachable via
/// top-bar status pill and Library) and #540 (Right sidebar: Inspector &amp; Similar+Discover always,
/// Mix only while mixing, no emoji icons).
/// </summary>
public class AnalysisAndRightSidebarGuardTests
{
    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SLSKDONET.csproj")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void Analysis_InLeftSidebar_AndReachableViaTopBarPillAndLibrary()
    {
        var mainWindow = ReadRepoFile("Views/Avalonia/MainWindow.axaml");
        var libraryPage = ReadRepoFile("Views/Avalonia/LibraryPage.axaml");

        Assert.Contains("x:Name=\"NavAnalysis\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding NavigateAnalysisCommand}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("NavigateAnalysisCommand", libraryPage, StringComparison.Ordinal);
    }

    [Fact]
    public void RightSidebar_HasInspectorSimilarAndConditionalMix_NoPlayingNotificationsDiscoverTabs()
    {
        var mainWindow = ReadRepoFile("Views/Avalonia/MainWindow.axaml");

        Assert.Contains("Header=\"Similar\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding Sidebar.IsMixTabVisible}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("PlaylistDiscoveryPanel", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("<TabItem Header=\"Playing\">", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("<TabItem Header=\"Notifications\">", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("<TabItem Header=\"Discover\">", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void SidebarViewModel_UsesNoEmojiIcons()
    {
        var code = ReadRepoFile("ViewModels/SidebarViewModel.cs");
        foreach (var emoji in new[] { "🎛", "🔬", "🔗", "🎵", "🔔", "✨" })
        {
            Assert.DoesNotContain(emoji, code, StringComparison.Ordinal);
        }
    }
}
