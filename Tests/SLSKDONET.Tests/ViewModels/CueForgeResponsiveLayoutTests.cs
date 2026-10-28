using SLSKDONET.ViewModels;
using Xunit;

namespace SLSKDONET.Tests.ViewModels;

public class CueForgeResponsiveLayoutTests
{
    [Theory]
    [InlineData(1400, false)]
    [InlineData(1000, false)]
    [InlineData(999, true)]
    [InlineData(640, true)]
    public void PanelMode_UsesTabsOnlyBelowCenterWidthThreshold(double width, bool expected)
    {
        Assert.Equal(expected, CueForgeResponsiveLayout.UseTabs(width));
    }

    [Fact]
    public void ShortWorkspace_AutoCollapsesOnce()
    {
        Assert.Equal(-1, CueForgeResponsiveLayout.HeightAction(699, wasAutomaticallyCollapsed: false));
        Assert.Equal(0, CueForgeResponsiveLayout.HeightAction(699, wasAutomaticallyCollapsed: true));
    }

    [Theory]
    [InlineData(700, 0)]
    [InlineData(759, 0)]
    [InlineData(760, 1)]
    [InlineData(1200, 1)]
    public void AutomaticCollapse_RestoresOnlyAfterHysteresisThreshold(double height, int expected)
    {
        Assert.Equal(expected, CueForgeResponsiveLayout.HeightAction(height, wasAutomaticallyCollapsed: true));
    }

    [Fact]
    public void ManualCollapse_IsNotUndoneByResize()
    {
        Assert.Equal(0, CueForgeResponsiveLayout.HeightAction(1200, wasAutomaticallyCollapsed: false));
    }
}
