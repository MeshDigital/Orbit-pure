using SLSKDONET.Models;
using SLSKDONET.Events;
using SLSKDONET.Views;
using Avalonia.Controls;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class MainViewModelNavigationTests
{
    [Theory]
    [InlineData(typeof(SLSKDONET.Views.Avalonia.ImportPage), PageType.Import)]
    [InlineData(typeof(SLSKDONET.Views.Avalonia.ImportPreviewPage), PageType.Import)]
    [InlineData(typeof(SLSKDONET.Views.Avalonia.SearchPage), PageType.Search)]
    [InlineData(typeof(SLSKDONET.Views.Avalonia.NowPlayingPage), PageType.NowPlaying)]
    [InlineData(typeof(SLSKDONET.Views.Avalonia.WorkstationPage), PageType.Workstation)]
    public void ResolvePageType_MapsKnownViews(Type pageType, PageType expected)
    {
        var resolved = MainViewModel.ResolvePageType(pageType, PageType.Home);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolvePageType_UsesFallbackForUnknownViews()
    {
        var resolved = MainViewModel.ResolvePageType(typeof(TestPage), PageType.Settings);

        Assert.Equal(PageType.Settings, resolved);
    }

    [Fact]
    public void OverlaySectionHelpers_ReportImportWithinAcquireGroup()
    {
        Assert.True(MainViewModel.IsAcquireOverlayPage(PageType.Import));
        Assert.False(MainViewModel.IsSystemOverlayPage(PageType.Import));
        Assert.False(MainViewModel.IsCreativeOverlayPage(PageType.Import));
    }

    [Fact]
    public void OverlaySectionHelpers_ReportWorkstationWithinCreativeGroup()
    {
        Assert.True(MainViewModel.IsCreativeOverlayPage(PageType.Workstation));
        Assert.False(MainViewModel.IsAcquireOverlayPage(PageType.Workstation));
    }

    [Theory]
    [InlineData("Player", true)]
    [InlineData("player", true)]
    [InlineData("NowPlaying", true)]
    [InlineData("nowplaying", true)]
    [InlineData("Workstation", false)]
    [InlineData("Library", false)]
    [InlineData(null, false)]
    public void ShouldRemapToWorkstationDestination_MatchesExpectedAliases(string? pageName, bool expected)
    {
        var shouldRemap = MainViewModel.ShouldRemapToWorkstationDestination(pageName);

        Assert.Equal(expected, shouldRemap);
    }

    [Theory]
    [InlineData("Library.TrackSelection.Single", "Library.TrackSelection.Single")]
    [InlineData(" Search.Selection.Single ", "Search.Selection.Single")]
    [InlineData("", "Unknown")]
    [InlineData("   ", "Unknown")]
    [InlineData(null, "Unknown")]
    public void NormalizeInspectorOpenSource_ReturnsExpectedValue(string? source, string expected)
    {
        var normalized = MainViewModel.NormalizeInspectorOpenSource(source);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldApplyInspectorPayload_ReturnsExpectedNullGuard(bool hasPayload)
    {
        var payload = hasPayload ? new object() : null;

        var shouldApply = MainViewModel.ShouldApplyInspectorPayload(payload);

        Assert.Equal(hasPayload, shouldApply);
    }

    [Theory]
    [InlineData("Library.TrackSelection.Double", "DOUBLE INSPECTOR", "🔗")]
    [InlineData("Library.TrackSelection.Single", "TRACK INSPECTOR", "🔍")]
    [InlineData("Library.TrackSelection.EmptyIntelligence", "INTELLIGENCE", "🧠")]
    [InlineData("Library.ProjectSelection.EmptyIntelligence", "INTELLIGENCE", "🧠")]
    [InlineData("Search.Selection.Single", "TRACK INSPECTOR", "🔍")]
    [InlineData("Downloads.Selection.Single", "TRACK INSPECTOR", "🔍")]
    [InlineData("FlowBuilder.TransitionInspector", "TRACK INSPECTOR", "🔬")]
    [InlineData(null, "INSPECTOR", "ℹ️")]
    [InlineData("External.Plugin.Source", "INSPECTOR", "ℹ️")]
    public void ResolvePresentationDefaults_UsesExpectedTitleAndIcon(string? source, string expectedTitle, string expectedIcon)
    {
        var presentation = OpenInspectorEvent.ResolvePresentationDefaults(source);

        Assert.Equal(expectedTitle, presentation.Title);
        Assert.Equal(expectedIcon, presentation.Icon);
    }

    [Theory]
    [InlineData("Library.TrackSelection.Single", PageType.Library, true)]
    [InlineData("Library.TrackSelection.Single", PageType.Search, false)]
    [InlineData("Search.Selection.Single", PageType.Search, true)]
    [InlineData("Search.Selection.Single", PageType.Library, false)]
    [InlineData("Downloads.Selection.Single", PageType.Projects, true)]
    [InlineData("Downloads.Selection.Single", PageType.Library, false)]
    [InlineData("FlowBuilder.TransitionInspector", PageType.Workstation, true)]
    [InlineData("FlowBuilder.TransitionInspector", PageType.Decks, true)]
    [InlineData("FlowBuilder.TransitionInspector", PageType.Library, false)]
    [InlineData("Unknown", PageType.Search, true)]
    [InlineData(null, PageType.Analysis, true)]
    [InlineData("External.Plugin.Source", PageType.Library, true)]
    public void ShouldApplyInspectorOpenForCurrentPage_ReturnsExpectedEligibility(string? source, PageType currentPageType, bool expected)
    {
        var shouldApply = MainViewModel.ShouldApplyInspectorOpenForCurrentPage(source, currentPageType);

        Assert.Equal(expected, shouldApply);
    }

    [Theory]
    [InlineData(1023.9, SplitViewDisplayMode.Overlay)]
    [InlineData(1024.0, SplitViewDisplayMode.Inline)]
    [InlineData(1800.0, SplitViewDisplayMode.Inline)]
    [InlineData(599.0, SplitViewDisplayMode.Overlay)]
    public void ResolveSidebarDisplayMode_UsesExpectedThreshold(double width, SplitViewDisplayMode expected)
    {
        var mode = MainViewModel.ResolveSidebarDisplayMode(width);

        Assert.Equal(expected, mode);
    }

    [Fact]
    public void ShouldCloseInspectorOnRouteTransition_ReturnsFalse_WhenNoCurrentInspectorVm()
    {
        var shouldClose = MainViewModel.ShouldCloseInspectorOnRouteTransition(
            PageType.Library,
            PageType.Search,
            currentPanelVm: null,
            playerPanelVm: new object());

        Assert.False(shouldClose);
    }

    [Fact]
    public void ShouldCloseInspectorOnRouteTransition_ReturnsFalse_WhenCurrentVmIsPlayerFallback()
    {
        var playerVm = new object();

        var shouldClose = MainViewModel.ShouldCloseInspectorOnRouteTransition(
            PageType.Library,
            PageType.Search,
            currentPanelVm: playerVm,
            playerPanelVm: playerVm);

        Assert.False(shouldClose);
    }

    [Fact]
    public void ShouldCloseInspectorOnRouteTransition_ReturnsFalse_WhenRouteDidNotChange()
    {
        var shouldClose = MainViewModel.ShouldCloseInspectorOnRouteTransition(
            PageType.Library,
            PageType.Library,
            currentPanelVm: new object(),
            playerPanelVm: new object());

        Assert.False(shouldClose);
    }

    [Fact]
    public void ShouldCloseInspectorOnRouteTransition_ReturnsTrue_WhenContextualInspectorPersistsAcrossRouteChange()
    {
        var shouldClose = MainViewModel.ShouldCloseInspectorOnRouteTransition(
            PageType.Library,
            PageType.Search,
            currentPanelVm: new object(),
            playerPanelVm: new object());

        Assert.True(shouldClose);
    }

    /// <summary>
    /// Regression coverage for a real bug: Zen Mode, Theater Mode, and the narrow-window
    /// auto-collapse (MainWindow.axaml.cs) all set IsNavigationCollapsed=true expecting the left
    /// nav sidebar to disappear, but nothing in MainWindow.axaml ever bound to that flag — the
    /// column's Width stayed at whatever NavSidebarWidth last was and MinWidth stayed a static 56,
    /// so the sidebar never actually hid under any of those three features. The fix moves the
    /// width transition into the IsNavigationCollapsed property setter itself so every caller gets
    /// correct behavior automatically, and MainWindow.axaml now binds the column's MinWidth/the
    /// nav Border's IsVisible to the same flag.
    /// </summary>
    private static MainViewModel CreateUninitializedMainViewModel()
        => (MainViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));

    [Fact]
    public void IsNavigationCollapsed_SetTrue_CollapsesSidebarWidthToZero()
    {
        var sut = CreateUninitializedMainViewModel();
        sut.NavSidebarWidth = 220;

        sut.IsNavigationCollapsed = true;

        Assert.Equal(0, sut.NavSidebarWidth);
    }

    [Fact]
    public void IsNavigationCollapsed_SetFalse_RestoresThePreCollapseWidth()
    {
        var sut = CreateUninitializedMainViewModel();
        sut.NavSidebarWidth = 220;

        sut.IsNavigationCollapsed = true;
        sut.IsNavigationCollapsed = false;

        Assert.Equal(220, sut.NavSidebarWidth);
    }

    [Fact]
    public void NavSidebarCycle_ExpandedToMiniToCollapsedAndBack_RoundTripsTheOriginalWidth()
    {
        // Simulates ToggleNavigationCommand's real 3-state cycle: Expanded(220) -> Mini(56) ->
        // Collapsed(0) -> Expanded again. The collapsed-width capture must not clobber the
        // remembered expanded width with the mini rail's 56px along the way.
        var sut = CreateUninitializedMainViewModel();
        sut.NavSidebarWidth = 220;

        sut.IsNavigationMini = true;
        Assert.Equal(56, sut.NavSidebarWidth);

        sut.IsNavigationCollapsed = true;
        Assert.Equal(0, sut.NavSidebarWidth);
        Assert.False(sut.IsNavigationMini, "Going fully hidden should clear the mini rail state too.");

        sut.IsNavigationCollapsed = false;
        Assert.Equal(220, sut.NavSidebarWidth);
    }

    [Fact]
    public void IsNavigationCollapsed_SetTrue_AlsoClearsMiniState()
    {
        var sut = CreateUninitializedMainViewModel();
        sut.NavSidebarWidth = 220;
        sut.IsNavigationMini = true;
        sut.NavSidebarWidth = 56;

        sut.IsNavigationCollapsed = true;

        Assert.False(sut.IsNavigationMini);
    }

    [Fact]
    public void IsNavigationCollapsed_SettingSameValueTwice_IsANoOp()
    {
        var sut = CreateUninitializedMainViewModel();
        sut.NavSidebarWidth = 220;

        sut.IsNavigationCollapsed = true;
        sut.NavSidebarWidth = 999; // simulate something else touching width while collapsed
        sut.IsNavigationCollapsed = true; // redundant set — must not re-run the transition

        Assert.Equal(999, sut.NavSidebarWidth);
    }

    private sealed class TestPage : global::Avalonia.Controls.UserControl;
}
