using System.Linq;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SLSKDONET.Services;
using Xunit;

namespace SLSKDONET.Tests.Services.Navigation;

/// <summary>
/// DJ request 2026-11-28: "Home → genre → Hip Hop → track → analyse, there should be a back and
/// forward button with the recent page visits on hover".
/// </summary>
public sealed class PageHistoryTests
{
    private sealed class HomePage : UserControl { }
    private sealed class LibraryPage : UserControl { }
    private sealed class CueForgePage : UserControl { }
    private sealed class SettingsPage : UserControl { }

    private static NavigationService Nav()
    {
        var services = new ServiceCollection()
            .AddSingleton<HomePage>().AddSingleton<LibraryPage>().AddSingleton<CueForgePage>().AddSingleton<SettingsPage>()
            .BuildServiceProvider();
        var nav = new NavigationService(services, NullLogger<NavigationService>.Instance, new PerformanceTracker());
        nav.RegisterPage("Home", typeof(HomePage));
        nav.RegisterPage("Library", typeof(LibraryPage));
        nav.RegisterPage("CueForge", typeof(CueForgePage));
        nav.RegisterPage("Settings", typeof(SettingsPage));
        return nav;
    }

    [Fact]
    public void BackThenForward_RetracesTheVisits_AndReturnsTheSameCachedPages()
    {
        var nav = Nav();
        nav.NavigateTo("Home");
        nav.NavigateTo("Library");
        var library = nav.CurrentPage;
        nav.NavigateTo("CueForge");

        Assert.True(nav.CanGoBack);
        Assert.False(nav.CanGoForward);

        nav.GoBack();
        Assert.Same(library, nav.CurrentPage); // cached: filter and selection survive
        Assert.True(nav.CanGoForward);
        Assert.IsType<CueForgePage>(nav.ForwardHistory[0]);

        nav.GoForward();
        Assert.IsType<CueForgePage>(nav.CurrentPage);
        Assert.False(nav.CanGoForward);
        Assert.Equal(new[] { typeof(LibraryPage), typeof(HomePage) }, nav.BackHistory.Select(p => p.GetType()));
    }

    [Fact]
    public void NewVisitAfterBack_DropsTheForwardHistory()
    {
        var nav = Nav();
        nav.NavigateTo("Home");
        nav.NavigateTo("Library");
        nav.GoBack();

        nav.NavigateTo("Settings");

        Assert.False(nav.CanGoForward);
        Assert.Equal(new[] { typeof(HomePage) }, nav.BackHistory.Select(p => p.GetType()));
    }

    [Fact]
    public void JumpingBackSeveralPages_FromTheRecentList_KeepsEveryPassedPageForForward()
    {
        var nav = Nav();
        nav.NavigateTo("Home");
        nav.NavigateTo("Library");
        nav.NavigateTo("CueForge");
        nav.NavigateTo("Settings");

        nav.GoBack(3);

        Assert.IsType<HomePage>(nav.CurrentPage);
        Assert.False(nav.CanGoBack);
        Assert.Equal(new[] { typeof(LibraryPage), typeof(CueForgePage), typeof(SettingsPage) },
            nav.ForwardHistory.Select(p => p.GetType()));
    }

    [Fact]
    public void ReopeningTheCurrentPage_DoesNotAddAHistoryStep()
    {
        var nav = Nav();
        nav.NavigateTo("Home");
        nav.NavigateTo("Library");
        nav.NavigateTo("Library");

        Assert.Single(nav.BackHistory);
    }

    [Fact]
    public void HistoryIsBounded()
    {
        var nav = Nav();
        for (int i = 0; i < NavigationService.MaxHistory + 20; i++)
            nav.NavigateTo(i % 2 == 0 ? "Home" : "Library");

        Assert.Equal(NavigationService.MaxHistory, nav.BackHistory.Count);
    }

    [Fact]
    public void HistoryChanged_FiresOnEveryStep_SoTheButtonsUpdate()
    {
        var nav = Nav();
        int changes = 0;
        nav.HistoryChanged += (_, _) => changes++;
        nav.NavigateTo("Home");    // first page: nothing to go back to yet
        nav.NavigateTo("Library"); // 1
        nav.GoBack();              // 2
        nav.GoForward();           // 3
        Assert.Equal(3, changes);
    }
}
