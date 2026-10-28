using System;
using System.IO;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

/// <summary>Responsive sidepanel layout guards. These protect reflow, not feature removal:
/// every existing tab and Inspector action remains present at narrow widths.</summary>
public class LibrarySidepanelResponsiveLayoutTests
{
    [Fact]
    public void ContextPanelTabs_WrapInsteadOfClippingAtNarrowWidths()
    {
        var xaml = ReadSource("Views", "Avalonia", "MainWindow.axaml");

        Assert.Contains("x:Name=\"ContextPanelTabs\"", xaml);
        Assert.Contains("<TabControl.ItemsPanel>", xaml);
        Assert.Contains("<WrapPanel Orientation=\"Horizontal\"/>", xaml);

        foreach (var tab in new[] { "Inspector", "Similar", "Playing", "Notifications", "Mix", "Discover" })
            Assert.Contains($"<TabItem Header=\"{tab}\">", xaml);
    }

    [Fact]
    public void LibraryInspectorActions_ReflowWithoutRemovingAnyAction()
    {
        var xaml = ReadSource("Views", "Avalonia", "Controls", "LibraryTrackInspector.axaml");

        Assert.Contains("<WrapPanel x:Name=\"InspectorActionBar\"", xaml);
        foreach (var command in new[]
                 {
                     "PlayCommand", "SearchAgainCommand", "OpenInCueForgeCommand", "AnalyzeTrackCommand",
                     "RevealFileCommand", "RequestEditTagsCommand", "AddToProjectCommand",
                     "OpenWorkstationCommand", "OpenInEditLabCommand", "SeparateStemsCommand", "RequestRemoveCommand",
                 })
        {
            Assert.Contains($"Command=\"{{Binding {command}}}\"", xaml);
        }
    }

    [Fact]
    public void TrackSurfaces_WidePreparationColumnUsesExistingRepairActions()
    {
        foreach (var xaml in new[]
                 {
                     ReadSource("Views", "Avalonia", "TrackListView.axaml"),
                     ReadSource("Views", "Avalonia", "Controls", "LibraryPlaylistTrackSurface.axaml"),
                 })
        {
            Assert.Contains("ShowPreparationColumn", xaml);
            Assert.Contains("Text=\"PREP\"", xaml);
            Assert.Contains("Content=\"{Binding PreparationStatus}\"", xaml);
            Assert.Contains("Command=\"{Binding PreparationRepairCommand}\"", xaml);
            Assert.Contains("ToolTip.Tip=\"{Binding PreparationTooltip}\"", xaml);
        }
    }

    [Fact]
    public void FlowBuilderHandoff_PreservesPlaylistWithoutRoutingThroughWorkstation()
    {
        var source = ReadSource("ViewModels", "LibraryViewModel.Commands.cs");
        var start = source.IndexOf("private void ExecuteOpenFlowBuilder()", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task ExecuteCombineSelectedPlaylistsAsync", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Could not isolate the Flow Builder handoff.");
        var method = source[start..end];

        Assert.Contains("new OpenFlowBuilderForPlaylistEvent(playlistId)", method);
        Assert.Contains("playlist?.Id ?? Guid.Empty", method);
        Assert.DoesNotContain("AddToTimelineRequestEvent", method);
        Assert.DoesNotContain("Build Set", method);
    }

    [Fact]
    public void DefaultTrackGrid_SupportsSelectAllAndKeyboardActivation()
    {
        var grid = ReadSource("Views", "Avalonia", "Controls", "VirtualGrid.axaml.cs");
        var library = ReadSource("Views", "Avalonia", "TrackListView.axaml.cs");

        Assert.Contains("e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control", grid);
        Assert.Contains("case Key.Enter:", grid);
        Assert.Contains("ItemActivated?.Invoke(this, currentIndex)", grid);
        Assert.Contains("grid.ItemActivated +=", library);
        Assert.Contains("vm.Operations?.PlayTrackCommand", library);
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "SLSKDONET.csproj")))
            {
                var path = Path.Combine(new[] { directory }.Concat(segments).ToArray());
                Assert.True(File.Exists(path), $"Expected source file at {path}");
                return File.ReadAllText(path);
            }
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the ORBIT source root.");
    }
}
