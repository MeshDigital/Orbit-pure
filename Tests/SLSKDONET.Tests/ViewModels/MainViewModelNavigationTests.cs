using SLSKDONET.Models;
using SLSKDONET.Views;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class MainViewModelNavigationTests
{
    [Theory]
    [InlineData(typeof(SLSKDONET.Views.Avalonia.ImportPage), PageType.Import)]
    [InlineData(typeof(SLSKDONET.Views.Avalonia.ImportPreviewPage), PageType.Import)]
    [InlineData(typeof(SLSKDONET.Views.Avalonia.SearchPage), PageType.Search)]
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

    private sealed class TestPage : global::Avalonia.Controls.UserControl;
}
