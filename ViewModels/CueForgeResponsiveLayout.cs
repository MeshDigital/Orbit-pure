namespace SLSKDONET.ViewModels;

/// <summary>Size-threshold policy for Cue Forge; independent of render DPI or display scaling.</summary>
public static class CueForgeResponsiveLayout
{
    public const double TabbedWidthThreshold = 1000;
    public const double CollapseHeightThreshold = 700;
    public const double RestoreHeightThreshold = 760;

    public static bool UseTabs(double centerWidth) => centerWidth < TabbedWidthThreshold;

    /// <returns>-1 to auto-collapse, +1 to restore an automatic collapse, otherwise 0.</returns>
    public static int HeightAction(double availableHeight, bool wasAutomaticallyCollapsed)
    {
        if (availableHeight < CollapseHeightThreshold)
            return wasAutomaticallyCollapsed ? 0 : -1;
        return wasAutomaticallyCollapsed && availableHeight >= RestoreHeightThreshold ? 1 : 0;
    }
}
