using System;
using System.IO;
using Xunit;

namespace SLSKDONET.Tests.Architecture;

public class LibrarySidebarUnificationStartTests
{
    [Fact]
    public void LibraryPage_UsesExtractedPlaylistIntelligencePanelControl()
    {
        var xaml = ReadLibraryPageXaml();

        Assert.Contains("<Grid ColumnDefinitions=\"Auto,*\">", xaml);
        Assert.DoesNotContain("<GridSplitter Grid.Column=\"2\"", xaml);
        Assert.DoesNotContain("<Border Grid.Column=\"3\"", xaml);
        Assert.DoesNotContain("Text=\"Smart Insert Intelligence\"", xaml);
        Assert.DoesNotContain("<Grid IsVisible=\"{Binding IsLibrarySidebarIntelligenceMode}\" RowDefinitions=\"Auto,Auto,Auto,*\">", xaml);
    }

    [Fact]
    public void PlaylistIntelligencePanel_ExistsWithExpectedHeader()
    {
        var xaml = ReadPlaylistIntelligencePanelXaml();

        Assert.Contains("Text=\"Playlist Intelligence\"", xaml);
        Assert.Contains("Text=\"Smart Insert Settings\"", xaml);
        Assert.Contains("Command=\"{Binding SetSmartInsertStrictPresetCommand}\"", xaml);
        Assert.Contains("Text=\"{Binding LibraryIntelligencePlaylistTitle}\"", xaml);
        Assert.Contains("Command=\"{Binding SetLibraryIntelligenceTabCommand}\"", xaml);
    }

    [Fact]
    public void MainWindow_UsesExplicitLibraryInspectorContextTemplates()
    {
        var xaml = ReadMainWindowXaml();

        Assert.Contains("DataTemplate DataType=\"vmCore:LibraryDoubleInspectorViewModel\"", xaml);
        Assert.Contains("DataTemplate DataType=\"vmCore:PlaylistIntelligenceViewModel\"", xaml);
        Assert.Contains("DataTemplate DataType=\"vmCore:PlaylistTrackViewModel\"", xaml);
        Assert.Contains("<controls:DoubleInspectorPanel/>", xaml);
        Assert.Contains("<controls:PlaylistIntelligencePanel/>", xaml);
        Assert.Contains("Inspector content is unavailable for this selection.", xaml);
        Assert.DoesNotContain("TrackInspectorPanel", xaml);
        Assert.DoesNotContain("DataTemplate DataType=\"vmCore:LibraryViewModel\"", xaml);
    }

    [Fact]
    public void LibraryViewModel_DoesNotRetainLegacySidebarModeCompatibilityState()
    {
        var source = ReadLibraryViewModelSource();

        Assert.DoesNotContain("LibrarySidebarMode", source);
        Assert.DoesNotContain("IsLibrarySidebarPlayerMode", source);
        Assert.DoesNotContain("IsLibrarySidebarTrackInspectorMode", source);
        Assert.DoesNotContain("IsLibrarySidebarDoubleInspectorMode", source);
        Assert.DoesNotContain("IsLibrarySidebarIntelligenceMode", source);
        Assert.DoesNotContain("EvaluateSidebarMode(", source);
    }

    [Fact]
    public void LibraryViewModel_DoesNotRetainLegacyDoubleInspectorMirrorProperties()
    {
        var source = ReadLibraryViewModelSource();

        Assert.DoesNotContain("DoubleInspectorTrackA", source);
        Assert.DoesNotContain("DoubleInspectorTrackB", source);
        Assert.DoesNotContain("IsDoubleInspectorPairAnalyzable", source);
        Assert.DoesNotContain("IsDoubleInspectorPairScoreLoading", source);
        Assert.DoesNotContain("HasDoubleInspectorPairContext", source);
        Assert.DoesNotContain("DoubleInspectorHeaderTitle", source);
        Assert.DoesNotContain("DoubleInspectorKeyCompatibilitySummary", source);
        Assert.DoesNotContain("DoubleInspectorBpmDifferenceSummary", source);
        Assert.DoesNotContain("DoubleInspectorEnergyAlignmentSummary", source);
        Assert.DoesNotContain("DoubleInspectorTransitionScore", source);
        Assert.DoesNotContain("DoubleInspectorHarmonicScore", source);
        Assert.DoesNotContain("DoubleInspectorBeatScore", source);
        Assert.DoesNotContain("DoubleInspectorDropScore", source);
        Assert.DoesNotContain("DoubleInspectorReasonTags", source);
        Assert.DoesNotContain("DoubleInspectorTransitionStyleLabel", source);
        Assert.DoesNotContain("DoubleInspectorTransitionStyleReason", source);
    }

    [Fact]
    public void LibraryViewModel_DoesNotRetainLegacyIntelligenceTabMirrorBooleans()
    {
        var source = ReadLibraryViewModelSource();

        Assert.DoesNotContain("IsLibraryIntelligenceSmartInsertActive", source);
        Assert.DoesNotContain("IsLibraryIntelligenceSuggestNextActive", source);
        Assert.DoesNotContain("IsLibraryIntelligenceUpgradeActive", source);
        Assert.DoesNotContain("IsLibraryIntelligenceAutomixActive", source);
    }

    [Fact]
    public void SidebarInspectorLane_DoesNotUseServiceLocatorForSimilarityDependencies()
    {
        var doubleInspectorSource = ReadLibraryDoubleInspectorSource();
        var trackInspectorSource = ReadLibraryTrackInspectorSource();
        var intelligenceSource = ReadPlaylistIntelligenceSource();
        var eventsSource = ReadLibraryEventsSource();

        Assert.DoesNotContain("GetService(typeof(TrackSimilarityService))", doubleInspectorSource);
        Assert.DoesNotContain("GetService(typeof(TransitionStyleClassifier))", doubleInspectorSource);
        Assert.DoesNotContain("GetService(typeof(SimilarityIndex))", trackInspectorSource);
        Assert.DoesNotContain("GetService(typeof(TrackSimilarityService))", intelligenceSource);
        Assert.DoesNotContain("GetService(typeof(TrackSimilarityService))", eventsSource);
    }

    [Fact]
    public void SidebarLane_DoesNotRetainStaleParentForwardingNotifications()
    {
        var librarySource = ReadLibraryViewModelSource();
        var intelligenceSource = ReadPlaylistIntelligenceSource();
        var trackInspectorSource = ReadLibraryTrackInspectorSource();

        Assert.DoesNotContain("OnSuggestNextCandidatesCollectionChanged", librarySource);
        Assert.DoesNotContain("OnPlaylistUpgradeCandidatesCollectionChanged", librarySource);

        Assert.DoesNotContain("_library.OnPropertyChanged(nameof(LibraryViewModel.SmartInsertFromLabel))", intelligenceSource);
        Assert.DoesNotContain("_library.OnPropertyChanged(nameof(LibraryViewModel.IsSuggestNextLoading))", intelligenceSource);
        Assert.DoesNotContain("_library.OnPropertyChanged(nameof(LibraryViewModel.IsPlaylistUpgradeLoading))", intelligenceSource);

        Assert.DoesNotContain("_library.OnPropertyChanged(nameof(LibraryViewModel.TrackExplainabilitySummary))", trackInspectorSource);
        Assert.DoesNotContain("_library.OnPropertyChanged(nameof(LibraryViewModel.HasSimilarTracksPreview))", trackInspectorSource);
    }

    [Fact]
    public void LibraryViewModel_DoesNotRetainSmartInsertShimWrapperMethods()
    {
        var source = ReadLibraryViewModelSource();

        Assert.DoesNotContain("private void SetSmartInsertPairContext", source);
        Assert.DoesNotContain("private void ResetSmartInsertPairContext", source);
        Assert.DoesNotContain("private void SetSmartInsertPreparationHint", source);
        Assert.DoesNotContain("private void ClearSmartInsertPreparationHint", source);
    }

    [Fact]
    public void LibraryTrackInspectorViewModel_DoesNotRetainDeadForwardingNoOps()
    {
        var source = ReadLibraryTrackInspectorSource();
        var librarySource = ReadLibraryViewModelSource();

        Assert.DoesNotContain("OnSimilarTracksPreviewCollectionChanged", source);
        Assert.DoesNotContain("RaiseStateChanged", source);
        Assert.DoesNotContain("IDisposable", source);
        Assert.DoesNotContain("TrackInspector.Dispose();", librarySource);
    }

    [Fact]
    public void LibrarySidebarLane_DoesNotRetainStaleClosureLanguageMarkers()
    {
        var eventsSource = ReadLibraryEventsSource();
        var commandsSource = ReadLibraryCommandsSource();

        Assert.DoesNotContain("Legacy: In-Memory Smart Playlists", eventsSource);
        Assert.DoesNotContain("CS8618 Fix: Initialize with null!", commandsSource);
    }

    [Fact]
    public void LibraryEvents_PublishesExplicitInspectorWrapperContexts()
    {
        var eventsSource = ReadLibraryEventsSource();

        Assert.Contains("OpenInspectorEvent.Create(DoubleInspector, \"Library.TrackSelection.Double\")", eventsSource);
        Assert.Contains("OpenInspectorEvent.Create(Intelligence, \"Library.TrackSelection.EmptyIntelligence\")", eventsSource);
        Assert.Contains("OpenInspectorEvent.Create(single, \"Library.TrackSelection.Single\")", eventsSource);
        Assert.Contains("OpenInspectorEvent.Create(Intelligence, \"Library.ProjectSelection.EmptyIntelligence\")", eventsSource);
        Assert.Contains("ReactiveUI.MessageBus.Current.SendMessage(new CloseInspectorEvent());", eventsSource);
        Assert.DoesNotContain("new OpenInspectorEvent(this, \"DOUBLE INSPECTOR\", \"🔗\")", eventsSource);
        Assert.DoesNotContain("new OpenInspectorEvent(this, \"INTELLIGENCE\", \"🧠\")", eventsSource);
        Assert.DoesNotContain("new OpenInspectorEvent(single, source: \"Library.TrackSelection.Single\")", eventsSource);
    }

    [Fact]
    public void LibraryEvents_SelectionFlow_RoutesThroughChildInspectorOwners()
    {
        var eventsSource = ReadLibraryEventsSource();

        Assert.Contains("_ = DoubleInspector.HandleSelectionChangedAsync(selectedTracks);", eventsSource);
        Assert.Contains("_ = TrackInspector.TryAttachEnhancementsAsync(single);", eventsSource);
        Assert.Contains("TrackInspector.ClearEnhancements();", eventsSource);
        Assert.Contains("_ = Intelligence.RefreshSuggestNextCandidatesAsync();", eventsSource);
        Assert.Contains("_ = Intelligence.RefreshPlaylistUpgradeCandidatesAsync();", eventsSource);
    }

    [Fact]
    public void SimilarTracks_PrimesFromDedicatedInspectorViewModels()
    {
        var source = ReadSimilarTracksSource();

        Assert.Contains("case LibraryDoubleInspectorViewModel", source);
        Assert.Contains("case PlaylistIntelligenceViewModel", source);
        Assert.DoesNotContain("case LibraryDoubleInspectorContext", source);
        Assert.DoesNotContain("case PlaylistIntelligenceInspectorContext", source);
    }

    [Fact]
    public void MainViewModel_HandlesCloseInspectorEvent()
    {
        var source = ReadMainViewModelSource();

        Assert.Contains("ShouldApplyInspectorPayload(evt.ViewModel)", source);
        Assert.Contains("NormalizeInspectorOpenSource(evt.Source)", source);
        Assert.Contains("ShouldApplyInspectorOpenForCurrentPage(source, CurrentPageType)", source);
        Assert.Contains("Listen<SLSKDONET.Events.CloseInspectorEvent>()", source);
        Assert.Contains("_rightPanelService.ClosePanel();", source);
    }

    [Fact]
    public void OpenInspectorEvent_UsesSharedPresentationResolver()
    {
        var source = ReadOpenInspectorEventSource();

        Assert.Contains("ResolvePresentationDefaults", source);
        Assert.Contains("Create(object viewModel, string? source = null)", source);
        Assert.DoesNotContain("string title = \"INSPECTOR\"", source);
        Assert.DoesNotContain("string icon = \"ℹ️\"", source);
    }

    [Fact]
    public void LibraryPage_UsesCardVmProjectListBindings()
    {
        var xaml = ReadLibraryPageXaml();

        // Playlist Folders: the sidebar list is now a nested tree (RootTreeNodes) whose leaves
        // wrap LibraryPlaylistCardViewModel via PlaylistTreeCardNodeViewModel.Card, rather than
        // binding ItemsSource directly to the flat FilteredProjectCards collection.
        Assert.Contains("ItemsSource=\"{Binding Projects.RootTreeNodes}\"", xaml);
        Assert.Contains("SelectedItem=\"{Binding Projects.SelectedTreeNode, Mode=TwoWay}\"", xaml);
        Assert.DoesNotContain("ItemsSource=\"{Binding Projects.FilteredProjects}\"", xaml);
    }

    [Fact]
    public void CompactPlaylistTemplate_UsesCardVmAndMosaicCoverBinding()
    {
        var xaml = ReadCompactPlaylistTemplateXaml();

        Assert.Contains("x:DataType=\"vm:LibraryPlaylistCardViewModel\"", xaml);
        Assert.Contains("<Image Source=\"{Binding CoverBitmap}\" Stretch=\"UniformToFill\"/>", xaml);
        Assert.Contains("CommandParameter=\"{Binding Model}\"", xaml);
        Assert.DoesNotContain("x:DataType=\"models:PlaylistJob\"", xaml);
        Assert.DoesNotContain("DisplayArtUrl, Converter={StaticResource BitmapValueConverter}", xaml);
    }

    [Fact]
    public void DocumentationIndex_NoLongerTracksRemovedSidebarUnificationSprawl()
    {
        // The 219-file LIBRARY_SIDEBAR_UNIFICATION_* governance/checklist chain this test used
        // to assert the presence of was itself the sprawl problem (runaway autonomous-loop
        // process documentation about a single UI change) — removed 2026-08-10 along with the
        // 131-file .agent/queues/ chain. DOCUMENTATION_INDEX.md was rewritten with every link
        // verified against tracked files. This test now guards against the sprawl coming back.
        var source = ReadDocumentationIndexSource();

        Assert.DoesNotContain("LIBRARY_SIDEBAR_UNIFICATION_ANNOTATION", source);
        Assert.DoesNotContain("LIBRARY_SIDEBAR_UNIFICATION_GOVERNANCE", source);
        Assert.DoesNotContain("LIBRARY_SIDEBAR_UNIFICATION_CLOSURE", source);
    }

    private static string ReadLibraryPageXaml()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "Avalonia", "LibraryPage.axaml");
        Assert.True(File.Exists(filePath), $"Expected library view at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadPlaylistIntelligencePanelXaml()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "Avalonia", "Controls", "PlaylistIntelligencePanel.axaml");
        Assert.True(File.Exists(filePath), $"Expected playlist intelligence panel at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadMainWindowXaml()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "Avalonia", "MainWindow.axaml");
        Assert.True(File.Exists(filePath), $"Expected main window view at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryEventsSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryViewModel.Events.cs");
        Assert.True(File.Exists(filePath), $"Expected library events source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryViewModelSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected library view model source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryCommandsSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryViewModel.Commands.cs");
        Assert.True(File.Exists(filePath), $"Expected library commands source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryDoubleInspectorSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryDoubleInspectorViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected double inspector source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadLibraryTrackInspectorSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "LibraryTrackInspectorViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected track inspector source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadPlaylistIntelligenceSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "PlaylistIntelligenceViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected playlist intelligence source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadCompactPlaylistTemplateXaml()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "Avalonia", "Controls", "CompactPlaylistTemplate.axaml");
        Assert.True(File.Exists(filePath), $"Expected compact playlist template view at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadSimilarTracksSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "ViewModels", "SimilarTracksViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected similar tracks source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadDocumentationIndexSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "DOCUMENTATION_INDEX.md");
        Assert.True(File.Exists(filePath), $"Expected documentation index at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadMainViewModelSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Views", "MainViewModel.cs");
        Assert.True(File.Exists(filePath), $"Expected main view model source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string ReadOpenInspectorEventSource()
    {
        var sourceRoot = FindSourceRoot();
        Assert.False(string.IsNullOrWhiteSpace(sourceRoot));

        var filePath = Path.Combine(sourceRoot, "Events", "OpenInspectorEvent.cs");
        Assert.True(File.Exists(filePath), $"Expected inspector event source at {filePath}");

        return File.ReadAllText(filePath);
    }

    private static string FindSourceRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (File.Exists(Path.Combine(dir, "SLSKDONET.csproj")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        var candidate = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(candidate, "SLSKDONET.csproj")))
        {
            return candidate;
        }

        return string.Empty;
    }
}
